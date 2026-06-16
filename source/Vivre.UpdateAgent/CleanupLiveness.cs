using System;
using System.Globalization;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Pure decisions for the component-cleanup liveness sampler — kept free of any
    /// <see cref="System.Diagnostics.Process"/> / I/O dependency so it compiles under BOTH the net48
    /// agent and the net10 test project (linked source, mirroring <see cref="BootServicingState"/>).
    /// The CPU/log sampling + process &amp; file I/O itself lives in Program.cs (it needs live processes
    /// and the CBS log folder); only the stall DECISION and the elapsed formatting are here so they're
    /// unit-testable.
    ///
    /// <para>Why a whole-stack signal, not dism's percent: DISM's StartComponentCleanup is a thin client —
    /// the heavy reclamation runs in the CBS / TrustedInstaller stack (<c>TiWorker.exe</c>), so
    /// <c>dism.exe</c> itself can sit at ~0% CPU for long LEGITIMATE stretches while the box works hard.
    /// So the "working" signal is widened to the whole servicing stack's CPU OR the CBS log growing, and
    /// a "no activity for the whole window" reading is treated as a non-terminal DISPLAY FLAG (a "may
    /// still be working" hint) — it is NEVER a kill and never a terminal Error.</para>
    /// </summary>
    internal static class CleanupLiveness
    {
        /// <summary>
        /// The cleanup is STALLED when NEITHER the servicing-stack CPU nor the CBS log has shown activity
        /// for at least the whole window. This is a non-terminal DISPLAY FLAG ("looks stalled — may still
        /// be working"), NOT a kill or terminal-Error threshold — the sampler keeps running and the next
        /// active tick clears the flag. Boundary is inclusive: at exactly the window with no activity we
        /// flag it as stalled.
        /// </summary>
        public static bool IsStalled(TimeSpan sinceLastActivity, TimeSpan window)
            => sinceLastActivity >= window;

        /// <summary>
        /// True when a CPU sample advanced "meaningfully" over the prior high-water mark — i.e. by more
        /// than <paramref name="minAdvance"/> of consumed processor time. Applied to the SUMMED servicing
        /// stack CPU (dism + TiWorker + TrustedInstaller). A trickle below the threshold (idle-thread noise
        /// on live processes) does NOT count as activity; only real work does.
        /// </summary>
        public static bool CpuAdvancedMeaningfully(TimeSpan previousHighWater, TimeSpan current, TimeSpan minAdvance)
            => current - previousHighWater > minAdvance;

        /// <summary>
        /// True when the CBS log's newest write advanced since the prior tick — the servicing stack appends
        /// to <c>CBS.log</c> (and rolls over to <c>CbsPersist_*.log</c>) while it works, so a later max
        /// last-write time across those files means the stack did work this tick even if its CPU read was
        /// momentarily flat.
        /// </summary>
        public static bool LogAdvanced(DateTime previousMaxWriteUtc, DateTime currentMaxWriteUtc)
            => currentMaxWriteUtc > previousMaxWriteUtc;

        /// <summary>
        /// The cleanup is WORKING this tick when EITHER the servicing-stack CPU advanced OR the CBS log
        /// grew. This is the load-bearing OR: dism can idle while TiWorker burns CPU, and CPU can read flat
        /// for a tick while the log still grows — either alone is proof of life, so a stall requires BOTH
        /// to be quiet.
        /// </summary>
        public static bool IsWorking(bool stackCpuAdvanced, bool logAdvanced)
            => stackCpuAdvanced || logAdvanced;

        /// <summary>
        /// Formats <paramref name="elapsed"/> as a compact human duration for the liveness line:
        /// "&lt;1m", "12m", "1h", "1h 4m". Seconds are deliberately dropped — this is a coarse
        /// "how long has cleanup been running" readout, not a stopwatch.
        /// </summary>
        public static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            long totalMinutes = (long)elapsed.TotalMinutes;
            if (totalMinutes < 1)
            {
                return "<1m";
            }

            long hours = totalMinutes / 60;
            long minutes = totalMinutes % 60;
            if (hours <= 0)
            {
                return minutes.ToString(CultureInfo.InvariantCulture) + "m";
            }

            if (minutes == 0)
            {
                return hours.ToString(CultureInfo.InvariantCulture) + "h";
            }

            return hours.ToString(CultureInfo.InvariantCulture) + "h "
                + minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }
    }
}
