using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// The settings <c>WuaUpdateLane</c> writes next to the agent EXE on the target and the
    /// agent reads from <c>args[0]</c>. Plain JSON (JavaScriptSerializer, in the net48
    /// framework — no NuGet). Property names match the JSON keys the lane emits.
    /// </summary>
    internal sealed class AgentConfig
    {
        /// <summary>"Install" (default) or "Uninstall".</summary>
        public string Mode { get; set; }

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

        public static AgentConfig Load(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var ser = new JavaScriptSerializer();
            return ser.Deserialize<AgentConfig>(json);
        }
    }
}
