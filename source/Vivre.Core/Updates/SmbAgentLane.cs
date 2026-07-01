using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Vivre.Core.Logging;
using Vivre.Core.PowerShell;
using Vivre.Core.Remoting;

namespace Vivre.Core.Updates;

/// <summary>
/// The SMB + Service-Control-Manager update lane: how Vivre scans, installs, and uninstalls on hosts
/// that reject WinRM/Kerberos (0x80090322). It is the BatchPatch/PsExec model, driven entirely from the
/// operator's machine on the <b>current Windows login</b> (NTLM SSO — no Kerberos, no credential prompt):
///
/// <list type="number">
///   <item>Drop the signed agent EXE + its config to an Administrators/SYSTEM-only directory on the
///   target's admin share (<c>\\host\C$\ProgramData\Vivre\agent</c>), and SHA-256-verify the dropped EXE.</item>
///   <item>Create a one-shot LocalSystem service whose image is that EXE in <c>--service</c> mode and start
///   it (<see cref="RemoteServiceController"/>). The service-aware agent reports RUNNING immediately, so the
///   start never trips the SCM's 1053 timeout.</item>
///   <item>Tail the agent's append-only progress JSONL over SMB (the same shape the WinRM lane streams),
///   forwarding each line as a <see cref="HostPatchStatus"/>; a missed heartbeat for the no-response window
///   means the agent died/hung. A Scan also reads back the agent's JSON update array.</item>
///   <item>Tear down: stop → wait for stopped → DeleteService → delete the per-run drop files.</item>
/// </list>
///
/// <para>This lane is selected by <see cref="WuaUpdateLane"/> when a host raises
/// <see cref="KerberosWrongPrincipalException"/>; the operation result it produces is deliberately
/// indistinguishable from a WinRM run (the Kerberos degradation is surfaced only through Vitals, never on
/// an operation result). It runs on the ambient identity only, so any alternate credential is ignored — the
/// whole reason this path exists is that the ambient login works over SMB/DCOM where Kerberos does not.</para>
/// </summary>
public sealed class SmbAgentLane : ISmbAgentLane
{
    // Target-local drop directory (Administrators/SYSTEM-only) — shared with the WinRM lane so both
    // drop to the same hardened location. Per-run filenames isolate concurrent runs against the same
    // host so one run's teardown never disturbs another's still-locked EXE.
    private const string DropDirLocal = WuaUpdateLane.AgentDropDir;

    private readonly Func<byte[]> _agentBytes;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _startupGrace;
    private readonly IActivityLog? _activity;

    /// <param name="agentBytesProvider">Supplies the compiled, signed agent EXE bytes (the same provider
    /// the WinRM lane uses, so both lanes drop byte-identical, integrity-verifiable agents).</param>
    /// <param name="pollInterval">How often the progress tail reads new bytes. Default 500ms; tests shrink it.</param>
    /// <param name="startupGrace">How long to wait for the agent to begin writing progress before declaring
    /// it never launched. Default 2 minutes (matches the WinRM bootstrap).</param>
    /// <param name="activityLog">Optional sink for the best-effort helper-service teardown: a failed teardown
    /// is logged (WARN) here rather than swallowed, but never fails the operation. Null in tests.</param>
    public SmbAgentLane(Func<byte[]> agentBytesProvider, TimeSpan? pollInterval = null, TimeSpan? startupGrace = null, IActivityLog? activityLog = null)
    {
        _agentBytes = agentBytesProvider ?? throw new ArgumentNullException(nameof(agentBytesProvider));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _startupGrace = startupGrace ?? TimeSpan.FromMinutes(2);
        _activity = activityLog;
    }

    // --- public lane operations -------------------------------------------

    public async Task<HostPatchStatus> ScanAsync(
        string host, PatchOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);

        bool installedScope = options.Scope == UpdateScope.Installed;
        string scope = installedScope ? "Installed" : "Applicable";

