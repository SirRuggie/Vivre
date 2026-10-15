// Nullable-oblivious: this is compiled by the net48 agent and linked into the net10 tests.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Observes a live servicing child without controlling it. A quiet servicing stack produces
    /// actionable, non-terminal guidance; this class never kills a process or declares an outcome.
    /// </summary>
    internal sealed class ServicingChildWatch : IDisposable
    {
        internal static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan DefaultStallWindow = TimeSpan.FromMinutes(45);
        internal static readonly TimeSpan DefaultRepeatCadence = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan CpuAdvanceThreshold = TimeSpan.FromSeconds(1);
        private static readonly string[] ProcessNames = { "dism", "TiWorker", "TrustedInstaller" };

        private readonly string _operation;
        private readonly Action<string, Func<bool>> _report;
        private readonly TimeSpan _cadence;
        private readonly TimeSpan _stallWindow;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private readonly Func<IDictionary<int, TimeSpan>> _sampleCpu;
        private readonly Func<DateTime> _sampleLog;
        private readonly Action<Action> _queueReport;
        private int _disposed;

        internal ServicingChildWatch(Process child, string operation, Action<string> report)
            : this(child, operation, (message, _) => report(message))
        {
        }

        internal ServicingChildWatch(Process child, string operation, Action<string, Func<bool>> report)
            : this(operation, report, () => SampleServicingProcesses(child), SampleCbsLogMaxWriteUtc,
                DefaultCadence, DefaultStallWindow,
                action => ThreadPool.QueueUserWorkItem(_ => action()))
        {
        }

        internal ServicingChildWatch(string operation, Action<string> report,
            Func<TimeSpan> sampleCpu, Func<DateTime> sampleLog, TimeSpan cadence, TimeSpan stallWindow)
            : this(operation, (message, _) => report(message), () => new Dictionary<int, TimeSpan> { { 0, sampleCpu() } },
                sampleLog, cadence, stallWindow, action => ThreadPool.QueueUserWorkItem(_ => action()))
        {
        }

        internal ServicingChildWatch(string operation, Action<string, Func<bool>> report,
            Func<IDictionary<int, TimeSpan>> sampleCpu, Func<DateTime> sampleLog,
            TimeSpan cadence, TimeSpan stallWindow, Action<Action> queueReport)
        {
            _operation = operation;
            _report = report;
            _cadence = cadence;
            _stallWindow = stallWindow;
            _sampleCpu = sampleCpu;
            _sampleLog = sampleLog;
            _queueReport = queueReport;
            _thread = new Thread(Loop) { IsBackground = true, Name = "VivreServicingChildWatch" };
            _thread.Start();
        }

        private void Loop()
        {
            var elapsed = Stopwatch.StartNew();
            TimeSpan lastActivity = TimeSpan.Zero;
            TimeSpan lastReport = TimeSpan.Zero;
            bool hasReported = false;
            IDictionary<int, TimeSpan> cpuHighWater = SafeSampleCpu(new Dictionary<int, TimeSpan>());
            DateTime logHighWater = SafeSampleLog(DateTime.MinValue);

            while (!_stop.Wait(_cadence))
            {
                TimeSpan now = elapsed.Elapsed;
                IDictionary<int, TimeSpan> cpu = SafeSampleCpu(cpuHighWater);
                DateTime log = SafeSampleLog(logHighWater);
                bool cpuAdvanced = CpuSetAdvancedMeaningfully(cpuHighWater, cpu, CpuAdvanceThreshold);
                bool working = CleanupLiveness.IsWorking(cpuAdvanced,
                    CleanupLiveness.LogAdvanced(logHighWater, log));
                // Preserve sub-threshold CPU increments until they cumulatively become meaningful.
                MergeCpuHighWater(cpuHighWater, cpu);
                if (log > logHighWater) logHighWater = log;
                if (working) lastActivity = now;

                TimeSpan quietFor = now - lastActivity;
                TimeSpan sinceLastReport = hasReported ? now - lastReport : TimeSpan.MaxValue;
                if (ShouldReport(quietFor, _stallWindow, sinceLastReport, DefaultRepeatCadence))
                {
                    lastReport = now;
                    hasReported = true;
                    QueueReport(CreateStallMessage(_operation, quietFor));
                }
            }
            _stop.Dispose();
        }

        internal static bool ShouldReport(TimeSpan quietFor, TimeSpan stallWindow,
            TimeSpan sinceLastReport, TimeSpan repeatCadence)
            => CleanupLiveness.IsStalled(quietFor, stallWindow) && sinceLastReport >= repeatCadence;

        internal static string CreateStallMessage(string operation, TimeSpan quietFor)
            => operation + " has shown no servicing-stack CPU or CBS-log activity for "
                + CleanupLiveness.FormatElapsed(quietFor)
                + ". It may still be working: check DISM.log and CBS.log on the host. Verify the host's "
                + "dism, TiWorker, and TrustedInstaller state before starting conflicting servicing work.";

        private void QueueReport(string message)
        {
            try
            {
                _queueReport(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    try { _report(message, () => Volatile.Read(ref _disposed) != 0); }
                    catch (Exception) { /* advisory reporting must never terminate the agent */ }
                });
            }
            catch (Exception) { /* queue failure must never terminate the agent */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Set();
            // Reporting is queued away from the sampler. Never let slow status I/O hold child cleanup.
            // Keep the event alive if a faulty sampler ignores the stop long enough to outlive this bound.
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        private IDictionary<int, TimeSpan> SafeSampleCpu(IDictionary<int, TimeSpan> fallback)
        {
            try { return _sampleCpu(); }
            catch (Exception) { return fallback; }
        }

        internal static bool CpuSetAdvancedMeaningfully(IDictionary<int, TimeSpan> highWater,
            IDictionary<int, TimeSpan> current, TimeSpan threshold)
        {
            foreach (KeyValuePair<int, TimeSpan> item in current)
            {
                TimeSpan previous;
                if (!highWater.TryGetValue(item.Key, out previous)) previous = TimeSpan.Zero;
                if (CleanupLiveness.CpuAdvancedMeaningfully(previous, item.Value, threshold)) return true;
            }
            return false;
        }

        private static void MergeCpuHighWater(IDictionary<int, TimeSpan> highWater,
            IDictionary<int, TimeSpan> current)
        {
            var exited = new List<int>();
            foreach (int processId in highWater.Keys)
                if (!current.ContainsKey(processId)) exited.Add(processId);
            foreach (int processId in exited) highWater.Remove(processId);

            foreach (KeyValuePair<int, TimeSpan> item in current)
            {
                TimeSpan previous;
                if (!highWater.TryGetValue(item.Key, out previous) || item.Value < previous
                    || CleanupLiveness.CpuAdvancedMeaningfully(previous, item.Value, CpuAdvanceThreshold))
                    highWater[item.Key] = item.Value;
            }
        }

        private DateTime SafeSampleLog(DateTime fallback)
        {
            try { return _sampleLog(); }
            catch (Exception) { return fallback; }
        }

        internal static TimeSpan SampleServicingStackCpu(Process child)
        {
            TimeSpan total = TimeSpan.Zero;
            foreach (TimeSpan value in SampleServicingProcesses(child).Values) total += value;
            return total;
        }

        internal static IDictionary<int, TimeSpan> SampleServicingProcesses(Process child)
        {
            var samples = new Dictionary<int, TimeSpan>();
            int childId = -1;
            try
            {
                childId = child.Id;
                samples[childId] = child.TotalProcessorTime;
            }
            catch (InvalidOperationException) { /* child exited during sampling */ }
            catch (System.ComponentModel.Win32Exception) { /* child CPU was inaccessible */ }

            foreach (string name in ProcessNames)
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }
                foreach (Process process in processes)
                {
                    if (process.Id == childId)
                    {
                        process.Dispose();
                        continue;
                    }
                    try { samples[process.Id] = process.TotalProcessorTime; }
                    catch (InvalidOperationException) { continue; /* process exited between enumeration and CPU read */ }
                    catch (System.ComponentModel.Win32Exception) { continue; /* access to the process was denied */ }
                    finally { process.Dispose(); }
                }
            }
            return samples;
        }

        internal static DateTime SampleCbsLogMaxWriteUtc()
        {
            try
            {
                string windows = Environment.GetEnvironmentVariable("WINDIR");
                if (string.IsNullOrEmpty(windows))
                {
                    windows = Directory.GetParent(Environment.SystemDirectory)?.FullName;
                }
                if (string.IsNullOrEmpty(windows)) return DateTime.MinValue;

                string directory = Path.Combine(windows, "Logs", "CBS");
                if (!Directory.Exists(directory)) return DateTime.MinValue;
                DateTime newest = DateTime.MinValue;
                foreach (string pattern in new[] { "CBS.log", "CbsPersist_*.log" })
                    foreach (string path in Directory.EnumerateFiles(directory, pattern))
                        try
                        {
                            DateTime written = File.GetLastWriteTimeUtc(path);
                            if (written > newest) newest = written;
                        }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                return newest;
            }
            catch (IOException) { return DateTime.MinValue; }
            catch (UnauthorizedAccessException) { return DateTime.MinValue; }
        }
    }
}
