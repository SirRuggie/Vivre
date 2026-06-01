using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Reads an <see cref="AgentConfig"/> from the JSON the lane drops on the target. Kept separate
    /// from the <see cref="AgentConfig"/> POCO so that POCO carries no net48-only dependency
    /// (<c>JavaScriptSerializer</c> lives in System.Web.Extensions) and can be linked into the net10
    /// test project to assert the cross-framework JSON contract.
    /// </summary>
    internal static class AgentConfigLoader
    {
        public static AgentConfig Load(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var ser = new JavaScriptSerializer();
            AgentConfig config = ser.Deserialize<AgentConfig>(json);
            if (config == null || string.IsNullOrWhiteSpace(config.ProgressPath))
            {
                throw new InvalidOperationException(
                    "Agent config at '" + path + "' was empty or malformed (no ProgressPath).");
            }

            return config;
        }
    }
}