        try
        {
            SmbRunOutcome outcome = await RunAgentAsync(
                host, "Scan", scope, options, progress: null,
                startingMessage: installedScope ? "Scanning installed updates…" : "Scanning for updates…",
                cancellationToken).ConfigureAwait(false);

            return BuildScanStatus(
                outcome.Last.Phase == PatchPhase.Error, outcome.Last.Message, outcome.ScanResultJson,
                installedScope, options.ExcludeNameContains);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HostPatchStatus.Failed($"Scan failed on {host}: {ex.Message}");
        }
    }

    public Task<HostPatchStatus> InstallAsync(
        string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken) =>
        RunOperationAsync(host, "Install", options, progress, "Starting update task…", cancellationToken);

    public Task<HostPatchStatus> UninstallAsync(
        string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken) =>
        RunOperationAsync(host, "Uninstall", options, progress, "Starting uninstall task…", cancellationToken);

    public Task<HostPatchStatus> RunComponentCleanupAsync(
        string host, PatchOptions options, IProgress<HostPatchStatus> progress, CancellationToken cancellationToken) =>
        RunOperationAsync(host, "Cleanup", options, progress, "Cleaning the component store…", cancellationToken);

    /// <summary>
    /// The 2016 full-package LCU path: copies the CU .msu from <paramref name="sourcePackagePath"/> (a
    /// controller-local file, e.g. the package directory) into the target's hardened drop dir, then runs
    /// the agent in AddPackage mode to DISM-add it as SYSTEM. The terminal status is "PendingReboot"
    /// (staged — reboot-ready) on success, Done (already current) for a no-op, or Failed. The copied
    /// package is deleted on teardown. Stages only — the operator commits later via a Reboot Wave.
    /// </summary>
    public async Task<HostPatchStatus> InstallFullPackageAsync(
        string host, string sourcePackagePath, PatchOptions options,
        IProgress<HostPatchStatus> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackagePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            SmbRunOutcome outcome = await RunAgentAsync(
                host, "AddPackage", scope: null, options, progress, "Staging cumulative update…",
                cancellationToken, payload: (sourcePackagePath, Path.GetFileName(sourcePackagePath)))
                .ConfigureAwait(false);
            return outcome.Last;
        }
        catch (OperationCanceledException)
        {
            progress.Report(new HostPatchStatus(PatchPhase.Idle, "Cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            var failed = HostPatchStatus.Failed($"Staging failed on {host}: {ex.Message}");
            progress.Report(failed);
            return failed;
        }
    }

    private async Task<HostPatchStatus> RunOperationAsync(
        string host, string mode, PatchOptions options, IProgress<HostPatchStatus> progress,
        string startingMessage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        // Scheduling rides the WinRM lane's scheduled task; the SMB lane runs now. Surface it plainly
        // (without revealing transport) rather than silently running a "scheduled" install immediately.
        if (options.RunBehavior == RunBehavior.ScheduleAt)
        {
            var notScheduled = HostPatchStatus.Failed(
                "Scheduling isn't available for this host yet — run the update now instead.");
            progress.Report(notScheduled);
            return notScheduled;
        }

        try
        {
            SmbRunOutcome outcome = await RunAgentAsync(
                host, mode, scope: null, options, progress, startingMessage, cancellationToken).ConfigureAwait(false);
            return outcome.Last;
        }
        catch (OperationCanceledException)
        {
            progress.Report(new HostPatchStatus(PatchPhase.Idle, "Cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            var failed = HostPatchStatus.Failed($"Update failed on {host}: {ex.Message}");
            progress.Report(failed);
            return failed;
        }
    }

    // --- core: drop → launch → tail → teardown ----------------------------

    private async Task<SmbRunOutcome> RunAgentAsync(
        string host, string mode, string? scope, PatchOptions options,
        IProgress<HostPatchStatus>? progress, string startingMessage, CancellationToken cancellationToken,
        (string SourcePath, string FileName)? payload = null)
    {
        if (HostName.IsLocal(host))
        {
            // The SMB lane exists only to reach a *remote* Kerberos-broken box; a local run has no
            // Kerberos problem and no admin share to itself. The selector never routes local here.
            throw new InvalidOperationException("The SMB agent lane is for remote hosts only.");
        }

        progress?.Report(new HostPatchStatus(PatchPhase.Scanning, startingMessage));

        string runId = Guid.NewGuid().ToString("N");
        string serviceName = $"Vivre_WUA_{runId}";
        bool isScan = string.Equals(mode, "Scan", StringComparison.OrdinalIgnoreCase);

        // Target-local paths (what the agent and the service binPath see).
        string localExe = $@"{DropDirLocal}\{serviceName}.exe";
        string localConfig = $@"{DropDirLocal}\{serviceName}_config.json";
        string localProgress = $@"{DropDirLocal}\{serviceName}_progress.json";
        string localResult = isScan ? $@"{DropDirLocal}\{serviceName}_scan.json" : null!;

        // Admin-share UNC paths (what we read/write from this machine).
        string uncDir = ToAdminShareUnc(host, DropDirLocal);
        string uncExe = ToAdminShareUnc(host, localExe);
        string uncConfig = ToAdminShareUnc(host, localConfig);
        string uncProgress = ToAdminShareUnc(host, localProgress);
        string uncResult = isScan ? ToAdminShareUnc(host, localResult) : null!;

        // AddPackage (the 2016 LCU lane): the full CU .msu the controller copies into the drop dir for the
        // agent to DISM-add. Kept in the same per-run drop dir and deleted on teardown like the run files.
        string? localPackage = payload is { } pl ? $@"{DropDirLocal}\{pl.FileName}" : null;
        string? uncPackage = localPackage is not null ? ToAdminShareUnc(host, localPackage) : null;

        byte[] agentBytes = _agentBytes();
        string expectedSha = Convert.ToHexString(SHA256.HashData(agentBytes));
        string configJson = WuaUpdateLane.BuildAgentConfigJson(
            options, localProgress, mode, scope, isScan ? localResult : null, localPackage);

        RemoteServiceController? service = null;
        try
        {
            // 1) Drop + verify the agent, and (for AddPackage) copy the CU .msu. This is heavy synchronous
            // SMB I/O — a 1.7 GB CU copy can take ~90s — so run it on a background thread. Doing it inline
            // would block whatever thread started the sweep; when a free throttle slot lets the chain run
            // synchronously that thread is the UI thread, and the app freezes for the whole copy. (Progress
            // reports still marshal back via IProgress; ConfigureAwait(false) is fine here — the result
            // writes the caller binds to happen in its own continuation, which keeps the UI context.)
            await Task.Run(() =>
            {
                EnsureHardenedDir(uncDir);
                CleanStaleRunFiles(uncProgress, uncResult);
                File.WriteAllBytes(uncExe, agentBytes);
                File.WriteAllText(uncConfig, configJson, new UTF8Encoding(false));
                VerifyDroppedExe(uncExe, expectedSha);

                // 1b) AddPackage: copy the full CU .msu into the hardened dir (the big SMB copy) and confirm
                // the bytes landed before the agent DISM-adds it. A short copy = a security agent or a
                // network blip mid-transfer; surface it rather than staging a truncated package.
                if (payload is { } pkg && uncPackage is not null)
                {
                    progress?.Report(new HostPatchStatus(PatchPhase.Scanning, "Copying package to host…"));
                    CopyPackage(pkg.SourcePath, uncPackage);
                }
            }, cancellationToken).ConfigureAwait(false);

            // 2) Create the LocalSystem service and start it. The display name is per-run too: the SCM
            // rejects a duplicate display name (not just a duplicate service name), and concurrent runs
            // against one host (e.g. unserialized Applicable scans) would otherwise collide.
            string binPath = $"\"{localExe}\" --service \"{localConfig}\"";
            service = RemoteServiceController.Create(host, serviceName, $"Vivre Update Agent {runId}", binPath);
            service.Start();

            // 3) Tail the progress (and, for a scan, the result file) until terminal / silence / death.
            return await TailAsync(
                host, service, uncProgress, isScan ? uncResult : null, isScan, options, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Always tear down: stop (the agent usually self-stopped) → wait → delete service → delete
            // files. Runs on success, cancel, and fault — a leftover service/file is the one thing worth
            // never leaving behind.
            if (service is not null)
            {
                await TeardownServiceAsync(service, host, serviceName).ConfigureAwait(false);
                service.Dispose();
            }

            DeleteRunFiles(uncExe, uncConfig, uncProgress, uncResult);
            if (uncPackage is not null)
            {
                TryDelete(uncPackage); // don't leave a 1.7 GB .msu behind on the box
            }
        }
    }

    /// <summary>
    /// Position-tracked read of the agent's progress JSONL over SMB. Forwards each parsed line to
    /// <paramref name="progress"/> (ignoring Heartbeat lines, which only prove liveness), stops on a
    /// terminal phase, and guards two failure modes: total silence past the no-response window (dead/hung),
    /// and the service exiting without ever reporting a terminal line (crashed on launch). Returns the last
    /// status plus, for a scan, the JSON the agent wrote (read before teardown deletes it).
    /// </summary>
    private async Task<SmbRunOutcome> TailAsync(
        string host, RemoteServiceController service, string uncProgress, string? uncResult, bool isScan,
        PatchOptions options, IProgress<HostPatchStatus>? progress, CancellationToken cancellationToken)
    {
        HostPatchStatus last = new(PatchPhase.Scanning, "Working…");
        long position = 0;
        DateTime startedUtc = DateTime.UtcNow;
        DateTime lastActivityUtc = startedUtc;
        bool progressSeen = false;
        bool terminal = false;

        while (!terminal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);

            foreach (string line in ReadNewLines(uncProgress, ref position))
            {
                lastActivityUtc = DateTime.UtcNow;

                // Heartbeat proves the agent is alive but must not change the displayed phase. (The agent's
                // serializer always emits compact JSON, so only the compact form is checked — matching the
                // drain loop below and the WinRM lane.)
                if (line.Contains("\"phase\":\"Heartbeat\"", StringComparison.Ordinal))
                {
                    continue;
                }

                if (WuaUpdateLane.TryParseProgress(line, out HostPatchStatus parsed))
                {
                    last = parsed;
                    progressSeen = true;
                    progress?.Report(parsed);
                    if (IsTerminal(parsed.Phase))
                    {
                        terminal = true;
                    }
                }
            }

            if (terminal)
            {
                break;
            }

            // The service exited. Drain any final lines once more; if still no terminal line, the agent
            // died without reporting a result.
            if (service.Query() == RemoteServiceState.Stopped)
            {
                foreach (string line in ReadNewLines(uncProgress, ref position))
                {
                    if (line.Contains("\"phase\":\"Heartbeat\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (WuaUpdateLane.TryParseProgress(line, out HostPatchStatus parsed))
                    {
                        last = parsed;
                        progressSeen = true;
                        progress?.Report(parsed);
                        if (IsTerminal(parsed.Phase))
                        {
                            terminal = true;
                        }
                    }
                }

                if (!terminal)
                {
                    last = HostPatchStatus.Failed(
                        $"The update agent on {host} stopped without reporting a result.");
                    progress?.Report(last);
                    break;
                }

                break;
            }

            // No progress file ever appeared within the startup grace — the agent never got going.
            if (!progressSeen && DateTime.UtcNow - startedUtc > _startupGrace)
            {
                last = HostPatchStatus.Failed(
                    $"The update agent on {host} did not start within {_startupGrace.TotalMinutes:N0} minutes.");
                progress?.Report(last);
                break;
            }

            // Total silence (no progress AND no heartbeat) past the no-response window — dead or hung.
            if (DateTime.UtcNow - lastActivityUtc > options.NoResponseTimeout)
            {
                last = HostPatchStatus.Failed(
                    $"No response from {host} for {options.NoResponseTimeout.TotalSeconds:N0}s — the update agent stopped sending progress (it appears dead or hung). Re-scan once it's back.");
                progress?.Report(last);
                break;
            }
        }

        // For a scan, capture the result JSON before teardown removes it.
        string? scanJson = null;
        if (isScan && uncResult is not null && terminal && last.Phase != PatchPhase.Error)
        {
            scanJson = TryReadAllText(uncResult);
        }

        return new SmbRunOutcome(last, scanJson);
    }

    private async Task TeardownServiceAsync(RemoteServiceController service, string host, string serviceName)
    {
        try
        {
            service.TryStop();

            // Wait (bounded) for the process to exit so the EXE unlocks before we delete the files.
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                RemoteServiceState state = service.Query();
                if (state is RemoteServiceState.Stopped or RemoteServiceState.Unknown)
                {
                    break;
                }

                await Task.Delay(300).ConfigureAwait(false);
            }

            service.Delete();
        }
        catch (Exception ex)
        {
            // Teardown is best-effort: a per-run service name means a leftover is harmless and a re-run
            // reaps it. Don't fold this into the operation result (the caller already has its outcome) and
            // don't rethrow — but surface it to the activity log + rolling file (WARN, not ERROR) so a
            // persistent leftover (a stuck Vivre_WUA_* service, a DeleteService denial) is visible instead
            // of vanishing. Debug.WriteLine here was stripped from Release, so the failure had no trace.
            _activity?.Warn(host, $"SMB helper-service teardown incomplete — '{serviceName}' may be left behind (harmless; the per-run name is reaped on the next run): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // --- SMB file helpers --------------------------------------------------

    /// <summary>Creates the drop directory (if absent) with an Administrators/SYSTEM-only ACL and no
    /// inheritance, so a non-privileged local user can neither plant nor swap the binary we run as SYSTEM
    /// (closing the world-writable-temp TOCTOU). Re-applies the ACL if the dir already exists.</summary>
    private static void EnsureHardenedDir(string uncDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            // The whole app is Windows-only; this branch only exists so the Windows-specific ACL APIs
            // below satisfy the platform-compatibility analyzer without an annotation cascade.
            Directory.CreateDirectory(uncDir);
            return;
        }

        var security = new DirectorySecurity();
        // Break inheritance and drop inherited ACEs (e.g. the ProgramData "Users may create files" ACE).
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        var di = new DirectoryInfo(uncDir);
        if (di.Exists)
        {
            di.SetAccessControl(security);
        }
        else
        {
            di.Create(security);
        }
    }

    private static void VerifyDroppedExe(string uncExe, string expectedSha)
    {
        byte[] onDisk = File.ReadAllBytes(uncExe);
        string actual = Convert.ToHexString(SHA256.HashData(onDisk));
        if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(uncExe); } catch { /* the launch is aborted regardless */ }
            throw new InvalidOperationException(
                "The agent failed its integrity check on the target (the dropped EXE did not match what was shipped); aborting to avoid running a tampered binary as SYSTEM.");
        }
    }

    /// <summary>Copies the CU package (controller-local) to the target's drop dir over SMB and confirms the
    /// full byte count landed — a short copy means a security agent or a network blip cut it off, and a
    /// truncated 1.7 GB .msu must never be handed to DISM.</summary>
    private static void CopyPackage(string sourcePath, string uncDest)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Update package not found: {sourcePath}", sourcePath);
        }

        File.Copy(sourcePath, uncDest, overwrite: true);

        long src = new FileInfo(sourcePath).Length;
        long dst = new FileInfo(uncDest).Length;
        if (dst != src)
        {
            throw new IOException($"Package copy was incomplete — {dst:N0} of {src:N0} bytes reached the target.");
        }
    }

    private static void CleanStaleRunFiles(string uncProgress, string? uncResult)
    {
        TryDelete(uncProgress);
        if (uncResult is not null)
        {
            TryDelete(uncResult);
        }
    }

    /// <summary>Reads any bytes appended to the progress file since <paramref name="position"/>, advancing
    /// it. Shared read/write so the agent can keep appending. A transient sharing/IO blip yields no lines
    /// this tick (the next tick re-reads from the same position).</summary>
    private static IReadOnlyList<string> ReadNewLines(string uncProgress, ref long position)
    {
        try
        {
            if (!File.Exists(uncProgress))
            {
                return [];
            }

            using var fs = new FileStream(uncProgress, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= position)
            {
                return [];
            }

            fs.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string text = reader.ReadToEnd();
            position = fs.Length;

            string[] split = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return split.Length == 0 ? [] : split;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryReadAllText(string uncPath)
    {
        try
        {
            return File.Exists(uncPath) ? File.ReadAllText(uncPath, Encoding.UTF8) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteRunFiles(string uncExe, string uncConfig, string uncProgress, string? uncResult)
    {
        // The EXE can be briefly locked while the just-stopped service process finishes exiting; retry it.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(uncExe))
                {
                    break;
                }

                File.Delete(uncExe);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }

        TryDelete(uncConfig);
        TryDelete(uncProgress);
        if (uncResult is not null)
        {
            TryDelete(uncResult);
        }
    }

    private static void TryDelete(string uncPath)
    {
        try
        {
            if (File.Exists(uncPath))
            {
                File.Delete(uncPath);
            }
        }
        catch (IOException)
        {
            // A leftover per-run file is harmless; it's overwritten/cleaned on the next run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // --- pure helpers (host-free, unit-tested) ----------------------------

    /// <summary>Maps a drive-rooted local path (<c>C:\ProgramData\…</c>) to its admin-share UNC on
    /// <paramref name="host"/> (<c>\\host\C$\ProgramData\…</c>).</summary>
    public static string ToAdminShareUnc(string host, string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (localPath.Length < 3 || localPath[1] != ':' || (localPath[2] != '\\' && localPath[2] != '/'))
        {
            throw new ArgumentException($"'{localPath}' is not a drive-rooted local path.", nameof(localPath));
        }

        char drive = localPath[0];
        string rest = localPath[3..].Replace('/', '\\').TrimStart('\\');
        return $@"\\{host}\{drive}$\{rest}";
    }

    /// <summary>
    /// Builds the scan status from the agent-run outcome. An <b>Error</b> outcome — a search that threw,
    /// OR (the second face) that returned without throwing but did not cleanly succeed and so the agent
    /// wrote an error line — is surfaced as a FAILURE and can NEVER read "up to date"; only a clean run
    /// with a parsed result list reports availability. Pure so the no-false-green rule is unit-tested.
    /// </summary>
    public static HostPatchStatus BuildScanStatus(
        bool isErrorOutcome, string outcomeMessage, string? scanResultJson,
        bool installedScope, IReadOnlyList<string> excludes)
    {
        // A failed scan is a failure — it must never fall through to the "up to date" / "no updates" path.
        if (isErrorOutcome)
        {
            return HostPatchStatus.Failed(outcomeMessage);
        }

        if (scanResultJson is null)
        {
            return HostPatchStatus.Failed("Scan returned no data.");
        }

        IReadOnlyList<SoftwareUpdate> updates = ParseScanResultJson(scanResultJson);
        updates = WuaUpdateLane.ApplyExclude(updates, excludes);

        string message = installedScope
            ? (updates.Count == 0 ? "No installed updates" : $"{updates.Count} installed update{(updates.Count == 1 ? "" : "s")}")
            : (updates.Count == 0 ? "Up to date" : $"{updates.Count} update{(updates.Count == 1 ? "" : "s")} available");

        return new HostPatchStatus(PatchPhase.Available, message, AvailableCount: updates.Count)
        {
            Updates = updates,
        };
    }

    /// <summary>Parses the agent's JSON scan array
    /// (Title/KB/IsDownloaded/MinSizeBytes/MaxSizeBytes/IsUninstallable/InstalledAt) into typed updates,
    /// mirroring <see cref="WuaUpdateLane.ParseScan"/>. Skips rows with no title.</summary>
    public static IReadOnlyList<SoftwareUpdate> ParseScanResultJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var list = new List<SoftwareUpdate>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? title = GetString(row, "Title");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            list.Add(new SoftwareUpdate(
                Title: title!,
                ArticleId: GetString(row, "KB"),
                IsDownloaded: GetBool(row, "IsDownloaded"),
                MinDownloadSizeBytes: GetInt64(row, "MinSizeBytes"),
                MaxDownloadSizeBytes: GetInt64(row, "MaxSizeBytes"),
                IsUninstallable: GetBoolOr(row, "IsUninstallable", fallback: true),
                InstalledAt: GetDateTime(row, "InstalledAt")));
        }

        return list;
    }

    private static bool IsTerminal(PatchPhase phase) =>
        // Deferred is a terminal servicing-busy refusal (the agent didn't start because a reboot is
        // already pending) — the tail must stop on it like any other terminal phase.
        phase is PatchPhase.Done or PatchPhase.Error or PatchPhase.PendingReboot or PatchPhase.Deferred;

    private static string? GetString(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool GetBool(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.True;

    private static bool GetBoolOr(JsonElement row, string name, bool fallback) =>
        row.TryGetProperty(name, out JsonElement el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : fallback;

    private static long GetInt64(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long n)
            ? n
            : 0L;

    private static DateTime? GetDateTime(JsonElement row, string name)
    {
        string? raw = GetString(row, name);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dt)
            ? dt
            : null;
    }

    /// <summary>The agent's last reported status, plus the raw scan JSON (Scan mode only).</summary>
    private sealed record SmbRunOutcome(HostPatchStatus Last, string? ScanResultJson);
}
