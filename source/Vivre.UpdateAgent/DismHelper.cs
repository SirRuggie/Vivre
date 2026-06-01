// Nullable-oblivious (net48 agent; <Nullable>disable</Nullable>). Stated explicitly so the file
// stays warning-clean when linked into the nullable-enabled test project.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// DISM-based package removal — the supported, non-deprecated servicing path (vs. the
    /// deprecated wusa.exe). Resolves a KB article id to its CBS package name via
    /// <c>dism /online /get-packages</c> and removes it with <c>dism /online /Remove-Package</c>.
    /// Used by the uninstall flow as the fallback for updates Windows Update itself won't remove.
    /// </summary>
    internal static class DismHelper
    {
        // (PackageIdentity, State) pairs from the one-time enumeration, cached for the run.
        private static List<KeyValuePair<string, string>> _packages;

        /// <summary>
        /// Removes the installed package matching <paramref name="kb"/> via DISM.
        /// Returns (succeeded, rebootRequired, failureReason).
        /// </summary>
        public static (bool ok, bool reboot, string reason) RemoveByKb(string kb)
        {
            string packageName;
            try
            {
                packageName = ResolvePackage(kb);
            }
            catch (Exception ex)
            {
                return (false, false, "DISM package lookup failed: " + ex.Message);
            }

            if (packageName == null)
            {
                return (false, false, "no supported uninstall path (no removable DISM package for KB" + kb + ")");
            }

            try
            {
                int exit = RunDism("/online /Remove-Package /PackageName:" + packageName + " /quiet /norestart");
                switch (exit)
                {
                    case 0:
                        return (true, false, null);
                    case 3010:   // ERROR_SUCCESS_REBOOT_REQUIRED
                        return (true, true, null);
                    default:
                        return (false, false, DescribeDismExit(exit));
                }
            }
            catch (Exception ex)
            {
                return (false, false, "DISM remove failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Turns a DISM failure exit code into a plain-English reason. DISM returns the HRESULT as
        /// its (unsigned) exit code on failure, so we show it in hex and translate the common
        /// "can't be removed" cases — most importantly 0x800F0825 (a permanent/required package,
        /// which is normal and by-design for cumulative & servicing-stack updates).
        /// </summary>
        internal static string DescribeDismExit(int exit)
        {
            uint code = unchecked((uint)exit);
            string hex = "0x" + code.ToString("X8");
            switch (code)
            {
                case 0x800F0825: // CBS_E_CANNOT_UNINSTALL
                    return "Windows blocks removal — this update is permanent/required and can't be uninstalled (" + hex
                        + "). Normal for cumulative & servicing-stack updates.";
                case 0x80070005: // E_ACCESSDENIED
                    return "access denied removing the package (" + hex + ")";
                default:
                    return "DISM could not remove it (" + hex + ")";
            }
        }

        /// <summary>Finds the Installed package identity whose name carries this KB, or null.</summary>
        private static string ResolvePackage(string kb)
        {
            EnsurePackages();

            // Match the KB as a whole token, NOT a bare substring. CBS identities carry the article
            // as "KB<digits>" bounded by non-digits (e.g. Package_for_KB5000802~31bf3856…), and KB
            // numbers vary in length, so a substring match for "KB5000" would also hit "KB5000802"
            // and DISM would remove the WRONG (likely cumulative/security) update. Require a non-digit
            // (or string end) on both sides so KB5000 cannot match KB5000802.
            var token = new Regex(@"(?<![0-9])KB" + Regex.Escape(kb) + @"(?![0-9])", RegexOptions.IgnoreCase);
            foreach (KeyValuePair<string, string> pkg in _packages)
            {
                if (token.IsMatch(pkg.Key)
                    && pkg.Value.IndexOf("Installed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return pkg.Key;
                }
            }

            return null;
        }

        /// <summary>Enumerates installed packages once via <c>dism /online /get-packages /format:list</c>.</summary>
        private static void EnsurePackages()
        {
            if (_packages != null)
            {
                return;
            }

            _packages = new List<KeyValuePair<string, string>>();

            var psi = new ProcessStartInfo("dism.exe", "/online /english /get-packages /format:list")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var proc = Process.Start(psi))
            {
                // Drain stderr concurrently so a chatty DISM (servicing-stack warnings, corrupt
                // packages) can't deadlock by filling the stderr pipe while we block on stdout.
                Task<string> errTask = proc.StandardError.ReadToEndAsync();
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                errTask.GetAwaiter().GetResult();

                // /format:list emits blocks of "Package Identity : X" then "State : Installed", etc.
                string currentIdentity = null;
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    int colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();

                    if (key.Equals("Package Identity", StringComparison.OrdinalIgnoreCase))
                    {
                        currentIdentity = value;
                    }
                    else if (key.Equals("State", StringComparison.OrdinalIgnoreCase) && currentIdentity != null)
                    {
                        _packages.Add(new KeyValuePair<string, string>(currentIdentity, value));
                        currentIdentity = null;
                    }
                }
            }
        }

        private static int RunDism(string arguments)
        {
            var psi = new ProcessStartInfo("dism.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var proc = Process.Start(psi))
            {
                // Drain both pipes concurrently so a chatty DISM can't deadlock on a full buffer
                // (reading stdout to end first would hang if DISM blocks writing a full stderr pipe).
                Task<string> errTask = proc.StandardError.ReadToEndAsync();
                proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                errTask.GetAwaiter().GetResult();
                return proc.ExitCode;
            }
        }
    }
}
