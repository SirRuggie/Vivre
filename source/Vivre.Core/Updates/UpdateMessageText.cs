namespace Vivre.Core.Updates;

/// <summary>
/// Pure text helpers for the per-host "Windows update message" shown in the grid.
/// </summary>
public static class UpdateMessageText
{
    private const string RebootRequiredPhrase = "reboot required";

    /// <summary>
    /// Removes a trailing "...reboot required" clause that an install/uninstall left on the update message,
    /// independent of the separator before it: the on-target agent uses a middot separator, older builds
    /// used a comma. Returns the message unchanged when there is no such trailing clause, and leaves a
    /// null/empty message untouched. An internal comma in the summary (e.g. "Installed 2, 1 failed") is kept.
    /// </summary>
    /// <remarks>
    /// Call this once a box's pending reboot has cleared (e.g. an out-of-band reboot the monitor detected) so
    /// the message stops claiming a reboot is still needed while the status pill already reads Done. Matching
    /// the phrase rather than an exact suffix is deliberate: the agent's wording and this strip live in
    /// different projects (the agent targets net48 and can't share a constant), so this won't silently break
    /// again if the separator changes.
    /// </remarks>
    public static string? WithoutRebootRequiredTail(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        int idx = message.LastIndexOf(RebootRequiredPhrase, StringComparison.OrdinalIgnoreCase);
        if (idx <= 0)
        {
            // Not present, or it IS the whole message - nothing safe to strip.
            return message;
        }

        // Only treat it as the trailing clause if nothing but whitespace follows it.
        if (message.AsSpan(idx + RebootRequiredPhrase.Length).Trim().Length != 0)
        {
            return message;
        }

        // Drop the phrase and any separator chars that preceded it: space, middot (U+00B7), comma,
        // em dash (U+2014), hyphen. TrimEnd only touches the tail, so an internal comma is preserved.
        return message[..idx].TrimEnd(' ', '·', ',', '—', '-');
    }
}
