using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

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
        private static string DescribeDismExit(int exit)
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
            string needle = "KB" + kb;
            foreach (KeyValuePair<string, string> pkg in _packages)
            {
                if (pkg.Key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
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
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

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
                // Drain the pipes so a chatty DISM can't deadlock on a full buffer.
                proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return proc.ExitCode;
            }
        }
    }
}
