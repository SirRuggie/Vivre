namespace Vivre.Core.Vitals;

/// <summary>
/// The single source of the operator-facing wording for a degraded WinRM transport: a one-line caption
/// (the headline), a bulleted fix (the scannable "what to do", shown in the Machine Details "Connection"
/// callout), and a concise one-line reason (for the vitality "why this score" list + the activity log).
/// Used by both <see cref="VitalityScorer"/> and the Connection callout so they can never drift. All
/// accessors return null when the transport is healthy / unknown.
///
/// <para>One <see cref="WinRmHealth.KerberosRejected"/> flag covers TWO real causes that can't be told
/// apart at scan time (no per-box AD SPN lookup): the app-server SPN collision (0x80090322 — by design,
/// e.g. SSRS / Deltek Vision under a service account) and a not-domain-joined host (0x80090303 — no SPN).
/// The bullets present both lead-neutrally so neither box is sent down the wrong path.</para>
/// </summary>
public static class WinRmHealthGuidance
{
    /// <summary>A one-line headline for the connection callout (keeps the "on your login / still
    /// manageable" facts so a rescued box reads as "needs a look", not "broken").</summary>
    public static string? Caption(WinRmHealth? health) => health switch
    {
        WinRmHealth.KerberosRejected =>
            "WinRM Kerberos auth rejected — vitals read over SMB/DCOM on your login. Box stays fully manageable.",
        WinRmHealth.WinRmUnavailable =>
            "WinRM didn't respond — vitals read over DCOM on your login. Box stays fully manageable.",
        _ => null,
    };

    /// <summary>The fix as scannable bullets (rendered as a bulleted list in the Connection callout).</summary>
    public static IReadOnlyList<string>? FixBullets(WinRmHealth? health) => health switch
    {
        WinRmHealth.KerberosRejected =>
        [
            "No action required — vitals, scan and install all work over SMB/DCOM on your current login.",
            "There are two possible causes, and Vivre can't tell which without an AD SPN lookup:",
            "App server (0x80090322, wrong principal): a domain service account owns the host's http/<host> SPN for the app's single sign-on, so WinRM can't share that identity — by design.",
            "If so (e.g. SSRS / Deltek Vision), leave it — “repairing” the SPN breaks the app's SSO.",
            "Not domain-joined (0x80090303, target unknown): the host has no http SPN at all.",
            "Only then: domain-join it or register the SPN — and only if you actually need WinRM here.",
            "To tell them apart, run 'setspn -Q http/<host>' — a service account = leave it; nothing found = no SPN.",
        ],
        WinRmHealth.WinRmUnavailable =>
        [
            "Vitals read over DCOM. Scan and install fall back to the on-box agent over SMB if WinRM is down (requires SMB/445 reachable on your current login).",
            "If it persists, run 'winrm quickconfig' on the host — the WinRM service is stopped or misconfigured.",
            "A one-off “remote session ended” is transient and clears on the next Check Vitals.",
        ],
        _ => null,
    };

    /// <summary>A concise one-liner for the vitality "why this score" list + activity log (the full
    /// step-by-step lives in the callout's bullets). Carries the SSPI codes so it's self-explanatory.</summary>
    public static string? Reason(WinRmHealth? health) => health switch
    {
        WinRmHealth.KerberosRejected =>
            "WinRM Kerberos auth rejected — vitals read over SMB/DCOM on your login (no action needed). "
            + "Likely 0x80090322 (the host's http SPN is held by an app's service account for SSO — by design) "
            + "or 0x80090303 (not domain-joined / no SPN). See the Connection box on the Vitals tab.",
        WinRmHealth.WinRmUnavailable =>
            "WinRM didn't respond — vitals read over DCOM on your login (no action needed). The WinRM service "
            + "may be stopped (run 'winrm quickconfig') or the session dropped (transient). See the Connection box.",
        _ => null,
    };
}
