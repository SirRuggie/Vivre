using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Writes the append-only progress JSONL the streaming controller tails: one compact JSON
    /// object per line, UTF-8. Thread-safe — WUA's progress callbacks fire on a COM thread while
    /// the main poll loop also writes, so every emit takes a lock. The <c>ts</c> tick makes each
    /// line unique even when nothing visible changed, so a slow link can still tell "still going"
    /// from "wedged" (matches the old PowerShell worker's shape exactly).
    /// </summary>
    internal sealed class ProgressWriter
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public ProgressWriter(string path)
        {
            _path = path;
            // Start clean — the lane removes any stale file before launch too, but be defensive.
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch
            {
                // A leftover file is harmless; the controller reads only new bytes anyway.
            }
        }

        public void Write(string phase, string message, int? percent, int available, int installed, int failed, bool rebootPending)
        {
            var obj = new Dictionary<string, object>
            {
                ["phase"] = phase,
                ["message"] = message,
                ["percent"] = percent,
                ["available"] = available,
                ["installed"] = installed,
                ["failed"] = failed,
                ["rebootPending"] = rebootPending,
                ["ts"] = DateTime.Now.Ticks,
            };

            string line = _serializer.Serialize(obj);
            lock (_gate)
            {
                // Best-effort with a short retry, and NEVER throw. A transient sharing violation
                // (AV/indexer touching the file in C:\Windows\Temp) must not abort the operation —
                // and this is called from Main's error path too, so a throw here would escape Main
                // and leave the controller with only its generic timeout. The controller tolerates a
                // missed line via the next ts-bumped line + the heartbeat.
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                    catch (Exception)
                    {
                        // Give up on this one line rather than throw into the caller.
                        return;
                    }
                }
            }
        }
    }
}
