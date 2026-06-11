using System;
using System.IO;
using Microsoft.Win32;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Gathers the live "servicing busy / reboot pending" signals from the registry + filesystem
    /// (the agent runs as SYSTEM, so it can read all of these) and asks
    /// <see cref="BootServicingState.Evaluate"/> for the verdict. The signal-gathering is the only
    /// untested part (it's pure I/O); the decision lives in the linked, unit-tested pure file.
    ///
    /// <para>Signals mirror <c>HostRebootProbe</c>'s reboot-pending cascade and add the
    /// servicing-in-progress ones (CBS RebootInProgress / PackagesPending and WinSxS\pending.xml)
    /// that indicate an offline-servicing transaction is staged or mid-flight.</para>
    /// </summary>
    internal static class BootBusyGuard
    {
        private const string CbsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
        private const string WuauRebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

        /// <summary>True if a reboot/servicing operation is already pending; <paramref name="reason"/>
        /// is a short human explanation. Any read failure is treated as "signal absent" (a probe
        /// error must not block a legitimate install).</summary>
        public static bool IsServicingBusy(out string reason)
        {
            (bool busy, string r) = BootServicingState.Evaluate(
                cbsRebootInProgress: SubKeyExists(CbsKey + @"\RebootInProgress"),
                pendingXmlExists: PendingXmlExists(),
                cbsPackagesPending: SubKeyExists(CbsKey + @"\PackagesPending"),
                cbsRebootPending: SubKeyExists(CbsKey + @"\RebootPending"),
                wuauRebootRequired: KeyExists(WuauRebootKey));

            reason = r;
            return busy;
        }

        private static bool KeyExists(string subKey)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(subKey))
                {
                    return k != null;
                }
            }
            catch
            {
                return false;
            }
        }

        // A CBS child key (e.g. RebootPending) "exists" only when present — it's absent in the
        // normal state, so presence is the signal.
        private static bool SubKeyExists(string subKey) => KeyExists(subKey);

        private static bool PendingXmlExists()
        {
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(windir))
                {
                    windir = Environment.GetEnvironmentVariable("WinDir") ?? @"C:\Windows";
                }

                return File.Exists(Path.Combine(windir, "WinSxS", "pending.xml"));
            }
            catch
            {
                return false;
            }
        }

    }
}
