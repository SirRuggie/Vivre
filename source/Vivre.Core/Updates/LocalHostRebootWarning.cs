using Vivre.Core.PowerShell;

namespace Vivre.Core.Updates;

/// <summary>
/// Pure (no I/O, no side effects) razor for the "you are about to reboot the box Vivre is running on"
/// DISCLOSURE shown in the existing reboot confirms.
/// </summary>
/// <remarks>
/// <para>
/// Sits beside <see cref="RebootProbeAdmission"/> and <see cref="BootTimeRefresh"/> — the same family of
/// tiny razors kept in Core so the rule is unit-testable (<c>Vivre.Core.Tests</c> is <c>net10.0</c> and
/// cannot reference the <c>net10.0-windows</c> Desktop assembly, so anything that needs a test lives here).
/// </para>
/// <para>
/// THIS IS DISCLOSURE, NOT A GUARD. Nothing here gates, blocks, filters, or reorders a reboot: the only
/// consumer of this type is dialog text. Rebooting the Vivre host is a legitimate operator action —
/// the operator simply deserves to be told, by name, that it takes Vivre (and any wave Vivre is currently
/// driving against OTHER machines) down with it.
/// </para>
/// <para>
/// "Is this the local box" is answered by <see cref="HostName.IsLocal"/> — the one existing detector, shared
/// with the local-vs-WinRM dispatch decision — so "what counts as local" can never drift between the two.
/// Blank / whitespace entries are skipped BEFORE that call: <see cref="HostName.IsLocal"/> treats an empty
/// host as local (correct for dispatch — no host means run here), but an unnamed row in a selection is not
/// evidence the operator picked this machine, and warning on it would be a false alarm.
/// </para>
/// </remarks>
public static class LocalHostRebootWarning
{
    /// <summary>
    /// True when <paramref name="hostNames"/> (the machines an operator just chose to reboot) includes the
    /// machine Vivre itself is running on — by real machine name, or by any of the local aliases
    /// <see cref="HostName.IsLocal"/> recognises (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>, <c>.</c>).
    /// </summary>
    public static bool TargetsTheVivreHost(IEnumerable<string?>? hostNames) =>
        hostNames is not null
        && hostNames.Any(n => !string.IsNullOrWhiteSpace(n) && HostName.IsLocal(n));

    /// <summary>
    /// The warning sentence pair for a named host. Kept to TWO sentences to match the terseness of the
    /// surrounding confirm text; takes the name as a parameter so the wording is testable without depending
    /// on the machine the tests happen to run on.
    /// </summary>
    public static string Warning(string hostName) =>
        $"{hostName} is the machine Vivre is running on. Rebooting it terminates Vivre and abandons any "
        + "in-flight wave against the other machines.";

    /// <summary>
    /// The warning to append to an existing reboot confirm, or <c>null</c> when the selection doesn't include
    /// this machine and the dialog should read exactly as it always has. Names the host by its REAL machine
    /// name even when the operator selected it by an alias.
    /// </summary>
    public static string? WarningOrNull(IEnumerable<string?>? hostNames) =>
        TargetsTheVivreHost(hostNames) ? Warning(Environment.MachineName) : null;
}
