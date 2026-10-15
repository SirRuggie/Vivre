using System.Diagnostics;
using System.Text;
using Xunit;

namespace Vivre.Core.Tests.Scripts;

public sealed class BundledScriptBehaviorTests
{
    private static readonly string PowerShell = Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Theory]
    [InlineData("WMI repository is consistent", 0, 1, true, "No action needed")]
    [InlineData("WMI repository is inconsistent", 1358, 3, true, "verified consistent")]
    [InlineData("localized response", -2147023538, 3, true, "verified consistent")]
    [InlineData("provider failed", 5, 1, false, "verification failed")]
    [InlineData("localized or unknown", 0, 1, false, "unrecognized response")]
    public void Wmi_repair_classifies_results_without_live_wmi(
        string firstOutput, int firstCode, int callsExpected, bool succeeds, string expected)
    {
        string healthy = "WMI repository is consistent";
        string items = callsExpected == 3
            ? $"@(@{{Output='{Ps(firstOutput)}';Code={firstCode}}},@{{Output='salvaged';Code=0}},@{{Output='{healthy}';Code=0}})"
            : $"@(@{{Output='{Ps(firstOutput)}';Code={firstCode}}})";
        string body = $"$q=[Collections.Queue]::new(); {items}|%{{$q.Enqueue($_)}}; " +
            "$mock={param($a) $x=$q.Dequeue(); & cmd.exe /d /c \"exit /b $($x.Code)\"; [pscustomobject]@{Output=$x.Output;ExitCode=$LASTEXITCODE}}; " +
            $"& '{Ps(Script("Repair", "Rebuild WMI repository.ps1"))}' -Winmgmt $mock";

        Result result = Run(body);

        Assert.Equal(succeeds, result.ExitCode == 0);
        Assert.Contains(expected, result.All, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Count missing updates.ps1")]
    [InlineData("Download and install updates.ps1")]
    public void Wua_non_clean_search_never_reports_clean_inventory(string name)
    {
        string body = "$r=[pscustomobject]@{ResultCode=3;Updates=@()}; " +
            $"& '{Ps(Script("Windows Update", name))}' -SearchResult $r";

        Result result = Run(body);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("did not complete cleanly", result.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Missing updates: 0", result.All, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No applicable updates", result.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cache_reset_reports_failed_phase_and_successful_recovery()
    {
        string body = "$stop={}; $reset={throw 'rename denied'}; $start={'RECOVERED'}; " +
            $"& '{Ps(Script("Repair", "Restart Windows Update (reset cache).ps1"))}' " +
            "-StopServices $stop -ResetCache $reset -StartServices $start";

        Result result = Run(body);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("failed during reset cache", result.All, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RECOVERED", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_reset_reports_operation_and_recovery_failures_together()
    {
        string body = "$stop={throw 'stop denied'}; $reset={}; $start={throw 'start denied'}; " +
            $"& '{Ps(Script("Repair", "Restart Windows Update (reset cache).ps1"))}' " +
            "-StopServices $stop -ResetCache $reset -StartServices $start";

        Result result = Run(body);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("stop services", result.All, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("service recovery failed", result.All, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cache_reset_default_recovery_attempts_both_services_when_first_start_fails()
    {
        string body = "$global:started=[Collections.Generic.List[string]]::new(); " +
            "function Start-Service { param([string]$Name) $global:started.Add($Name); if($Name -eq 'wuauserv'){throw 'start denied'} }; " +
            "$stop={}; $reset={}; " +
            $"try {{ & '{Ps(Script("Repair", "Restart Windows Update (reset cache).ps1"))}' -StopServices $stop -ResetCache $reset }} " +
            "catch { [Console]::Error.WriteLine($_.Exception.Message) }; 'ATTEMPTS=' + ($global:started -join ',')";

        Result result = Run(body);

        Assert.Contains("service recovery failed", result.All, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wuauserv: start denied", result.All, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ATTEMPTS=wuauserv,bits", result.All, StringComparison.OrdinalIgnoreCase);
    }

    private static Result Run(string body)
    {
        string wrapper = Path.Combine(Path.GetTempPath(), $"vivre-script-test-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(wrapper, "$ErrorActionPreference='Stop'; try { " + body +
                " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }", new UTF8Encoding(true));
            var start = new ProcessStartInfo(PowerShell, $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{wrapper}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.Environment.Remove("PSModulePath");
            using Process process = Process.Start(start)!;
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(15_000), "PowerShell fixture timed out.");
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            return new Result(process.ExitCode, stdout, stderr);
        }
        finally
        {
            if (File.Exists(wrapper)) File.Delete(wrapper);
        }
    }

    private static string Script(string category, string name) => Path.Combine(RepoRoot(), "scripts", category, name);

    private static string RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CLAUDE.md"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public string All => StdOut + Environment.NewLine + StdErr;
    }
}
