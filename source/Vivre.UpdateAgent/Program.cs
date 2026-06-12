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
            // SMB lane: the Service Control Manager launches us as LocalSystem via the service binPath
            // "Vivre.UpdateAgent.exe --service <config.json>". ServiceBase.Run must own the main thread
            // so the SCM dispatcher can drive start/stop; AgentService hosts the same work and does the
            // "I'm running" check-in that keeps StartService from timing out with error 1053.
            if (args != null && args.Length >= 2 &&
                string.Equals(args[0], "--service", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    System.ServiceProcess.ServiceBase.Run(new AgentService(args[1]));
                    return 0;
                }
                catch (Exception ex)
                {
                    // Only reachable if launched with --service outside the SCM (it requires a service
                    // dispatcher). Never the WinRM/console path.
                    Console.Error.WriteLine(ex);
                    return 1;
                }
            }

            // WinRM lane (and local): the one-time SYSTEM scheduled task runs us in plain console mode
            // as "Vivre.UpdateAgent.exe <config.json>".
            if (args == null || args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.Error.WriteLine("usage: Vivre.UpdateAgent <config.json>  (or: --service <config.json>)");
                return 2;
            }

            return RunFromConfig(args[0], heartbeat: null);
        }

        /// <summary>
        /// Loads the config, runs the requested operation (Install / Uninstall / Scan) writing the
        /// append-only progress JSONL, and handles the optional post-op reboot. Shared by the console
        /// entry (WinRM lane, <paramref name="heartbeat"/> null) and <see cref="AgentService"/> (SMB
        /// lane, which passes a heartbeat so a long quiet WUA search still proves liveness). Never
        /// throws — a fault becomes a terminal Error line and a non-zero return.
        /// </summary>
        internal static int RunFromConfig(string configPath, AgentHeartbeat heartbeat)
        {
            ProgressWriter progress = null;
            try
            {
                AgentConfig config = AgentConfigLoader.Load(configPath);
                progress = new ProgressWriter(config.ProgressPath);
                heartbeat?.Start(progress);

                // Never touch the servicing stack (WUA/DISM) while a reboot is already pending or a
                // servicing transaction is staged/in-flight — that's how we'd collide with the OS's
                // boot-time offline-servicing pass. Defer cleanly and let the user reboot first.
                // (A read-only Scan is safe during a pending reboot, so only the mutating modes defer.)
                bool isScan = string.Equals(config.Mode, "Scan", StringComparison.OrdinalIgnoreCase);
                if (!isScan && BootBusyGuard.IsServicingBusy(out string busyReason))
                {
                    progress.Write("PendingReboot",
                        "Deferred — " + busyReason + ". Reboot the machine, then re-run. (Not started, to avoid colliding with Windows servicing.)",
                        100, 0, 0, 0, true);
                    return 0;
                }

                // The agent NEVER reboots the box. It runs the operation and only REPORTS whether a reboot is
                // required (a PendingReboot progress line the controller surfaces); acting on that — the
                // reboot itself — is always a separate, explicit operator action (a confirmed Reboot Wave /
                // Reboot, or an operator-created scheduled task), never the agent's own decision. The
                // reboot-required return value is therefore intentionally discarded here.
                if (isScan)
                {
                    _ = RunScan(config, progress);
                }
                else if (string.Equals(config.Mode, "AddPackage", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RunAddPackage(config, progress);
                }
                else if (string.Equals(config.Mode, "Cleanup", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RunComponentCleanup(progress);
                }
                else if (string.Equals(config.Mode, "Uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RunUninstall(config, progress);
                }
                else
                {
                    _ = RunInstall(config, progress);
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
            finally
            {
                // Stop the heartbeat before we return so no stray Heartbeat line trails the terminal
                // (Done/Error/PendingReboot) line the controller stops on.
                heartbeat?.Stop();
            }
        }

        // --- full-package LCU (Server 2016 lane) ---------------------------

        /// <summary>
        /// The 2016 full-package lane: DISM-adds a complete cumulative-update .msu (sidesteps the broken
        /// Express delta pipeline). Runs as SYSTEM (the SCM-launched service), streams DISM's percent as
        /// Staging progress, and — crucially — verifies the stage actually registered a pending reboot
        /// before reporting reboot-ready (DISM can exit 0 as a silent no-op). NEVER reboots here; the
        /// operator commits later via the controller's Reboot Wave. Returns false (no agent-side reboot).
        ///
        /// <para>Primary path is <c>dism /add-package</c> on the .msu directly — with a current servicing
        /// stack this handles a combined SSU+LCU and its internal ordering. If beta validation shows
        /// 14393's DISM rejects the .msu, add an expand-to-cab fallback here (the one beta-contingent bit).</para>
        /// </summary>
        private static bool RunAddPackage(AgentConfig config, ProgressWriter progress)
        {
            string pkg = config.PackagePath;
            if (string.IsNullOrWhiteSpace(pkg) || !File.Exists(pkg))
            {
                throw new InvalidOperationException("AddPackage: package not found at '" + (pkg ?? "<null>") + "'.");
            }

            // Disk floor: the .msu is already here, but DISM expands it + grows WinSxS during the apply.
            // Refuse to stage on a tight system drive (run Component Cleanup first). 8 GB matches the lane gate.
            try
            {
                var sysDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                double freeGb = sysDrive.AvailableFreeSpace / 1073741824.0;
                if (freeGb < 8.0)
                {
                    progress.Write("Error",
                        "Insufficient disk: " + freeGb.ToString("0.0", CultureInfo.InvariantCulture)
                        + " GB free on the system drive (need at least 8 GB). Run Component Cleanup, then re-stage.",
                        100, 1, 0, 1, false);
                    return false;
                }
            }
            catch
            {
                // Can't read free space — let DISM surface any real disk error rather than blocking.
            }

            progress.Write("Staging", "Adding cumulative update via DISM…", 0, 1, 0, 0, false);
            int exit = RunDism("/online /add-package /packagepath:\"" + pkg + "\" /norestart /english",
                progress, "Staging", "Staging update");

            // DISM/CBS success codes: 0 = applied, 3010 = applied (reboot required), 1641 = reboot
            // initiated. 0x800f081e (CBS_E_NOT_APPLICABLE) = already installed / superseded — that's a
            // green no-op, not a failure.
            const int ERROR_SUCCESS_REBOOT_REQUIRED = 3010;
            const int ERROR_SUCCESS_REBOOT_INITIATED = 1641;
            const int CBS_E_NOT_APPLICABLE = unchecked((int)0x800F081E);

            if (exit == CBS_E_NOT_APPLICABLE)
            {
                progress.Write("Done", "Already installed — this CU is not applicable (no change).", 100, 1, 1, 0, false);
                return false;
            }

            bool ok = exit == 0 || exit == ERROR_SUCCESS_REBOOT_REQUIRED || exit == ERROR_SUCCESS_REBOOT_INITIATED;
            if (!ok)
            {
                progress.Write("Error",
                    "DISM add-package failed (exit 0x" + exit.ToString("X8") + "). See %WINDIR%\\Logs\\DISM\\dism.log and CBS.log on the host.",
                    100, 1, 0, 1, false);
                return false;
            }

            // Success exit, but confirm a pending reboot was actually registered — otherwise the "apply"
            // was a silent no-op and the box is NOT reboot-ready (do not mislabel it).
            if (!IsCbsRebootPending())
            {
                progress.Write("Error",
                    "DISM reported success but no pending reboot was registered — the update did not stage. Re-check the package and component store.",
                    100, 1, 0, 1, false);
                return false;
            }

            progress.Write("PendingReboot",
                "Staged — reboot-ready. Run a Reboot Wave to commit the update.",
                100, 1, 1, 0, true);
            return false; // staging never auto-reboots; the operator's Reboot Wave commits it
        }

        /// <summary>
        /// Runs <c>dism.exe</c> with <paramref name="arguments"/>, live-streaming the latest percent as
        /// progress under <paramref name="phase"/> with the message <paramref name="prefix"/>. DISM redraws
        /// its bar with backspace/CR, so we read stdout in chunks and pull the most-recent "NN.N%" rather
        /// than whole lines; stderr is drained separately so a full pipe can't deadlock. Returns the exit
        /// code. Shared by AddPackage (the LCU stage) and Component Cleanup. /english forces a parseable
        /// percent format on localized hosts.
        /// </summary>
        private static int RunDism(string arguments, ProgressWriter progress, string phase, string prefix)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var p = new System.Diagnostics.Process { StartInfo = psi })
            {
                p.ErrorDataReceived += (s, e) => { /* drain stderr so the pipe can't fill and deadlock */ };
                p.Start();
                p.BeginErrorReadLine();

                var sb = new StringBuilder();
                var chunk = new char[256];
                int lastPct = -1;
                int n;
                while ((n = p.StandardOutput.Read(chunk, 0, chunk.Length)) > 0)
                {
                    sb.Append(chunk, 0, n);
                    var matches = System.Text.RegularExpressions.Regex.Matches(sb.ToString(), @"(\d{1,3}(?:\.\d+)?)%");
                    if (matches.Count > 0)
                    {
                        string last = matches[matches.Count - 1].Groups[1].Value;
                        if (double.TryParse(last, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                        {
                            int pct = (int)Math.Round(val);
                            if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                progress.Write(phase, prefix + " — " + pct + "%", pct, 1, 0, 0, false);
                            }
                        }
                    }

                    // Keep the buffer bounded (we only ever need the tail to find the latest percent).
                    if (sb.Length > 4096)
                    {
                        sb.Remove(0, sb.Length - 1024);
                    }
                }

                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>
        /// Reclaims component-store space: <c>DISM /Online /Cleanup-Image /StartComponentCleanup</c>,
        /// streaming percent. Never reboots. (RunFromConfig's BootBusyGuard already refuses this when a
        /// reboot is pending / servicing is in progress, so it won't collide with a staged update.)
        /// </summary>
        private static bool RunComponentCleanup(ProgressWriter progress)
        {
            progress.Write("Scanning", "Cleaning the component store (DISM /StartComponentCleanup)…", 0, 1, 0, 0, false);
            int exit = RunDism("/online /cleanup-image /startcomponentcleanup /english",
                progress, "Scanning", "Cleaning component store");

            const int ERROR_SUCCESS_REBOOT_REQUIRED = 3010;
            if (exit == 0)
            {
                progress.Write("Done", "Component cleanup complete.", 100, 1, 1, 0, false);
                return false;
            }

            if (exit == ERROR_SUCCESS_REBOOT_REQUIRED)
            {
                progress.Write("PendingReboot", "Component cleanup complete — a reboot is recommended.", 100, 1, 1, 0, true);
                return false;
            }

            progress.Write("Error",
                "Component cleanup failed (exit 0x" + exit.ToString("X8") + "). See %WINDIR%\\Logs\\DISM\\dism.log.",
                100, 1, 0, 1, false);
            return false;
        }

        /// <summary>True when the CBS "RebootPending" key exists — the authoritative "an update is staged
        /// and a reboot will commit it" signal. Its presence after a DISM add is how we know the stage
        /// actually applied (vs. a silent no-op).</summary>
        private static bool IsCbsRebootPending()
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            {
                return key != null;
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
            // Per-KB failure reasons, kept so the FINAL summary can report WHY each one failed —
            // the per-iteration "could not remove…" line below is otherwise instantly overwritten
            // by the summary, so the user never sees the reason without this.
            var failures = new List<string>();

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
                        progress.Write("Uninstalling", prefix + " — removing via Windows Update…", basePct, total, removed, failed, rebootPending);
                        (ok, needReboot, reason) = TryWuaUninstall(session, wuaUpdate, progress, i, total, kb, removed, failed);
                    }
                }

                // 2) DISM Remove-Package fallback (the supported, future-proof path) for anything
                // WUA won't/can't remove. Resolves the KB to its CBS package name.
                if (!ok)
                {
                    progress.Write("Uninstalling", prefix + " — removing via DISM…", basePct, total, removed, failed, rebootPending);
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
                    string why = reason ?? "no supported uninstall path";
                    failures.Add("KB" + kb + ": " + why);
                    int donePct = (int)(((i + 1) * 100.0) / total);
                    progress.Write("Uninstalling", prefix + " — could not remove: " + why, donePct, total, removed, failed, rebootPending);
                }
            }

            // Join the reasons onto one line (no newlines, so the JSONL stays one object per line);
            // the grid trims it but the row tooltip shows it in full.
            string reasons = failures.Count > 0 ? " — " + string.Join(" · ", failures) : string.Empty;

            // Nothing came off: report it as an Error (red), not a green "Done". Most often this is
            // Windows refusing to remove permanent/cumulative updates (0x800F0825) — by design, but
            // the user should see that it failed and why.
            if (removed == 0 && failed > 0)
            {
                string failMsg = string.Format(CultureInfo.InvariantCulture, "Uninstalled 0 — {0} could not be removed", failed) + reasons;
                progress.Write("Error", failMsg, 100, total, removed, failed, false);
                return false;
            }

            string summary = failed > 0
                ? string.Format(CultureInfo.InvariantCulture, "Uninstalled {0}, {1} could not be removed", removed, failed) + reasons
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
                        progress.Write("Uninstalling",
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

        // --- scan ----------------------------------------------------------

        /// <summary>
        /// Read-only WUA search for the SMB lane (the WinRM lane scans over PowerShell instead). Mirrors
        /// <c>WuaUpdateLane.BuildScanScript</c>: Applicable scope returns IsInstalled=0 rows, Installed
        /// scope returns IsInstalled=1 rows with their install dates from WUA history. Writes the update
        /// array to <see cref="AgentConfig.ResultPath"/> for the controller to read back over SMB, then a
        /// terminal Done line. Never reboots (returns false). Excludes are applied controller-side
        /// (matching the WinRM scan), so the agent returns the raw list.
        /// </summary>
        private static bool RunScan(AgentConfig config, ProgressWriter progress)
        {
            bool installedScope = string.Equals(config.Scope, "Installed", StringComparison.OrdinalIgnoreCase);
            progress.Write("Searching",
                installedScope ? "Scanning installed updates…" : "Scanning for updates…",
                0, 0, 0, 0, false);

            var session = new UpdateSession();
            IUpdateSearcher searcher = session.CreateUpdateSearcher();
            ApplySource(searcher, config);

            string typeFilter = config.IncludeDrivers ? string.Empty : " and Type='Software'";
            string installedFilter = installedScope ? "IsInstalled=1" : "IsInstalled=0";
            ISearchResult result = searcher.Search(installedFilter + " and IsHidden=0" + typeFilter);

            // Installed scope: map each installed update to its most recent install date from WUA
            // history, by Identity UpdateID and (fallback) by KB — same dual keying as the PS scan,
            // because WUSA-installed updates sometimes show a different UpdateID in history.
            var datesById = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var datesByKb = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            if (installedScope)
            {
                BuildHistoryDateMaps(searcher, datesById, datesByKb);
            }

            var rows = new List<Dictionary<string, object>>();
            foreach (IUpdate u in result.Updates)
            {
                // Wrap each update so one stale COM proxy or odd subtype is skipped, not fatal.
                try
                {
                    string title = u.Title;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    string kb = (u.KBArticleIDs != null && u.KBArticleIDs.Count > 0) ? u.KBArticleIDs[0] : null;

                    double size = 0;
                    try { size = Math.Round((double)u.MaxDownloadSize / 1048576.0, 1); } catch { }

                    bool isUninstallable = true;
                    if (installedScope)
                    {
                        try { isUninstallable = u.IsUninstallable; } catch { isUninstallable = false; }
                    }

                    bool isDownloaded = false;
                    try { isDownloaded = u.IsDownloaded; } catch { }

                    string installedAt = null;
                    if (installedScope)
                    {
                        DateTime found;
                        string uid = null;
                        try { uid = u.Identity != null ? u.Identity.UpdateID : null; } catch { }
                        if (uid != null && datesById.TryGetValue(uid, out found))
                        {
                            installedAt = found.ToString("o", CultureInfo.InvariantCulture);
                        }
                        else if (kb != null && datesByKb.TryGetValue(kb, out found))
                        {
                            installedAt = found.ToString("o", CultureInfo.InvariantCulture);
                        }
                    }

                    rows.Add(new Dictionary<string, object>
                    {
                        ["Title"] = title,
                        ["KB"] = kb,
                        ["IsDownloaded"] = isDownloaded,
                        ["SizeMb"] = size,
                        ["IsUninstallable"] = isUninstallable,
                        ["InstalledAt"] = installedAt,
                    });
                }
                catch
                {
                    // Skip an update that can't be read.
                }
            }

            WriteScanResult(config.ResultPath, rows);

            string message = installedScope
                ? (rows.Count == 0 ? "No installed updates" : rows.Count + " installed update(s)")
                : (rows.Count == 0 ? "Up to date" : rows.Count + " update(s) available");
            progress.Write("Done", message, 100, rows.Count, 0, 0, false);
            return false;
        }

        /// <summary>Fills the UpdateID→date and KB→date maps from WUA's install history (Operation 1),
        /// keeping the most recent date per key. Best-effort: a history read failure leaves the maps
        /// empty (rows then carry no install date, exactly like the PS scan's try/catch).</summary>
        private static void BuildHistoryDateMaps(
            IUpdateSearcher searcher,
            Dictionary<string, DateTime> datesById,
            Dictionary<string, DateTime> datesByKb)
        {
            try
            {
                int count = searcher.GetTotalHistoryCount();
                if (count <= 0)
                {
                    return;
                }

                IUpdateHistoryEntryCollection history = searcher.QueryHistory(0, count);
                foreach (IUpdateHistoryEntry h in history)
                {
                    try
                    {
                        // Operation 1 == uoInstallation (the embedded-interop enum name isn't surfaced,
                        // so compare the underlying value, exactly like the PS scan's "-ne 1").
                        if ((int)h.Operation != 1 || h.UpdateIdentity == null)
                        {
                            continue;
                        }

                        string id = h.UpdateIdentity.UpdateID;
                        if (!string.IsNullOrEmpty(id) &&
                            (!datesById.TryGetValue(id, out DateTime prev) || prev < h.Date))
                        {
                            datesById[id] = h.Date;
                        }

                        // Pull the KB out of the title ("...(KB1234567)...") for the fallback map.
                        string kb = ExtractKb(h.Title);
                        if (kb != null &&
                            (!datesByKb.TryGetValue(kb, out DateTime prevKb) || prevKb < h.Date))
                        {
                            datesByKb[kb] = h.Date;
                        }
                    }
                    catch
                    {
                        // Skip an unreadable history entry.
                    }
                }
            }
            catch
            {
                // No history available — leave the maps empty.
            }
        }

        /// <summary>Pulls the digits from the first "KB#######" token in a title, or null.</summary>
        private static string ExtractKb(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return null;
            }

            int i = title.IndexOf("KB", StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                return null;
            }

            int start = i + 2;
            int end = start;
            while (end < title.Length && char.IsDigit(title[end]))
            {
                end++;
            }

            return end > start ? title.Substring(start, end - start) : null;
        }

        /// <summary>Serializes the scan rows to <paramref name="path"/> as a JSON array (JavaScriptSerializer
        /// on net48; the controller reads it with System.Text.Json — the same cross-framework contract as
        /// the config). A null/empty path means the caller didn't ask for a result file.</summary>
        private static void WriteScanResult(string path, List<Dictionary<string, object>> rows)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string json = serializer.Serialize(rows);
            System.IO.File.WriteAllText(path, json, new UTF8Encoding(false));
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
