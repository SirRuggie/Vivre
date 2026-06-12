// Nullable-oblivious (net48 agent; <Nullable>disable</Nullable>). Stated explicitly so the file
// stays warning-clean when linked into the nullable-enabled test project.
#nullable disable

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// The settings <c>WuaUpdateLane</c> writes next to the agent EXE on the target and the
    /// agent reads from <c>args[0]</c>. Property names match the JSON keys the lane emits — that
    /// cross-framework contract (the lane serializes with System.Text.Json on net10; the agent
    /// deserializes with JavaScriptSerializer on net48) is asserted by a round-trip test that links
    /// this POCO. The deserialization itself lives in <see cref="AgentConfigLoader"/> so this type
    /// stays free of net48-only dependencies and can be compiled into the net10 test project.
    /// </summary>
    internal sealed class AgentConfig
    {
        /// <summary>"Install" (default), "Uninstall", "Scan", or "AddPackage" (the 2016 full-package LCU
        /// lane — DISM /Add-Package of a local .msu, no WUA).</summary>
        public string Mode { get; set; }

        /// <summary>For <c>Mode = "AddPackage"</c>: the local path to the full CU package (.msu) the
        /// controller already copied to the target's ACL'd drop dir. The agent DISM-adds this; null otherwise.</summary>
        public string PackagePath { get; set; }

        /// <summary>
        /// Scan scope: "Applicable" (default — IsInstalled=0, the install checklist) or "Installed"
        /// (IsInstalled=1, the uninstall checklist). Only read in <c>Mode = "Scan"</c>; the SMB lane
        /// sets it, the WinRM lane (which scans over PowerShell, not the agent) leaves it null.
        /// </summary>
        public string Scope { get; set; }

        /// <summary>
        /// Where <c>Mode = "Scan"</c> writes the JSON update array the controller reads back over SMB.
        /// Null for Install/Uninstall (which report through <see cref="ProgressPath"/> only).
        /// </summary>
        public string ResultPath { get; set; }

        /// <summary>WUA ServerSelectionEnum: 1 managed, 2 Windows Update, 3 other-by-ServiceID.</summary>
        public int ServerSelection { get; set; }

        /// <summary>Service GUID to register/select (Microsoft Update), or null/empty for ServerSelection.</summary>
        public string ServiceId { get; set; }

        /// <summary>Whether the search returns driver updates (default false = software only).</summary>
        public bool IncludeDrivers { get; set; }

        /// <summary>Case-insensitive title substrings to skip (fleet-wide exclude box).</summary>
        public string[] Excludes { get; set; }

        /// <summary>When non-empty, restrict to updates whose first KB id is in this list (the ticked checklist).</summary>
        public string[] IncludeKbs { get; set; }

        /// <summary>Reboot the box after a successful install/uninstall that requires it.</summary>
        public bool RebootAfter { get; set; }

        /// <summary>The append-only progress JSONL the controller tails.</summary>
        public string ProgressPath { get; set; }
    }
}
