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
                    // Phase "Deferred" (NOT "PendingReboot"): a servicing-busy refusal must NOT look
                    // like a successful stage's reboot-pending. The host maps "Deferred" to a
                    // non-staged "reboot first, then re-run" state — a deferral is never a success.
                    // This covers every mutating mode (AddPackage/Cleanup/Uninstall/Install).
                    progress.Write("Deferred",
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

            // 14393's online DISM rejects .msu payloads (0x80070032 "DISM does not support installing MSU
            // files online") — extract the installable .cab(s) with expand.exe and add those instead. A
            // combined SSU+LCU .msu yields a servicing-stack cab AND the LCU cab (plus WSUSSCAN.cab, which
            // is scan metadata and never installable); the SSU must be added before the LCU.
            string[] cabs;
            bool extracted = false;
            if (pkg.EndsWith(".msu", StringComparison.OrdinalIgnoreCase))
            {
                cabs = ExtractInstallableCabs(pkg, progress);
                if (cabs == null)
                {
                    return false; // extraction failed — already reported as an Error line
                }

                extracted = true;
            }
            else
            {
                cabs = new[] { pkg }; // already a .cab — add it directly
            }

            // DISM/CBS success codes: 0 = applied, 3010 = applied (reboot required), 1641 = reboot
            // initiated. 0x800f081e (CBS_E_NOT_APPLICABLE) = already installed / superseded — that's a
            // green no-op for that piece, not a failure.
            const int ERROR_SUCCESS_REBOOT_REQUIRED = 3010;
            const int ERROR_SUCCESS_REBOOT_INITIATED = 1641;
            const int CBS_E_NOT_APPLICABLE = unchecked((int)0x800F081E);

            try
            {
                progress.Write("Staging", "Adding cumulative update via DISM…", 0, 1, 0, 0, false);

                int applied = 0;
                for (int i = 0; i < cabs.Length; i++)
                {
                    string cab = cabs[i];
                    // Phase label: a servicing-stack (SSU) cab installs first and is named so the operator
                    // sees what each step is doing; everything else is the LCU itself.
                    bool isSsu = Path.GetFileName(cab).IndexOf("SSU", StringComparison.OrdinalIgnoreCase) >= 0;
                    string label = isSsu ? "Installing servicing stack" : "Staging update";
                    int exit = RunDism("/online /add-package /packagepath:\"" + cab + "\" /norestart /english",
                        progress, "Staging", label);

                    if (exit == CBS_E_NOT_APPLICABLE)
                    {
                        continue; // this piece is already installed/superseded — carry on with the rest
                    }

                    bool ok = exit == 0 || exit == ERROR_SUCCESS_REBOOT_REQUIRED || exit == ERROR_SUCCESS_REBOOT_INITIATED;
                    if (!ok)
                    {
                        progress.Write("Error",
                            "DISM add-package failed on '" + Path.GetFileName(cab) + "' (exit 0x" + exit.ToString("X8")
                            + "). See %WINDIR%\\Logs\\DISM\\dism.log and CBS.log on the host.",
                            100, 1, 0, 1, false);
                        return false;
                    }

                    applied++;
                }

                if (applied == 0)
                {
                    progress.Write("Done", "Already installed — this CU is not applicable (no change).", 100, 1, 1, 0, false);
                    return false;
                }

                // Success exits, but confirm a pending reboot was actually registered — otherwise the
                // "apply" was a silent no-op and the box is NOT reboot-ready (do not mislabel it).
                progress.Write("Staging", "Waiting for reboot-ready signal…", 100, 1, 0, 0, false);
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
            finally
            {
                // Don't leave ~1.6 GB of extracted cabs behind; the controller deletes the .msu itself.
                if (extracted)
                {
                    foreach (string cab in cabs)
                    {
                        try { File.Delete(cab); }
                        catch (IOException) { /* locked leftover — the next run's pre-clean reaps it */ }
                        catch (UnauthorizedAccessException) { /* same: best-effort, reaped next run */ }
                    }
                }
            }
        }

        /// <summary>
        /// Extracts the installable .cab payload(s) from <paramref name="msuPath"/> into the same directory
        /// with <c>expand.exe</c> and returns them in install order — any servicing-stack (SSU) cab first
        /// (a combined SSU+LCU package's stack update must apply before the LCU), then largest-first.
        /// WSUSSCAN.cab (scan metadata, never installable) is excluded. Pre-deletes stale .cab files so a
        /// crashed prior run can't leak the wrong package into the glob. Returns null (after writing an
        /// Error line) when extraction fails or yields nothing installable.
        /// </summary>
        private static string[] ExtractInstallableCabs(string msuPath, ProgressWriter progress)
        {
            string dir = Path.GetDirectoryName(msuPath);

            // Pre-clean: any .cab here is a leftover from a crashed prior run (the per-host CBS guard means
            // no concurrent AddPackage on this box, and scans never produce cabs).
            foreach (string stale in Directory.GetFiles(dir, "*.cab"))
            {
                try { File.Delete(stale); }
                catch (IOException) { /* locked — KB-specific names keep a stale file from masking this run's cab */ }
                catch (UnauthorizedAccessException) { /* same: best-effort pre-clean */ }
            }

            progress.Write("Staging", "Extracting update…", 0, 1, 0, 0, false);

            int exit = RunTool("expand.exe", "\"" + msuPath + "\" -F:*.cab \"" + dir + "\"");
            if (exit != 0)
            {
                progress.Write("Error",
                    "expand.exe failed to extract the update package (exit " + exit + "). The .msu may be corrupt — re-download it from the Catalog.",
                    100, 1, 0, 1, false);
                return null;
            }

            string[] cabs = Directory.GetFiles(dir, "*.cab")
                .Where(f => !string.Equals(Path.GetFileName(f), "WSUSSCAN.cab", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (cabs.Length == 0)
            {
                progress.Write("Error",
                    "No installable .cab was found inside the .msu after extraction. Re-download it from the Catalog.",
                    100, 1, 0, 1, false);
                return null;
            }

            return cabs
                .OrderBy(f => Path.GetFileName(f).IndexOf("SSU", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenByDescending(f => new FileInfo(f).Length)
                .ToArray();
        }

        /// <summary>Runs a console tool silently, draining both pipes so neither can fill and deadlock, and
        /// returns its exit code. (RunDism stays separate — it live-parses DISM's percent redraws.)</summary>
        private static int RunTool(string fileName, string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var p = new System.Diagnostics.Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { /* drain stdout so the pipe can't fill and deadlock */ };
                p.ErrorDataReceived += (s, e) => { /* drain stderr so the pipe can't fill and deadlock */ };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
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

        // --- component cleanup (Server 2016 lane) --------------------------

        // Liveness tuning for component cleanup (named so they're trivially adjustable). On a backlogged
        // 2016 box DISM's percent stalls for long silent reclamation phases, so we emit a DISPLAYED
        // "still working" line on a cadence. The "working" signal is the whole servicing stack (dism +
        // TiWorker + TrustedInstaller) burning CPU OR the CBS log growing; "no activity for the whole
        // window" is a non-terminal DISPLAY FLAG ("looks stalled — may still be working"), never a kill.
        private static readonly TimeSpan HeartbeatCadence = TimeSpan.FromSeconds(20);
        // StallWindow is now a FLAG threshold, not a kill threshold — nothing is killed, so it's safe to
        // lower later if a tighter "looks stalled" hint is wanted.
        private static readonly TimeSpan StallWindow = TimeSpan.FromMinutes(45);
        // A summed-stack CPU delta over the high-water mark must exceed this to count as activity and reset
        // the stall clock — filters idle-thread noise from a genuinely advancing servicing stack.
        private static readonly TimeSpan CpuAdvanceThreshold = TimeSpan.FromSeconds(1);

        // The servicing-stack processes whose summed CPU is the "stack is working" signal. dism.exe is a
        // thin client; the heavy reclamation runs in TiWorker / TrustedInstaller, so dism alone can idle.
        private static readonly string[] ServicingStackProcessNames = { "dism", "TiWorker", "TrustedInstaller" };

        /// <summary>
        /// Reclaims component-store space: <c>DISM /Online /Cleanup-Image /StartComponentCleanup</c>,
        /// with a liveness sampler (see <see cref="RunDismWithLiveness"/>). Never reboots. (RunFromConfig's
        /// BootBusyGuard already refuses this when a reboot is pending / servicing is in progress, so it
        /// won't collide with a staged update.)
        /// </summary>
        private static bool RunComponentCleanup(ProgressWriter progress)
        {
            progress.Write("Cleaning", "Cleaning the component store (DISM /StartComponentCleanup)…", 0, 1, 0, 0, false);
            int exit = RunDismWithLiveness("/online /cleanup-image /startcomponentcleanup /english", progress);

            const int ERROR_SUCCESS_REBOOT_REQUIRED = 3010;
            const int ERROR_ACCESS_DENIED = 5;
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

            if (exit == ERROR_ACCESS_DENIED)
            {
                // Access-denied (0x80070005 in CBS): the CSI scavenge cleared the backlog but couldn't commit
                // the deletion of a locked remainder — commonly security software (AV/EDR) holding WinSxS
                // handles. This is a success-with-caveat, NOT a hard failure. Emit the RAW facts (exit code,
                // whether the read-only AnalyzeComponentStore parsed, reclaimable count) and let the
                // controller's ComponentCleanupClassifier build the operator-facing wording — the agent
                // builds none. AnalyzeComponentStore is read-only; nothing here reboots.
                bool analyzeOk = TryGetReclaimablePackages(out int reclaimable);
                progress.WriteCleanupFacts("Done",
                    "StartComponentCleanup exit 0x" + exit.ToString("X8")
                        + " (access denied); analyzeOk=" + analyzeOk
                        + " reclaimable=" + (analyzeOk ? reclaimable.ToString(CultureInfo.InvariantCulture) : "?"),
                    exit, analyzeOk, analyzeOk ? reclaimable : (int?)null);
                return false;
            }

            progress.Write("Error",
                "Component cleanup failed (exit 0x" + exit.ToString("X8") + "). See %WINDIR%\\Logs\\DISM\\dism.log.",
                100, 1, 0, 1, false);
            return false;
        }

        /// <summary>
        /// Runs a read-only <c>DISM /Online /Cleanup-Image /AnalyzeComponentStore /english</c> and parses the
        /// "Number of Reclaimable Packages : N" line. Returns true with <paramref name="reclaimable"/> set when
        /// DISM ran and the count parsed; false (count -1) when it couldn't run or the line wasn't found — so
        /// the caller can report the remaining count as unknown. Never reboots; /english forces the parseable
        /// label on localized hosts. Doubles as a store-health check: a store that can't be analyzed may be
        /// genuinely broken.
        /// </summary>
        private static bool TryGetReclaimablePackages(out int reclaimable)
        {
            reclaimable = -1;
            try
            {
                string stdout = RunDismCaptureStdout("/online /cleanup-image /analyzecomponentstore /english");
                if (string.IsNullOrEmpty(stdout))
                {
                    return false;
                }

                var m = System.Text.RegularExpressions.Regex.Match(
                    stdout, @"Number of Reclaimable Packages\s*:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    reclaimable = n;
                    return true;
                }

                return false;
            }
            catch
            {
                // Best-effort store-health probe: any failure to launch/read DISM means the count is unknown,
                // which the classifier reports honestly ("a locked remainder couldn't be removed"). Never throw
                // into the cleanup terminal path.
                return false;
            }
        }

        /// <summary>Runs <c>dism.exe</c> with <paramref name="arguments"/>, capturing full stdout (stderr is
        /// drained so the pipe can't deadlock). Used by the read-only AnalyzeComponentStore probe.</summary>
        private static string RunDismCaptureStdout(string arguments)
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

            var sb = new StringBuilder();
            using (var p = new System.Diagnostics.Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) { sb.AppendLine(e.Data); } };
                p.ErrorDataReceived += (s, e) => { /* drain stderr so the pipe can't fill and deadlock */ };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return sb.ToString();
            }
        }

        /// <summary>
        /// A cleanup-specific DISM runner with a liveness sampler. Like <see cref="RunDism"/> it parses
        /// dism's redrawn percent, but where RunDism only emits when the percent changes (so it goes
        /// SILENT during 2016's long reclamation phases) this runner drives a separate timer loop that,
        /// independent of any stdout, every <see cref="HeartbeatCadence"/>:
        ///   • emits a DISPLAYED "Cleaning" progress line with the compact elapsed time + the latest known
        ///     percent (the elapsed + working/stalled hint is the real liveness; the percent is decoration);
        ///   • samples the WHOLE servicing stack's CPU — the summed
        ///     <see cref="System.Diagnostics.Process.TotalProcessorTime"/> across the running
        ///     <c>dism</c> / <c>TiWorker</c> / <c>TrustedInstaller</c> processes — tracking a high-water
        ///     mark, AND the CBS log's newest write time (<c>%WINDIR%\Logs\CBS\CBS.log</c> +
        ///     <c>CbsPersist_*.log</c>). The cleanup is WORKING this tick if stack CPU advanced OR the log
        ///     grew; the stall clock resets on any activity. If NEITHER has shown activity for the whole
        ///     <see cref="StallWindow"/> the line is flagged "looks stalled (may still be working)" — a
        ///     NON-TERMINAL DISPLAY hint. Nothing is killed and no terminal Error is written; the next
        ///     active tick clears the flag.
        ///
        /// <para>Why the whole stack, not dism alone: dism.exe is a thin client — the heavy reclamation
        /// runs in TiWorker / TrustedInstaller, so dism can sit at ~0% CPU for long LEGITIMATE stretches.
        /// Sampling dism alone would false-flag (and, before this revision, KILL) a working cleanup. The
        /// log signal is a second proof-of-life robust to a momentarily-flat CPU read.</para>
        ///
        /// <para>Percent parsing runs off the async <c>OutputDataReceived</c> pump (so it never blocks the
        /// sampler), and the sampler runs on its OWN thread (so a fully silent dism — no stdout at all —
        /// is still sampled on cadence). DISM redraws its bar with CR, which the event splits into lines we
        /// scan for the most-recent "NN.N%". The runner always returns dism's real <c>ExitCode</c> — there
        /// is no kill and no sentinel.</para>
        ///
        /// <para>The stall DECISION (<see cref="CleanupLiveness.IsStalled"/> /
        /// <see cref="CleanupLiveness.IsWorking"/> / <see cref="CleanupLiveness.LogAdvanced"/>) and elapsed
        /// formatting are the pure, linked, unit-tested <see cref="CleanupLiveness"/> predicates; only the
        /// live CPU/process + log file I/O lives here.</para>
        /// </summary>
        private static int RunDismWithLiveness(string arguments, ProgressWriter progress)
        {
            const string phase = "Cleaning";

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
                // Latest parsed dism percent (-1 until first seen). Written by the async stdout pump,
                // read by the sampler thread — volatile is enough for a single int snapshot.
                int latestPct = -1;

                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data))
                    {
                        return;
                    }

                    // DISM redraws "NN.N%" repeatedly; take the most-recent one on this line.
                    var matches = System.Text.RegularExpressions.Regex.Matches(e.Data, @"(\d{1,3}(?:\.\d+)?)%");
                    if (matches.Count > 0 &&
                        double.TryParse(matches[matches.Count - 1].Groups[1].Value,
                            NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                    {
                        int pct = (int)Math.Round(val);
                        if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
                        System.Threading.Volatile.Write(ref latestPct, pct);
                    }
                };
                p.ErrorDataReceived += (s, e) => { /* drain stderr so the pipe can't fill and deadlock */ };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // The liveness sampler runs on its own thread so it fires on cadence even while dism emits
                // NOTHING on stdout (the exact 2016 stall). It exits only when dism does (WaitForExit below
                // sets the stop event) — it never kills anything; a stall is a display flag, not an end.
                DateTime startedUtc = DateTime.UtcNow;
                var stop = new System.Threading.ManualResetEventSlim(false);
                var sampler = new System.Threading.Thread(() =>
                {
                    DateTime lastActivityUtc = startedUtc;
                    TimeSpan cpuHighWater = TimeSpan.Zero;
                    DateTime logMaxWriteUtc = SampleCbsLogMaxWriteUtc(); // baseline before any work

                    // Wait a full cadence between samples; Wait returns true if signalled to stop.
                    while (!stop.Wait(HeartbeatCadence))
                    {
                        DateTime nowUtc = DateTime.UtcNow;

                        // Signal 1 — servicing-stack CPU. Sum TotalProcessorTime across the running
                        // dism / TiWorker / TrustedInstaller processes; a MEANINGFUL advance over the
                        // high-water mark (filters idle-thread noise) is CPU activity. dism alone can idle
                        // while TiWorker burns the CPU, so sampling the whole stack is what makes this safe.
                        TimeSpan stackCpu = SampleServicingStackCpu();
                        bool stackCpuAdvanced =
                            CleanupLiveness.CpuAdvancedMeaningfully(cpuHighWater, stackCpu, CpuAdvanceThreshold);
                        if (stackCpuAdvanced)
                        {
                            cpuHighWater = stackCpu;
                        }

                        // Signal 2 — CBS log growth. The servicing stack appends to CBS.log (rolling over to
                        // CbsPersist_*.log when large) while it works, so a newer max last-write means the
                        // stack did work even if its CPU read was momentarily flat.
                        DateTime newLogMaxWriteUtc = SampleCbsLogMaxWriteUtc();
                        bool logAdvanced = CleanupLiveness.LogAdvanced(logMaxWriteUtc, newLogMaxWriteUtc);
                        if (newLogMaxWriteUtc > logMaxWriteUtc)
                        {
                            logMaxWriteUtc = newLogMaxWriteUtc;
                        }

                        // Working this tick if EITHER signal fired; reset the stall clock on any activity.
                        bool working = CleanupLiveness.IsWorking(stackCpuAdvanced, logAdvanced);
                        if (working)
                        {
                            lastActivityUtc = nowUtc;
                        }

                        // Stall is a NON-TERMINAL DISPLAY FLAG: NEITHER signal active for the whole window.
                        // The line says so but the sampler + dism keep running; the next active tick clears
                        // it. No kill, no terminal Error.
                        bool stalled = CleanupLiveness.IsStalled(nowUtc - lastActivityUtc, StallWindow);

                        // Emit the DISPLAYED liveness line (phase "Cleaning" so the host shows it — NOT a
                        // "Heartbeat" line, which the host ignores for display).
                        int pctSnapshot = System.Threading.Volatile.Read(ref latestPct);
                        string elapsed = CleanupLiveness.FormatElapsed(nowUtc - startedUtc);
                        string state = stalled
                            ? "looks stalled (may still be working) — check the box"
                            : "working";
                        string msg = "Cleaning component store — " + elapsed + ", " + state;
                        int? pctField = null;
                        if (pctSnapshot >= 0)
                        {
                            msg += " (" + pctSnapshot + "%)";
                            pctField = pctSnapshot;
                        }

                        progress.Write(phase, msg, pctField, 1, 0, 0, false);
                    }
                })
                {
                    IsBackground = true,
                    Name = "VivreDismLiveness",
                };
                sampler.Start();

                p.WaitForExit();

                // Stop the sampler and let it settle so no stray "Cleaning" line trails the terminal line.
                stop.Set();
                try { sampler.Join(TimeSpan.FromSeconds(2)); }
                catch { /* best-effort join; background thread won't block process exit */ }

                // Always return dism's real exit code — nothing is killed and there is no sentinel, so the
                // caller's 0 → Done / 3010 → PendingReboot / else → Error mapping is the whole truth.
                return p.ExitCode;
            }
        }

        /// <summary>
        /// Sums <see cref="System.Diagnostics.Process.TotalProcessorTime"/> across the running servicing
        /// stack (<c>dism</c> / <c>TiWorker</c> / <c>TrustedInstaller</c>) — the "stack is working" signal,
        /// because dism.exe is a thin client and the heavy reclamation runs in TiWorker / TrustedInstaller.
        /// Any process can exit between enumeration and the CPU read, so each read is guarded individually
        /// (skip the gone process, never throw out of a tick), and every enumerated handle is disposed.
        /// </summary>
        private static TimeSpan SampleServicingStackCpu()
        {
            TimeSpan total = TimeSpan.Zero;
            foreach (string name in ServicingStackProcessNames)
            {
                System.Diagnostics.Process[] procs;
                try
                {
                    procs = System.Diagnostics.Process.GetProcessesByName(name);
                }
                catch
                {
                    // Enumeration itself can fail transiently (e.g. a perf-counter hiccup); treat the whole
                    // name as contributing nothing this tick rather than failing the sample.
                    continue;
                }

                foreach (var proc in procs)
                {
                    try
                    {
                        total += proc.TotalProcessorTime;
                    }
                    catch
                    {
                        // Process exited between enumeration and the read, or access was denied; skip it.
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// The newest write time across the CBS log files (<c>%WINDIR%\Logs\CBS\CBS.log</c> and the rolled
        /// <c>CbsPersist_*.log</c>) — robust to rollover. A later max last-write since the prior tick means
        /// the servicing stack appended to the log, i.e. it did work. Returns <see cref="DateTime.MinValue"/>
        /// when the folder/files can't be read (treated as "no info this tick" by the caller — never throws).
        /// </summary>
        private static DateTime SampleCbsLogMaxWriteUtc()
        {
            try
            {
                string windir = Environment.GetEnvironmentVariable("WINDIR");
                if (string.IsNullOrEmpty(windir))
                {
                    // Fall back to the parent of the system directory (…\System32 → …\Windows).
                    windir = System.IO.Directory.GetParent(Environment.SystemDirectory)?.FullName;
                }

                if (string.IsNullOrEmpty(windir))
                {
                    return DateTime.MinValue;
                }

                string cbsDir = System.IO.Path.Combine(windir, "Logs", "CBS");
                if (!System.IO.Directory.Exists(cbsDir))
                {
                    return DateTime.MinValue;
                }

                DateTime max = DateTime.MinValue;
                foreach (string pattern in new[] { "CBS.log", "CbsPersist_*.log" })
                {
                    foreach (string file in System.IO.Directory.EnumerateFiles(cbsDir, pattern))
                    {
                        try
                        {
                            DateTime w = System.IO.File.GetLastWriteTimeUtc(file);
                            if (w > max)
                            {
                                max = w;
                            }
                        }
                        catch
                        {
                            // The file may be locked/deleted mid-rollover; skip it, no info from this file.
                        }
                    }
                }

                return max;
            }
            catch
            {
                // IO / Unauthorized enumerating the folder — no info this tick, never throw out of a sample.
                return DateTime.MinValue;
            }
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

            // The SECOND face of the transient failure: the search returned WITHOUT throwing but did not
            // cleanly succeed (SucceededWithErrors / Failed / Aborted). Its result is INCOMPLETE, so "no
            // applicable updates" would be a false green. Surface a transient reach failure (HRESULT
            // 0x80240438) the controller retries — read-only, no install, no reboot.
            if (result.ResultCode != OperationResultCode.orcSucceeded)
            {
                progress.Write("Error",
                    "Windows Update search did not complete cleanly (result code " + ((int)result.ResultCode)
                    + ", HRESULT 0x80240438) - the update source was not fully reached.",
                    100, 0, 0, 0, false);
                return false;
            }

            List<IUpdate> applicable = FilterUpdates(result.Updates, config, requireUninstallable: false);
            int total = applicable.Count;
            if (total == 0)
            {
                progress.Write("Done", "No applicable updates", 100, 0, 0, 0, false);
                return false;
            }

            progress.Write("Searching", total + " update" + (total == 1 ? "" : "s") + " matched — starting downloads…", 5, total, 0, 0, false);

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
                : string.Format(CultureInfo.InvariantCulture, "Uninstalled {0} update" + (removed == 1 ? "" : "s"), removed);

            if (rebootPending)
            {
                progress.Write("PendingReboot", summary + " · reboot required", 100, total, removed, failed, true);
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

            // Second face (see RunInstall): a search that returned without throwing but did not cleanly
            // succeed has an INCOMPLETE list — never report it as "up to date"/"no updates". Surface a
            // transient reach failure (0x80240438) the controller retries — read-only, no install, no reboot.
            if (result.ResultCode != OperationResultCode.orcSucceeded)
            {
                progress.Write("Error",
                    "Windows Update search did not complete cleanly (result code " + ((int)result.ResultCode)
                    + ", HRESULT 0x80240438) - the update source was not fully reached.",
                    100, 0, 0, 0, false);
                return false;
            }

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

                    // Emit BOTH WUA sizes as RAW BYTES (no MB rounding here). The controller shows MaxDownloadSize
                    // directly for normal updates (Min when Max is 0) and substitutes the Microsoft Update Catalog
                    // size only when Max is implausibly large (the inflated express-CU aggregate).
                    long minBytes = 0;
                    long maxBytes = 0;
                    try { minBytes = (long)u.MinDownloadSize; } catch { }
                    try { maxBytes = (long)u.MaxDownloadSize; } catch { }

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
                        ["MinSizeBytes"] = minBytes,
                        ["MaxSizeBytes"] = maxBytes,
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
                ? (rows.Count == 0 ? "No installed updates" : rows.Count + " installed update" + (rows.Count == 1 ? "" : "s"))
                : (rows.Count == 0 ? "Up to date" : rows.Count + " update" + (rows.Count == 1 ? "" : "s") + " available");
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
                : string.Format(CultureInfo.InvariantCulture, "{0} {1} update" + (installed == 1 ? "" : "s"), verb, installed);

            if (rebootPending)
            {
                progress.Write("PendingReboot", summary + " · reboot required", 100, total, installed, failed, true);
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
