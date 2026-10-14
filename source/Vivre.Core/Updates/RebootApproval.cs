using Vivre.Core.Models;

namespace Vivre.Core.Updates;

/// <summary>Captures the rows and host identities described by a reboot approval.</summary>
public sealed class RebootApproval
{
    private readonly IReadOnlyList<Computer> _targets;
    private readonly string[] _names;

    private RebootApproval(IEnumerable<Computer> targets, string scope)
    {
        _targets = Array.AsReadOnly(targets.ToArray());
        _names = _targets.Select(c => c.Name).ToArray();
        Scope = scope;
    }

    public int Count => _targets.Count;
    public string Scope { get; }
    public string? LocalWarning => LocalHostRebootWarning.WarningOrNull(_names);
    public string ForcePrompt => $"Force-reboot {Scope} now?\n\nApplications will be forced closed; unsaved work will be lost.";
    public string VerifyPrompt => $"Reboot and verify {Scope}?\n\n"
        + "Starts gracefully. If the reboot call is refused because someone is logged on (including a disconnected session), Vivre forces it immediately. "
        + "Otherwise it forces after 8 minutes (20 for staged Server 2016). Forced reboots close applications; unsaved work will be lost.\n\n"
        + "Vivre checks that servicing has finished before starting and then verifies each machine after restart. Run this when the machines are safe to restart.";

    public static RebootApproval Selected(IEnumerable<Computer> selected)
    {
        Computer[] rows = selected.ToArray();
        return new(rows, $"{rows.Length} selected machine(s)");
    }

    public static RebootApproval Pending(IEnumerable<Computer> installTargets, bool selectedScope)
    {
        Computer[] all = installTargets.ToArray();
        Computer[] pending = all.Where(c => c.RebootRequired == true).ToArray();
        string source = selectedScope ? "selected install targets" : "install targets in this tab";
        return new(pending, $"the {pending.Length} reboot-pending machine(s) out of {all.Length} {source}");
    }

    /// <summary>Only executes after approval, using the captured rows. A renamed row invalidates approval.</summary>
    public async Task ExecuteAsync(bool approved, Func<IReadOnlyList<Computer>, Task> execute, Action<string> reportChanged)
    {
        if (!approved || Count == 0) return;
        if (_targets.Where((row, i) => !string.Equals(row.Name, _names[i], StringComparison.Ordinal)).Any())
        {
            reportChanged("Reboot cancelled: a target name changed while confirmation was open. Review the targets and try again.");
            return;
        }
        await execute(_targets);
    }
}
