using System;
using System.Globalization;
using WUApiLib;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// WUA's real download-progress callback — fires on a COM thread as bytes move, giving the
    /// smooth live percent BatchPatch shows. We map WUA's overall 0-100% into our bar slice
    /// (<paramref name="lowPct"/>..<paramref name="lowPct"/>+<paramref name="span"/>) and emit a
    /// progress line. Passing a real compiled callback here is exactly what makes BeginDownload
    /// succeed where PowerShell's null-callback attempt fell back to sync mode.
    /// </summary>
    internal sealed class DownloadProgressCallback : IDownloadProgressChangedCallback
    {
        private readonly ProgressWriter _progress;
        private readonly int _total;
        private readonly AgentConfig _config;
        private readonly string _phaseLabel;
        private readonly int _lowPct;
        private readonly int _span;

        public DownloadProgressCallback(ProgressWriter progress, int total, AgentConfig config, string phaseLabel, int lowPct, int span)
        {
            _progress = progress;
            _total = total;
            _config = config;
            _phaseLabel = phaseLabel;
            _lowPct = lowPct;
            _span = span;
        }

        public void Invoke(IDownloadJob downloadJob, IDownloadProgressChangedCallbackArgs callbackArgs)
        {
            try
            {
                IDownloadProgress p = callbackArgs.Progress;
                int overall = Program.Clamp(_lowPct + (int)(p.PercentComplete * _span / 100.0));
                string mb = string.Empty;
                try
                {
                    long dl = (long)p.CurrentUpdateBytesDownloaded;
                    long tot = (long)p.CurrentUpdateBytesToDownload;
                    if (tot > 0)
                    {
                        // WUA frequently reports CurrentUpdateBytesDownloaded = 0 for the entire
                        // download even while the percent climbs. Derive a displayed-bytes estimate
                        // from the per-update percent so the counter moves with the bar.
                        if (dl <= 0)
                        {
                            dl = (long)(tot * p.CurrentUpdatePercentComplete / 100.0);
                            if (dl < 0) dl = 0;
                            if (dl > tot) dl = tot;
                        }
                        mb = string.Format(CultureInfo.InvariantCulture, " ({0:N0}/~{1:N0} MB)", dl / 1048576.0, tot / 1048576.0);
                    }
                }
                catch
                {
                    // Byte counters best-effort.
                }

                string msg = string.Format(CultureInfo.InvariantCulture,
                    "{0} {1} of {2} — {3}%{4}", _phaseLabel, Math.Min(p.CurrentUpdateIndex + 1, _total), _total, p.PercentComplete, mb);
                _progress.Write("Downloading", msg, overall, _total, 0, 0, false);
            }
            catch
            {
                // A callback must never throw back into WUA's COM machinery.
            }
        }
    }

    /// <summary>WUA's real install/uninstall progress callback — same idea for the install phase.</summary>
    internal sealed class InstallProgressCallback : IInstallationProgressChangedCallback
    {
        private readonly ProgressWriter _progress;
        private readonly int _total;
        private readonly AgentConfig _config;
        private readonly string _phaseLabel;
        private readonly int _lowPct;
        private readonly int _span;

        public InstallProgressCallback(ProgressWriter progress, int total, AgentConfig config, string phaseLabel, int lowPct, int span)
        {
            _progress = progress;
            _total = total;
            _config = config;
            _phaseLabel = phaseLabel;
            _lowPct = lowPct;
            _span = span;
        }

        public void Invoke(IInstallationJob installationJob, IInstallationProgressChangedCallbackArgs callbackArgs)
        {
            try
            {
                IInstallationProgress p = callbackArgs.Progress;
                int overall = Program.Clamp(_lowPct + (int)(p.PercentComplete * _span / 100.0));
                string msg = string.Format(CultureInfo.InvariantCulture,
                    "{0} {1} of {2} — {3}%", _phaseLabel, Math.Min(p.CurrentUpdateIndex + 1, _total), _total, p.PercentComplete);
                _progress.Write("Installing", msg, overall, _total, 0, 0, false);
            }
            catch
            {
                // Never throw back into WUA.
            }
        }
    }

    /// <summary>No-op download-completed callback — supplied so BeginDownload has a non-null sink.</summary>
    internal sealed class DownloadCompletedCallback : IDownloadCompletedCallback
    {
        public void Invoke(IDownloadJob downloadJob, IDownloadCompletedCallbackArgs callbackArgs)
        {
        }
    }

    /// <summary>No-op install/uninstall progress callback — supplied to BeginUninstall when we drive
    /// progress by polling GetProgress() ourselves (per-KB uninstall), so it stays a non-null sink
    /// without emitting its own batch-indexed messages.</summary>
    internal sealed class NoOpInstallProgressCallback : IInstallationProgressChangedCallback
    {
        public void Invoke(IInstallationJob installationJob, IInstallationProgressChangedCallbackArgs callbackArgs)
        {
        }
    }

    /// <summary>No-op install-completed callback — supplied so BeginInstall/BeginUninstall has a non-null sink.</summary>
    internal sealed class InstallCompletedCallback : IInstallationCompletedCallback
    {
        public void Invoke(IInstallationJob installationJob, IInstallationCompletedCallbackArgs callbackArgs)
        {
        }
    }
}
