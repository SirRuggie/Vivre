namespace Vivre.Core.Configuration;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  ⚠  MACHINE-WIDE, MULTI-OPERATOR FILE — C:\ProgramData\Vivre\settings.json  ⚠
//  This POCO is serialized to a shared file that EVERY operator account on the box can READ and
//  WRITE. NEVER add credential material here — no passwords, secrets, tokens, API keys, or
//  DPAPI-protected blobs. Per-operator secrets stay in memory (the in-memory CredentialStore) or,
//  if they must persist, go to a per-user ENCRYPTED store (see RdpCredentialStore's DPAPI-per-user
//  model). SharedSettingsStore.Save runs a reflection guard that THROWS if a credential-shaped
//  property (by name or type) is ever added — that guard is a backstop for this rule, not licence
//  to get anywhere near it.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Operational (fleet-wide) settings shared by every operator on the machine, persisted to
/// <c>C:\ProgramData\Vivre\settings.json</c> by <see cref="SharedSettingsStore"/>. Personal preferences
/// (theme, grid columns, auto-check on load, etc.) stay per-user in the Roaming <c>AppSettings</c> store —
/// only the settings that should look the same to whoever runs Vivre on this box live here.
/// </summary>
public sealed class SharedSettings
{
    /// <summary>WhatsUp Gold server address, remembered for the maintenance-mode dialog (the
    /// credentials are NOT saved — only this address).</summary>
    public string WugServer { get; set; } = "10.70.25.111";

    /// <summary>Folder holding the stageable software packages (each subfolder or lone .msi/.exe is
    /// one package). Populates the Stage software window's package dropdown; empty by default.</summary>
    public string PackagesFolder { get; set; } = string.Empty;

    /// <summary>Folder the Server 2016 full-package CU lane reads the monthly cumulative-update <c>.msu</c>
    /// from (the operator drops the catalog download here; auto-fetch is off by design). Defaults to
    /// <c>C:\Vivre\VivrePackages</c>; configurable in Settings.</summary>
    public string LcuPackagesFolder { get; set; } = @"C:\Vivre\VivrePackages";

    /// <summary>This cycle's Server 2016 cumulative update — the KB the lane stages and the UBR it verifies
    /// after the reboot. Surfaced in the 2016 panel and confirmed by the operator each month. Defaults to
    /// EMPTY (no KB, UBR 0) so a fresh shared file never carries a stale-looking KB — the empty-KB gates
    /// guide the operator to set it (or use "Read from package").</summary>
    public MonthlyCu MonthlyCu { get; set; } = new();

    /// <summary>Cap on how many hosts install/uninstall/stage/clean-up at once. Operator-tunable in
    /// Settings → "Max simultaneous installs"; applied to every install sweep started after the change
    /// (in-flight sweeps continue at the cap they were started with). The practical governor is
    /// update-download bandwidth (N hosts pulling cumulative updates simultaneously), not the client.
    /// Range 1–200; default 50.</summary>
    public int MaxSimultaneousInstalls { get; set; } = 50;

    /// <summary>How many WhatsUp Gold devices the state check looks up at once. Operator-tunable in
    /// Settings → "WhatsUp Gold state check — simultaneous lookups"; applied to checks started after the
    /// change (an in-flight check keeps the value it launched with). Measured on the live WUG server:
    /// wall time halves going 1→2 and then flatlines 2→4→8 with per-lookup latency creeping up, so the
    /// ceiling is deliberately 4 (<see cref="Vivre.Core.Wug.WugMaintenance.StateReadMaxConcurrency"/>).
    /// Range 1–4; default 2. 1 = sequential (the pre-parallel behaviour).</summary>
    public int WugStateConcurrency { get; set; } = 2;

    /// <summary>The persisted set of host names the operator has flagged as needing staged (DISM) patching;
    /// OrdinalIgnoreCase; the source of truth behind <see cref="Vivre.Core.Models.Computer.RequiresStagedPatching"/>.
    /// Always normalize after deserialization — a JSON round-trip resets the set's comparer to ordinal.</summary>
    public HashSet<string> StagedHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The month's Server 2016 cumulative update the operator confirms each cycle. Kept deliberately small —
/// the KB to stage, the architecture token expected in the <c>.msu</c> name, and the UBR the box should
/// report once the CU commits (what Verify / the Reboot Wave check). Maps to <c>LcuTarget</c> in the lane.
/// </summary>
public sealed class MonthlyCu
{
    /// <summary>The CU article, e.g. "KB5094122" (bare "5094122" also accepted by the lane). Empty by
    /// default — a fresh shared file must not ship a stale KB; the empty-KB gates prompt the operator.</summary>
    public string Kb { get; set; } = string.Empty;

    /// <summary>Architecture token expected in the .msu filename (Server 2016 is x64).</summary>
    public string Arch { get; set; } = "x64";

    /// <summary>The build revision (UBR) the box should report after the CU commits, e.g. 9234 → the box
    /// reads 14393.9234. Verify and the Reboot Wave use this as the pass/fail check. 0 = not set yet.</summary>
    public int TargetUbr { get; set; }

    /// <summary>The display Vivre shows in the 2016 panel, e.g. "KB5094122 / 9234".</summary>
    public string Display => $"{Kb} / {TargetUbr}";
}
