using System;
using System.Globalization;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Pure decisions for the component-cleanup liveness sampler — kept free of any
    /// <see cref="System.Diagnostics.Process"/> / I/O dependency so it compiles under BOTH the net48
    /// agent and the net10 test project (linked source, mirroring <see cref="BootServicingState"/>).
    /// The CPU sampling + process I/O itself lives in Program.cs (it needs a live process); only the
    /// hang DECISION and the elapsed formatting are here so they're unit-testable.
    ///
    /// <para>Why CPU, not percent: DISM's StartComponentCleanup on a backlogged Server 2016 box stalls
    /// its redrawn percent for long stretches while it genuinely reclaims the component store — the
    /// percent is decoration. A WORKING dism burns CPU; a DEADLOCKED one sits at ~0. So "no meaningful
    /// CPU advance for the whole hang window" is the real "genuinely hung" signal.</para>
    /// </summary>
    internal static class CleanupLiveness
    {
        /// <summary>
        /// The cleanup is hung when dism's CPU has not advanced meaningfully for at least the whole
        /// hang window. A working dism keeps consuming CPU, so a window with zero meaningful advance
        /// means it deadlocked (not merely a stalled percent). Boundary is inclusive: at exactly the
        /// window we treat it as hung.
        /// </summary>
        public static bool IsHung(TimeSpan sinceLastCpuAdvance, TimeSpan hangWindow)
            => sinceLastCpuAdvance >= hangWindow;

        /// <summary>
        /// True when a CPU sample advanced "meaningfully" over the prior high-water mark — i.e. by more
        /// than <paramref name="minAdvance"/> of consumed processor time. A trickle below the threshold
        /// (idle-thread noise on a live process) does NOT reset the hang clock; only real work does.
        /// </summary>
        public static bool CpuAdvancedMeaningfully(TimeSpan previousHighWater, TimeSpan current, TimeSpan minAdvance)
            => current - previousHighWater > minAdvance;

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
