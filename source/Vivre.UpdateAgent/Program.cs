using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using WUApiLib;

namespace Vivre.UpdateAgent
{
    /// <summary>
    /// The on-target Windows Update worker. Runs as SYSTEM from a one-time scheduled task,
    /// reads its settings from the config-file path in <c>args[0]</c>, drives WUA
    /// search → download → install (or uninstall), and writes the same append-only progress
    /// JSONL (<c>{"phase","message","percent","available","installed","failed","rebootPending","ts"}</c>)
    /// that <c>WuaUpdateLane</c>'s streaming controller tails over WinRM. Live percent comes
    /// straight from WUA's own download/install progress — the data BatchPatch shows.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            ProgressWriter progress = null;
            try
            {
                if (args == null || args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
                {
                    Console.Error.WriteLine("usage: Vivre.UpdateAgent <config.json>");
                    return 2;
                }

                AgentConfig config = AgentConfig.Load(args[0]);
                progress = new ProgressWriter(config.ProgressPath);

                // Never touch the servicing stack (WUA/DISM) while a reboot is already pending or a
                // servicing transaction is staged/in-flight — that's how we'd collide with the OS's
                // boot-time offline-servicing pass. Defer cleanly and let the user reboot first.
                if (BootBusyGuard.IsServicingBusy(out string busyReason))
                {
                    progress.Write("PendingReboot",
                        "Deferred — " + busyReason + ". Reboot the machine, then re-run. (Not started, to avoid colliding with Windows servicing.)",
                        100, 0, 0, 0, true);
                    return 0;
                }

                bool rebootNeeded = string.Equals(config.Mode, "Uninstall", StringComparison.OrdinalIgnoreCase)
                    ? RunUninstall(config, progress)
                    : RunInstall(config, progress);

                // Reboot (RebootAndWait) only after the operation method has returned — its WUA COM
                // objects are now out of scope. Force-release the RCWs so nothing of ours holds a
                // handle into the servicing stack as the box goes down, then schedule the restart
                // with enough delay for the controller to reap the task + temp files, and exit
                // immediately (no lingering process).
                if (rebootNeeded && config.RebootAfter)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    System.Diagnostics.Process.Start("shutdown.exe", "/r /t 20 /c \"Vivre update reboot\"");
                }

                return 0;
            }
            catch (Exception ex)
            {
                // Surface the real cause as a terminal Error line so the controller shows it
                // instead of a silent zero, then exit cleanly (the task is one-shot anyway).
                if (progress != null)
                {
                    progress.Write("Error", ex.Message, null, 0, 0, 0, false);
                }
                else
                {
                    Console.Error.WriteLine(ex);
                }

                return 1;
            }
        }

        // --- install -------------------------------------------------------

        /// <summary>Returns whether a reboot is required after the install (the caller reboots).</summary>
        private static bool RunInstall(AgentConfig config, ProgressWriter progress)
        {
            progress.Write("Searching", "Searching for updates…", 0, 0, 0, 0, false);

            var session = new UpdateSession();
            IUpdateSearcher searcher = session.CreateUpdateSearcher();
            ApplySource(searcher, config);

            string typeFilter = config.IncludeDrivers ? string.Empty : " and Type='Software'";
            ISearchResult result = searcher.Search(
                "IsInstalled=0 and IsHidden=0 and DeploymentAction='Installation'" + typeFilter);

            List<IUpdate> applicable = FilterUpdates(result.Updates, config, requireUninstallable: false);
            int total = applicable.Count;
            if (total == 0)
            {
                progress.Write("Done", "No applicable updates", 100, 0, 0, 0, false);
                return false;
            }

            progress.Write("Searching", total + " update(s) matched — starting downloads…", 5, total, 0, 0, false);

            var coll = new UpdateCollection();
            foreach (IUpdate u in applicable)
            {
                // Unattended installs must accept the EULA up front or those updates silently
                // fail to install (the old PowerShell worker missed this).
                try
                {
                    if (!u.EulaAccepted)
                    {
                        u.AcceptEula();
                    }
                }
                catch
                {
                    // Non-fatal: a refused EULA just means that update may not install.
                }

                coll.Add(u);
            }

            // --- Download the whole batch with live callback progress (mapped to 0-50%). ---
            IUpdateDownloader downloader = session.CreateUpdateDownloader();
            downloader.Updates = coll;
            RunJob(
                begin: () => downloader.BeginDownload(
                    new DownloadProgressCallback(progress, total, config, phaseLabel: "Downloading", lowPct: 0, span: 50),
                    new DownloadCompletedCallback(),
                    null),
                isCompleted: job => ((IDownloadJob)job).IsCompleted,
                pollWrite: job => WriteDownloadPoll((IDownloadJob)job, progress, total, config),
                end: job => downloader.EndDownload((IDownloadJob)job));

            // --- Install the whole batch with live callback progress (mapped to 50-100%). ---
            IUpdateInstaller installer = session.CreateUpdateInstaller();
            installer.Updates = coll;
            IInstallationResult installResult = (IInstallationResult)RunJob(
                begin: () => installer.BeginInstall(
                    new InstallProgressCallback(progress, total, config, phaseLabel: "Installing", lowPct: 50, span: 50),
                    new InstallCompletedCallback(),
                    null),
                isCompleted: job => ((IInstallationJob)job).IsCompleted,
                pollWrite: job => WriteInstallPoll((IInstallationJob)job, progress, total, config, "Installing"),
                end: job => installer.EndInstall((IInstallationJob)job));

            return Summarize(progress, installResult, coll.Count, "Installed");
        }

        // --- uninstall -----------------------------------------------------

        /// <summary>Returns whether a reboot is required after the uninstall (the caller reboots).</summary>
        private static bool RunUninstall(AgentConfig config, ProgressWriter progress)
        {
            string[] targets = config.IncludeKbs ?? new string[0];
            if (targets.Length == 0)
            {
                // The UI only enables Uninstall when removable KBs are ticked, so this is a guard.
                progress.Write("Done", "No updates selected to uninstall", 100, 0, 0, 0, false);
                return false;
            }

            progress.Write("Searching", "Finding updates to uninstall…", 0, targets.Length, 0, 0, false);

            // WUA view: map each installed update's KB → its IUpdate, so we can use WUA's own
            // uninstall (with live progress) for the ones it can remove.
            var session = new UpdateSession();
            IUpdateSearcher searcher = session.CreateUpdateSearcher();
            ApplySource(searcher, config);
            var byKb = new Dictionary<string, IUpdate>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ISearchResult result = searcher.Search("IsInstalled=1 and IsHidden=0");
                foreach (IUpdate u in result.Updates)
                {
                    try
                    {
                        if (u.KBArticleIDs != null && u.KBArticleIDs.Count > 0)
                        {
                            string kb = u.KBArticleIDs[0];
                            if (!byKb.ContainsKey(kb))
                            {
                                byKb[kb] = u;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                // If the WUA search fails we can still try DISM for every target below.
            }

            int total = targets.Length;
            int removed = 0;
            int failed = 0;
            bool rebootPending = false;

            for (int i = 0; i < total; i++)
            {
                string kb = targets[i];
                int basePct = (int)((i * 100.0) / total);
                string prefix = string.Format(CultureInfo.InvariantCulture, "Uninstalling {0} of {1} (KB{2})", i + 1, total, kb);

                bool ok = false;
                bool needReboot = false;
                string reason = null;

                // 1) Windows Update's own uninstall, when it can remove this one (live progress).
                if (byKb.TryGetValue(kb, out IUpdate wuaUpdate))
                {
                    bool isUninstallable = false;
                    try { isUninstallable = wuaUpdate.IsUninstallable; } catch { }
                    if (isUninstallable)
                    {
                        progress.Write("Installing", prefix + " — removing via Windows Update…", basePct, total, removed, failed, rebootPending);
                        (ok, needReboot, reason) = TryWuaUninstall(session, wuaUpdate, progress, i, total, kb, removed, failed);
                    }
                }

                // 2) DISM Remove-Package fallback (the supported, future-proof path) for anything
                // WUA won't/can't remove. Resolves the KB to its CBS package name.
                if (!ok)
                {
                    progress.Write("Installing", prefix + " — removing via DISM…", basePct, total, removed, failed, rebootPending);
                    (ok, needReboot, reason) = DismHelper.RemoveByKb(kb);
                }

                if (ok)
                {
                    removed++;
                    if (needReboot)
                    {
                        rebootPending = true;
                    }
                }
                else
                {
                    failed++;
                    int donePct = (int)(((i + 1) * 100.0) / total);
                    progress.Write("Installing", prefix + " — could not remove: " + (reason ?? "no supported uninstall path"), donePct, total, removed, failed, rebootPending);
                }
            }

            string summary = failed > 0
                ? string.Format(CultureInfo.InvariantCulture, "Uninstalled {0}, {1} could not be removed", removed, failed)
                : string.Format(CultureInfo.InvariantCulture, "Uninstalled {0} update(s)", removed);

            if (rebootPending)
            {
                progress.Write("PendingReboot", summary + ", reboot required", 100, total, removed, failed, true);
            }
            else
            {
                progress.Write("Done", summary, 100, total, removed, failed, false);
            }

            return rebootPending;
        }

        /// <summary>Uninstalls one update via WUA's own installer (live percent from GetProgress).
        /// Returns (succeeded, rebootRequired, failureReason).</summary>
        private static (bool ok, bool reboot, string reason) TryWuaUninstall(
            UpdateSession session, IUpdate update, ProgressWriter progress, int idx, int total, string kb, int removed, int failed)
        {
            try
            {
                var coll = new UpdateCollection();
                coll.Add(update);
                IUpdateInstaller installer = session.CreateUpdateInstaller();
                installer.Updates = coll;

                IInstallationJob job = installer.BeginUninstall(new NoOpInstallProgressCallback(), new InstallCompletedCallback(), null);
                while (!job.IsCompleted)
                {
                    System.Threading.Thread.Sleep(1000);
                    try
                    {
                        int pct = job.GetProgress().PercentComplete;
                        int overall = Clamp((int)((idx * 100.0 + pct) / total));
                        progress.Write("Installing",
                            string.Format(CultureInfo.InvariantCulture, "Uninstalling {0} of {1} (KB{2}) — {3}%", idx + 1, total, kb, pct),
                            overall, total, removed, failed, false);
                    }
                    catch { }
                }

                IInstallationResult r = installer.EndUninstall(job);
                bool ok = r.ResultCode == OperationResultCode.orcSucceeded;
                return (ok, r.RebootRequired, ok ? null : "Windows Update result code " + (int)r.ResultCode);
            }
            catch (Exception ex)
            {
                return (false, false, ex.Message);
            }
        }

        // --- shared job driver ---------------------------------------------

        /// <summary>
        /// Runs an async WUA job: begin it (passing a live progress callback), then poll
        /// IsCompleted on this thread — writing a progress line each tick as a deterministic
        /// backstop to the event-driven callback — and return the typed end-result. Begin*
        /// here always succeeds because we pass a real compiled COM callback (the exact thing
        /// PowerShell could not supply), so there is no sync-mode fallback.
        /// </summary>
        private static object RunJob(
            Func<object> begin,
            Func<object, bool> isCompleted,
            Action<object> pollWrite,
            Func<object, object> end)
        {
            object job = begin();
            while (!isCompleted(job))
            {
                System.Threading.Thread.Sleep(1500);
                try
                {
                    pollWrite(job);
                }
                catch
                {
                    // A transient progress read must not abort the job; the callback still fires.
                }
            }

            return end(job);
        }

        private static void WriteDownloadPoll(IDownloadJob job, ProgressWriter progress, int total, AgentConfig config)
        {
            IDownloadProgress p = job.GetProgress();
            int idx = p.CurrentUpdateIndex;
            int overall = Clamp((int)(p.PercentComplete / 2.0)); // 0-50% of the bar
            string mb = string.Empty;
            try
            {
                long dl = (long)p.CurrentUpdateBytesDownloaded;
                long tot = (long)p.CurrentUpdateBytesToDownload;
                if (tot > 0)
                {
                    mb = string.Format(CultureInfo.InvariantCulture, " ({0:N0}/{1:N0} MB)", dl / 1048576.0, tot / 1048576.0);
                }
            }
            catch
            {
                // Byte counters are best-effort; percent alone is enough for the bar.
            }

            string msg = string.Format(CultureInfo.InvariantCulture,
                "Downloading {0} of {1} — {2}%{3}", Math.Min(idx + 1, total), total, p.PercentComplete, mb);
            progress.Write("Downloading", msg, overall, total, 0, 0, false);
        }

        private static void WriteInstallPoll(IInstallationJob job, ProgressWriter progress, int total, AgentConfig config, string verb)
        {
            IInstallationProgress p = job.GetProgress();
            int idx = p.CurrentUpdateIndex;
            bool downloadPhaseFirst = string.Equals(verb, "Installing", StringComparison.Ordinal);
            int overall = downloadPhaseFirst
                ? Clamp(50 + (int)(p.PercentComplete / 2.0)) // install occupies 50-100%
                : Clamp(p.PercentComplete);                  // uninstall occupies the whole bar
            string msg = string.Format(CultureInfo.InvariantCulture,
                "{0} {1} of {2} — {3}%", verb, Math.Min(idx + 1, total), total, p.PercentComplete);
            progress.Write("Installing", msg, overall, total, 0, 0, false);
        }

        /// <summary>Writes the terminal Done/PendingReboot line and returns whether a reboot is
        /// required. The actual reboot is the caller's job (after COM is released).</summary>
        private static bool Summarize(
            ProgressWriter progress, IInstallationResult result, int total, string verb)
        {
            int installed = 0;
            int failed = 0;
            for (int i = 0; i < total; i++)
            {
                try
                {
                    // ResultCode 2 == orcSucceeded.
                    if (result.GetUpdateResult(i).ResultCode == OperationResultCode.orcSucceeded)
                    {
                        installed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            bool rebootPending = result.RebootRequired;
            string summary = failed > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0} {1}, {2} failed", verb, installed, failed)
                : string.Format(CultureInfo.InvariantCulture, "{0} {1} update(s)", verb, installed);

            if (rebootPending)
            {
                progress.Write("PendingReboot", summary + ", reboot required", 100, total, installed, failed, true);
            }
            else
            {
                progress.Write("Done", summary, 100, total, installed, failed, false);
            }

            return rebootPending;
        }

        // --- helpers -------------------------------------------------------

        private static void ApplySource(IUpdateSearcher searcher, AgentConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.ServiceId))
            {
                try
                {
                    // ServerSelection 3 (ssOthers) + a registered ServiceID = Microsoft Update.
                    var sm = new UpdateServiceManager();
                    sm.AddService2(config.ServiceId, 2, string.Empty);
                }
                catch
                {
                    // Registration can fail if already present / policy-blocked; the search still
                    // runs against the box default rather than aborting.
                }

                searcher.ServerSelection = ServerSelection.ssOthers;
                searcher.ServiceID = config.ServiceId;
            }
            else
            {
                searcher.ServerSelection = (ServerSelection)config.ServerSelection;
            }
        }

        private static List<IUpdate> FilterUpdates(IUpdateCollection updates, AgentConfig config, bool requireUninstallable)
        {
            string[] excludes = config.Excludes ?? new string[0];
            string[] includeKbs = config.IncludeKbs ?? new string[0];
            var applicable = new List<IUpdate>();

            foreach (IUpdate u in updates)
            {
                try
                {
                    if (requireUninstallable && !u.IsUninstallable)
                    {
                        continue;
                    }

                    string title = u.Title ?? string.Empty;
                    if (excludes.Any(x => title.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        continue;
                    }

                    if (includeKbs.Length > 0)
                    {
                        string kb = (u.KBArticleIDs != null && u.KBArticleIDs.Count > 0)
                            ? u.KBArticleIDs[0]
                            : null;
                        if (kb == null || !includeKbs.Contains(kb))
                        {
                            continue;
                        }
                    }

                    applicable.Add(u);
                }
                catch
                {
                    // A single weird update (stale COM proxy, odd subtype) is skipped, never fatal.
                }
            }

            return applicable;
        }

        internal static int Clamp(int pct) => pct < 0 ? 0 : (pct > 100 ? 100 : pct);
    }
}
