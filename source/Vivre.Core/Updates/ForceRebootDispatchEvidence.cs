using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>Maintains the evidence gate for an ambiguously acknowledged standalone Force reboot.</summary>
public static class ForceRebootDispatchEvidence
{
    public static int BeginDispatch(Computer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return ++computer.ForceRebootGeneration;
    }

    public static bool IsCurrentDispatch(Computer computer, int dispatchGeneration)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return computer.ForceRebootGeneration == dispatchGeneration;
    }

    public static void BeginUncertain(Computer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        computer.ForceRebootAwaitingVerify = false;
        computer.ForceRebootAwaitingRestartEvidence = true;
        computer.UpdatePhase = PatchPhase.Unverified.ToString();
        computer.UpdateMessage = "Reboot not confirmed — checking for restart";
        computer.UnverifiedRebootProbeOnly = false;
    }

    public static void RecordRestartEvidence(Computer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        if (!computer.ForceRebootAwaitingRestartEvidence)
        {
            return;
        }

        computer.ForceRebootAwaitingRestartEvidence = false;
        computer.ForceRebootAwaitingVerify = true;
    }

    public static void RecordRestartEvidence(Computer computer, int dispatchGeneration)
    {
        ArgumentNullException.ThrowIfNull(computer);
        if (computer.ForceRebootGeneration != dispatchGeneration)
        {
            return;
        }

        RecordRestartEvidence(computer);
    }

    public static void ExpireUnverified(Computer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        if (computer.ForceRebootAwaitingRestartEvidence)
        {
            computer.UpdatePhase = PatchPhase.Unverified.ToString();
            computer.UpdateMessage = "Reboot unverified — no restart could be confirmed; re-check";
            computer.RebootMessage = "Reboot unverified — no restart could be confirmed; re-check";
            computer.UnverifiedRebootProbeOnly = false;
        }

        // Preserve the evidence guard as-is: uncertain dispatches remain guarded after expiry, while an
        // accepted dispatch does not acquire uncertainty merely because its ordinary fast watch expired.
        computer.ForceRebootAwaitingVerify = false;
    }
}
