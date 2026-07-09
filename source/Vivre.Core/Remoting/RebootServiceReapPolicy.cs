namespace Vivre.Core.Remoting;

/// <summary>
/// Decides whether an SCM service found on a target is a reapable orphan of the SMB/SCM reboot
/// fallback (see DcomRebootTrigger.RebootViaSmbScm — its best-effort delete can lose the race with
/// the reboot, leaving a Stopped demand-start Vivre_Reboot_&lt;guid&gt; service behind). Pure so this
/// reboot-adjacent DELETE decision is unit-tested; for a deleter the dangerous direction is a false
/// POSITIVE, so the match is exact and anchored — never Contains.
/// </summary>
public static class RebootServiceReapPolicy
{
    private const string Prefix = "Vivre_Reboot_";
    private const int GuidLength = 32;

    /// <summary>True only for an exact Vivre reboot-service SCM KEY name: "Vivre_Reboot_" followed by
    /// exactly 32 LOWERCASE hex digits (Guid "N" always emits lowercase — an uppercase variant was not
    /// created by Vivre, so reject: the safe direction for a deleter). Anchored, never Contains —
    /// rejects the fixed-name Vivre_Reboot scheduled task, Vivre_WUA_* agent services, and anything
    /// decorated or embedded.</summary>
    public static bool IsReapableName(string? serviceName)
    {
        if (serviceName is null || serviceName.Length != Prefix.Length + GuidLength)
        {
            return false;
        }

        if (!serviceName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = Prefix.Length; i < serviceName.Length; i++)
        {
            char c = serviceName[i];
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The full reap decision: an unmistakably-Vivre reboot-service name AND confirmed
    /// Stopped. Anything else (Running/StartPending/StopPending/Unknown) is skipped — a non-Stopped
    /// state means a reboot may be in flight or the service is unreadable; never interfere.</summary>
    public static bool ShouldReap(string? serviceName, RemoteServiceState state) =>
        IsReapableName(serviceName) && state == RemoteServiceState.Stopped;
}
