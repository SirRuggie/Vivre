using System.Management.Automation;

namespace Vivre.Core.PowerShell;

public enum WinRmRetryOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed record WinRmRetryResult(WinRmRetryOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == WinRmRetryOutcome.Succeeded;
}

/// <summary>Explicitly verifies one WinRM connection without replaying an earlier operation.</summary>
public interface IWinRmRecovery
{
    Task<WinRmRetryResult> RetryWinRmAsync(
        string host,
        PSCredential? credential = null,
        int port = 5985,
        bool useSsl = false,
        CancellationToken cancellationToken = default);
}
