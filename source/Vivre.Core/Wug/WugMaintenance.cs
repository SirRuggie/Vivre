using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Vivre.Core.Wug;

/// <summary>The outcome of a WhatsUp Gold maintenance-mode run.</summary>
/// <param name="Ok">True when maintenance was set on at least one device.</param>
/// <param name="DevicesSet">How many WUG devices were put into / taken out of maintenance.</param>
/// <param name="Unmatched">Machine names that didn't map to a WUG device (by IP).</param>
/// <param name="Error">A human-readable failure reason, or null on success.</param>
public sealed record WugMaintenanceResult(
    bool Ok,
    int DevicesSet,
    IReadOnlyList<string> Unmatched,
    string? Error);

/// <summary>
/// Sets WhatsUp Gold maintenance mode for a set of machines via the <c>WhatsUpGoldPS</c> module — an
/// adaptation of the admin's standalone script, supplied the tab's machine names instead of a file.
///
/// <para><b>Runs under Windows PowerShell 5.1</b> (<c>powershell.exe</c>), NOT Vivre's embedded
/// PowerShell 7: <c>WhatsUpGoldPS</c> is a Windows-PowerShell module and PS7 either can't find it on
/// its own module path or drags it through a Windows-PowerShell compatibility session over local
/// WinRM (which hangs on a flaky box). Shelling out to 5.1 with <c>-NonInteractive</c> matches the
/// proven environment, can never hang on a prompt, and is bounded by a hard timeout. The WUG password
/// is passed via a child-process environment variable — never on the command line, never to disk.</para>
/// </summary>
public static class WugMaintenance
{
    // The on-host run, reading its inputs from environment variables (so the password never appears
    // on a command line). Emits a single JSON result object: { ok, devicesSet, unmatched[], error }.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $result = [ordered]@{ ok = $false; devicesSet = 0; unmatched = @(); error = $null }
        function Emit($r) { $r | ConvertTo-Json -Compress -Depth 4 }
        # Live step markers the host reads off stdout and pushes to the activity log, so a slow run
        # shows what it's doing (and, on timeout, the last step reached) instead of a silent wait.
        function Progress($m) { Write-Output "__WUGP__$m" }

        $names  = @($env:VIVRE_WUG_NAMES -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $server = $env:VIVRE_WUG_SERVER
        $enable = $env:VIVRE_WUG_ENABLE -eq '1'
        $reason = $env:VIVRE_WUG_REASON
        try {
            $sec  = ConvertTo-SecureString $env:VIVRE_WUG_PASS -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($env:VIVRE_WUG_USER, $sec)
        } catch {
            $result.error = "Couldn't build the WhatsUp Gold credential: $($_.Exception.Message)."
            Emit $result; return
        }

        # 1. Ensure the WhatsUpGoldPS module: import, else install for the current user, else explain.
        try {
            Progress 'Loading the WhatsUpGoldPS module...'
            if (-not (Get-Module -ListAvailable -Name WhatsUpGoldPS)) {
                # PowerShell Gallery requires TLS 1.2; 5.1 doesn't always negotiate it by default, and
                # without this Install-Module can hang on the handshake until the caller's timeout kills it.
                try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
                try {
                    if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
                        Install-PackageProvider -Name NuGet -Scope CurrentUser -Force -ErrorAction Stop | Out-Null
                    }
                    if ((Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue).InstallationPolicy -ne 'Trusted') {
                        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
                    }
                    Install-Module -Name WhatsUpGoldPS -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
                } catch {
                    $result.error = "The WhatsUpGoldPS module isn't installed and auto-install failed: $($_.Exception.Message). Install it once: Install-Module WhatsUpGoldPS -Scope CurrentUser (needs PSGallery/internet access)."
                    Emit $result; return
                }
            }
            Import-Module WhatsUpGoldPS -ErrorAction Stop
        } catch {
            $result.error = "Couldn't load the WhatsUpGoldPS module: $($_.Exception.Message)."
            Emit $result; return
        }

        # 2. Connect to the WUG server (HTTPS, ignoring the cert as the standalone script does).
        try {
            Progress "Connecting to WhatsUp Gold at $server..."
            Connect-WUGServer -ServerUri $server -Protocol https -Credential $cred -IgnoreSSLErrors -ErrorAction Stop | Out-Null
        } catch {
            $result.error = "Couldn't connect to WhatsUp Gold at $server`: $($_.Exception.Message). Check the address, that the server is reachable, and the WUG username/password."
            Emit $result; return
        }

        # 3. Resolve each machine to its WUG DeviceId with a targeted SearchValue lookup - one call per
        #    entry, NOT a full inventory pull (which is slow on a large WUG install). SearchValue takes a
        #    hostname or IP; on a miss for a name we DNS-resolve and retry by IP. Mirrors the proven
        #    standalone Set-WUGMaintenanceMode_SearchValue script.
        $deviceIds = @()
        $unmatched = @()
        $total = $names.Count
        $i = 0
        try {
            foreach ($srv in $names) {
                $i++
                Progress "Looking up $srv ($i of $total)..."
                $match = $null

                $results = @(Get-WUGDevice -SearchValue $srv -View overview -ErrorAction SilentlyContinue)
                if ($results.Count -gt 0) {
                    # Prefer an exact hit on name or IP; fall back to the first result.
                    $match = $results | Where-Object { $_.displayName -eq $srv -or $_.networkAddress -eq $srv } | Select-Object -First 1
                    if (-not $match) { $match = $results[0] }
                }
                elseif ($srv -notmatch '^(?:\d{1,3}\.){3}\d{1,3}$') {
                    # A name with no direct hit - resolve to IP and search again by address.
                    try {
                        $ip = [System.Net.Dns]::GetHostAddresses($srv) |
                              Where-Object AddressFamily -eq 'InterNetwork' |
                              Select-Object -First 1 -ExpandProperty IPAddressToString
                    } catch { $ip = $null }
                    if ($ip) {
                        Progress "  $srv resolved to $ip, retrying..."
                        $results2 = @(Get-WUGDevice -SearchValue $ip -View overview -ErrorAction SilentlyContinue)
                        if ($results2.Count -gt 0) {
                            $match = $results2 | Where-Object { $_.networkAddress -eq $ip } | Select-Object -First 1
                            if (-not $match) { $match = $results2[0] }
                        }
                    }
                }

                if ($null -ne $match) { $deviceIds += $match.id } else { $unmatched += $srv }
            }
        } catch {
            $result.error = "Connected, but couldn't search WhatsUp Gold devices: $($_.Exception.Message)."
            Emit $result; return
        }

