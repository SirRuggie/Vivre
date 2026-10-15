using System.Diagnostics;

namespace Vivre.Core.Tests.IO;

/// <summary>Dummy local process used only to prove that persistence leases exclude another process.</summary>
internal sealed class CrossProcessLeaseHolder : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    private CrossProcessLeaseHolder(Process process) => _process = process;

    internal static CrossProcessLeaseHolder Start(string leasePath)
    {
        string escapedPath = leasePath.Replace("'", "''", StringComparison.Ordinal);
        string command =
            $"$s=[IO.File]::Open('{escapedPath}',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
            "[Console]::Out.WriteLine('ACQUIRED');[Console]::Out.Flush();[Console]::In.ReadLine() > $null;$s.Dispose()";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);

        Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start lease-holder process.");
        try
        {
            string? ready = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (ready != "ACQUIRED")
            {
                throw new InvalidOperationException($"Lease-holder process did not acquire the lease: {ready ?? "<no output>"}");
            }

            return new CrossProcessLeaseHolder(process);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
            catch { /* best effort cleanup of this test-only child */ }
            process.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_process.HasExited)
        {
            _process.Dispose();
            return;
        }

        _process.StandardInput.WriteLine();
        _process.StandardInput.Close();
        if (!_process.WaitForExit(10_000))
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
        _process.Dispose();
    }
}
