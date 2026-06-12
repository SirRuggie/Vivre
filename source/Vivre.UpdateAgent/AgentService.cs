using System;
using System.ServiceProcess;
using System.Threading;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// Hosts the agent as a Windows service so the SMB lane can launch it as LocalSystem through the
    /// Service Control Manager (the BatchPatch-equivalent path used when a host rejects WinRM/Kerberos
    /// with 0x80090322). The service-aware check-in is the whole point: <see cref="OnStart"/> returns
    /// immediately after spawning the work thread, so the SCM sees SERVICE_RUNNING within a moment and
    /// never reports error 1053 ("the service did not respond to the start request in a timely fashion")
    /// — the failure a plain non-service EXE produces under <c>sc start</c>. When the work finishes the
    /// worker self-stops, so the controller can DeleteService and remove the drop dir cleanly.
    ///
    /// <para>Reached only via <c>Vivre.UpdateAgent.exe --service &lt;config.json&gt;</c>, which the SCM
    /// runs from the service binPath. The WinRM lane (one-time SYSTEM scheduled task) still runs the
    /// EXE in plain console mode — that path is unchanged.</para>
    /// </summary>
    internal sealed class AgentService : ServiceBase
    {
        private readonly string _configPath;
        private Thread _worker;

        public AgentService(string configPath)
        {
            _configPath = configPath;
            ServiceName = "VivreUpdateAgent";
            CanStop = true;
            CanShutdown = true;
            // We write our own progress JSONL; don't let the SCM tie us to the event log.
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            // Return FAST. Anything slow here (the WUA search/download/install) would blow the SCM's
            // start timeout and surface as 1053. Spin the real work onto a thread and let OnStart
            // return so the SCM transitions us to RUNNING right away.
            _worker = new Thread(Work)
            {
                IsBackground = false,
                Name = "VivreAgentWork",
            };
            _worker.Start();
        }

        private void Work()
        {
            try
            {
                // Heartbeat only in service mode: a long WUA search can write nothing for a minute, and
                // the controller tails the progress file from THIS machine over SMB (no server-side
                // heartbeat loop like the WinRM lane has). A periodic Heartbeat line proves liveness so
                // a quiet stretch never looks identical to a hung process. Console mode passes null, so
                // the WinRM lane's progress stream stays byte-for-byte unchanged.
                var heartbeat = new AgentHeartbeat(TimeSpan.FromSeconds(10));
                Program.RunFromConfig(_configPath, heartbeat);
            }
            catch
            {
                // RunFromConfig already translated any fault into a terminal Error progress line; a
                // throw must never escape the work thread (it would crash the service process and the
                // controller would only see silence).
            }
            finally
            {
                // Self-stop so the controller's stop->wait->DeleteService->delete-dir teardown proceeds
                // without having to force a stop. Best-effort: if the SCM already initiated a stop this
                // is a no-op.
                try
                {
                    Stop();
                }
                catch
                {
                    // The SCM may have torn us down already; nothing to do.
                }
            }
        }

        protected override void OnStop()
        {
            // ServiceBase.Stop() runs OnStop synchronously on the CALLING thread. When the worker
            // self-stops (its normal completion), OnStop therefore runs on the worker thread itself —
            // joining it would just block for the full timeout against the current thread. Skip the
            // join in that case (the work is already done). Only an EXTERNAL stop (user cancel / the
            // controller's teardown) arrives on a different thread; give the worker a bounded window to
            // finish its current step and write its terminal line so the controller doesn't miss it.
            if (Thread.CurrentThread == _worker)
            {
                return;
            }

            try
            {
                _worker?.Join(TimeSpan.FromSeconds(15));
            }
            catch
            {
                // Bounded wait only; never hang the SCM stop.
            }
        }
    }

    /// <summary>
    /// Writes a <c>Heartbeat</c> progress line on a fixed cadence while the agent runs as a service, so
    /// the SMB-lane controller (which tails the progress file over SMB from the operator's machine) can
    /// always tell "still working" from "dead/hung". The controller ignores Heartbeat lines for phase
    /// mapping but resets its silence watchdog on any line — the same contract the WinRM lane uses.
    /// </summary>
    internal sealed class AgentHeartbeat
    {
        private readonly TimeSpan _interval;
        private readonly object _gate = new object();
        private ProgressWriter _progress;
        private Thread _thread;
        private volatile bool _stop;

        public AgentHeartbeat(TimeSpan interval) => _interval = interval;

        public void Start(ProgressWriter progress)
        {
            lock (_gate)
            {
                if (_thread != null)
                {
                    return;
                }

                _progress = progress;
                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "VivreAgentHeartbeat",
                };
                _thread.Start();
            }
        }

        public void Stop()
        {
            _stop = true;
            Thread t;
            lock (_gate)
            {
                t = _thread;
            }

            try
            {
                t?.Join(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort join; the thread is a background thread and will not block exit.
            }
        }

        private void Loop()
        {
            // Sleep in small slices so Stop() is responsive (we don't want a terminal line followed by
            // a stray heartbeat 10s later).
            var slice = TimeSpan.FromMilliseconds(250);
            while (!_stop)
            {
                TimeSpan waited = TimeSpan.Zero;
                while (!_stop && waited < _interval)
                {
                    Thread.Sleep(slice);
                    waited += slice;
                }

                if (_stop)
                {
                    return;
                }

                _progress?.Write("Heartbeat", "Worker still running…", null, 0, 0, 0, false);
            }
        }
    }
}