        $result.unmatched = @($unmatched)

        if ($deviceIds.Count -eq 0) {
            $result.error = "None of the $($names.Count) machine(s) matched a WhatsUp Gold device. Unmatched: $($unmatched -join ', ')."
            Emit $result; return
        }

        # 4. Set maintenance mode.
        try {
            Progress "Setting maintenance for $($deviceIds.Count) device(s)..."
            Set-WUGDeviceMaintenance -DeviceId $deviceIds -Enabled $enable -Reason $reason -ErrorAction Stop | Out-Null
            $result.ok = $true
            $result.devicesSet = $deviceIds.Count
        } catch {
            $result.error = "Mapped $($deviceIds.Count) device(s), but Set-WUGDeviceMaintenance failed: $($_.Exception.Message)."
        }

        Emit $result
        """;

    // Prefix the on-host script puts on each live status line (see the script's Progress helper).
    private const string ProgressMarker = "__WUGP__";

    /// <summary>
    /// Runs the maintenance set under Windows PowerShell 5.1 and returns a typed result. Bounded by
    /// <paramref name="timeout"/> so it can never hang indefinitely. <paramref name="progress"/>, if
    /// supplied, receives a line per step (loading module, connecting, listing devices, setting) so a
    /// slow run can show what it's doing and, on timeout, the last step it reached.
    /// </summary>
    public static async Task<WugMaintenanceResult> RunAsync(
        IReadOnlyList<string> names,
        bool enable,
        string server,
        string username,
        string password,
        string reason,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        string psExe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(psExe))
        {
            return new WugMaintenanceResult(false, 0, [], $"Windows PowerShell wasn't found at {psExe}.");
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"Vivre_Wug_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, Script, cancellationToken).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo(psExe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Inputs via env vars (the password is never on the command line / in process args).
            psi.Environment["VIVRE_WUG_NAMES"] = string.Join("\n", names);
            psi.Environment["VIVRE_WUG_SERVER"] = server;
            psi.Environment["VIVRE_WUG_ENABLE"] = enable ? "1" : "0";
            psi.Environment["VIVRE_WUG_REASON"] = reason ?? string.Empty;
            psi.Environment["VIVRE_WUG_USER"] = username;
            psi.Environment["VIVRE_WUG_PASS"] = password;

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                // Live status lines go to the progress callback; everything else (incl. the final
                // JSON result) is buffered for parsing once the process exits.
                if (e.Data.StartsWith(ProgressMarker, StringComparison.Ordinal))
                {
                    progress?.Report(e.Data[ProgressMarker.Length..]);
                }
                else
                {
                    stdout.AppendLine(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            if (!proc.Start())
            {
                return new WugMaintenanceResult(false, 0, [], "Couldn't start Windows PowerShell (powershell.exe).");
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new WugMaintenanceResult(false, 0, [],
                    $"Timed out after {timeout.TotalSeconds:N0}s. The WhatsUpGoldPS module may be missing/incompatible on this machine, or {server} isn't reachable. Run your standalone script once here to confirm the module + connectivity.");
            }

            return Parse(stdout.ToString(), stderr.ToString());
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>Parses the single JSON result line the script emits; falls back to stderr on failure.</summary>
    internal static WugMaintenanceResult Parse(string stdout, string stderr)
    {
        string? json = stdout
            .Split('\n')
            .Select(s => s.Trim())
            .LastOrDefault(s => s.Contains('{') && s.Contains('}'));

        if (json is null)
        {
            string detail = !string.IsNullOrWhiteSpace(stderr)
                ? stderr.Trim()
                : "Windows PowerShell returned no result.";
            return new WugMaintenanceResult(false, 0, [], $"No result from WhatsUp Gold — {detail}");
        }

        try
        {
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            using JsonDocument doc = JsonDocument.Parse(json[start..(end + 1)]);
            JsonElement root = doc.RootElement;

            bool ok = root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
            int set = root.TryGetProperty("devicesSet", out JsonElement dEl) && dEl.TryGetInt32(out int d) ? d : 0;
            string? error = root.TryGetProperty("error", out JsonElement eEl) && eEl.ValueKind == JsonValueKind.String
                ? eEl.GetString()
                : null;

            var unmatched = new List<string>();
            if (root.TryGetProperty("unmatched", out JsonElement uEl))
            {
                if (uEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in uEl.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            unmatched.Add(item.GetString()!);
                        }
                    }
                }
                else if (uEl.ValueKind == JsonValueKind.String)
                {
                    unmatched.Add(uEl.GetString()!);
                }
            }

            return new WugMaintenanceResult(ok, set, unmatched, error);
        }
        catch (JsonException)
        {
            return new WugMaintenanceResult(false, 0, [], "Couldn't parse the WhatsUp Gold result: " + json);
        }
    }
}
