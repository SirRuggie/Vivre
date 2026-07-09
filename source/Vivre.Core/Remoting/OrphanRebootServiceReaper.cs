using System.ComponentModel;
using System.Runtime.InteropServices;
using Vivre.Core.Logging;
using Vivre.Core.Net;

namespace Vivre.Core.Remoting;

/// <summary>
/// Reaps orphaned <c>Vivre_Reboot_*</c> services left by the SMB/SCM reboot fallback's best-effort
/// delete losing the race with the reboot (see <see cref="Updates.DcomRebootTrigger"/>); runs over the
/// SAME channel the service was created on (advapi32 SCM over <c>\\host\IPC$\svcctl</c>, ambient NTLM
/// SSO — WinRM/DCOM are broken on exactly the boxes that accumulate these). Read-enumerate-query-delete
/// ONLY — this file deliberately binds no StartService/ControlService/CreateService, and service
/// handles are opened without SERVICE_START, so starting anything is impossible by construction.
/// </summary>
public sealed class OrphanRebootServiceReaper
{
    // Ping gate timeout (ms) and the per-host SCM sweep bound (seconds).
    private const int PingTimeoutMs = 1500;
    private const int ScmSweepTimeoutSeconds = 10;

    // The sick-box analogue of the reboot-probe / Enable-WinRM caps — NOT the WinRM SplitThrottle;
    // different transport, and a wide burst against degraded boxes makes them worse.
    private readonly SemaphoreSlim _throttle = new(8);

    // Swept once per host per SESSION. This dedup is cardinal-load-bearing: it keeps the reaper
    // temporally disjoint from any later reboot wave on the same host — never add a re-arm hook.
    private readonly HashSet<string> _sweptHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sweptLock = new();

    private readonly IHostPinger _pinger;
    private readonly IActivityLog _activity;

    public OrphanRebootServiceReaper(IHostPinger pinger, IActivityLog activity)
    {
        _pinger = pinger;
        _activity = activity;
    }

    /// <summary>
    /// Sweeps <paramref name="hosts"/> for orphaned <c>Vivre_Reboot_*</c> services and deletes any
    /// confirmed-Stopped ones. Fire-and-forget safe: never throws (the returned Task always runs to
    /// completion), and each host is swept at most once per session.
    /// </summary>
    public async Task ReapAsync(IReadOnlyCollection<string> hosts)
    {
        // Core infrastructure kicked from the UI thread but needing no UI affinity; continuations must
        // not land on the UI thread during cold start — every await below is ConfigureAwait(false).
        try
        {
            var toSweep = new List<string>();
            lock (_sweptLock)
            {
                foreach (string host in hosts)
                {
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        continue;
                    }

                    // Add before sweeping so a re-entrant load never double-sweeps the same host.
                    if (_sweptHosts.Add(host))
                    {
                        toSweep.Add(host);
                    }
                }
            }

            if (toSweep.Count == 0)
            {
                return;
            }

            await Task.WhenAll(toSweep.Select(SweepHostThrottledAsync)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activity.Warn(null, $"Orphan reboot-service sweep failed: {ex.Message}");
        }
    }

    private async Task SweepHostThrottledAsync(string host)
    {
        await _throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            // Ping gate: an orphan on a down box is not a live risk (a later session retries);
            // OpenSCManager has no timeout parameter and blocks ~21s (TCP SYN-retransmit) on a
            // dead/445-filtered box — the native call is uncancellable, so this ping gate is the
            // PRIMARY bound (worst case ~17 abandoned pool threads on an all-filtered list, draining
            // in ~21s — accepted; mirrors the Enable WinRM belt).
            PingResult ping = await _pinger.PingAsync(host, PingTimeoutMs).ConfigureAwait(false);
            if (!ping.IsOnline)
            {
                return;
            }

            Task work = Task.Run(() => SweepHost(host));
            _ = work.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default); // observe BEFORE the belt abandons it
            try
            {
                await work.WaitAsync(TimeSpan.FromSeconds(ScmSweepTimeoutSeconds)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return; // offline-ish; silent — retried next session
            }
        }
        catch (Exception ex)
        {
            // One degraded box never aborts the sweep on the rest of the selection.
            _activity.Warn(host, $"Orphan reboot-service sweep failed on {host} — {ex.Message}");
        }
        finally
        {
            _throttle.Release();
        }
    }

    // Synchronous, pool thread. Connects to the target SCM, enumerates its Win32 services, and reaps
    // any confirmed-Stopped Vivre_Reboot_* orphan. Silent for clean, unreachable, and SCM-unreachable
    // hosts — no summary line.
    private void SweepHost(string host)
    {
        // advapi32 wants the UNC \\machine form for a remote SCM (same as RemoteServiceController).
        string machine = host.StartsWith(@"\\", StringComparison.Ordinal) ? host : $@"\\{host}";

        IntPtr scm = OpenSCManager(machine, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            // Skipped silently and retried next session; if the operator's rights changed since the
            // orphan was created, it persists unreaped and unreported — accepted: the orphan is inert
            // and demand-start, and per-box logging would be noise across a mixed fleet.
            return;
        }

        try
        {
            foreach (string name in CollectReapableNames(scm))
            {
                ReapService(scm, host, name);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    // Enumerate every Win32 service and return the exact Vivre reboot-service KEY names. Two-call
    // pattern: a zero-length probe to learn the buffer size, then the standard resume-handle loop.
    private static List<string> CollectReapableNames(IntPtr scm)
    {
        var names = new List<string>();

        uint bytesNeeded;
        uint servicesReturned;
        uint resumeHandle = 0;

        // First call with a zero-length buffer — expect false with ERROR_MORE_DATA and bytesNeeded set.
        if (EnumServicesStatusW(scm, SERVICE_WIN32, SERVICE_STATE_ALL, IntPtr.Zero, 0,
                out bytesNeeded, out servicesReturned, ref resumeHandle))
        {
            return names; // no services returned (nothing to reap)
        }

        if (Marshal.GetLastWin32Error() != ERROR_MORE_DATA)
        {
            return names; // any other error -> skip silently (same honest-skip rationale)
        }

        uint size = bytesNeeded;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            int stride = Marshal.SizeOf<ENUM_SERVICE_STATUSW>();
            while (true)
            {
                bool ok = EnumServicesStatusW(scm, SERVICE_WIN32, SERVICE_STATE_ALL, buffer, size,
                    out bytesNeeded, out servicesReturned, ref resumeHandle);
                int err = ok ? 0 : Marshal.GetLastWin32Error();

                for (int i = 0; i < servicesReturned; i++)
                {
                    IntPtr entryPtr = buffer + i * stride;
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUSW>(entryPtr);
                    // Classify on lpServiceName (the SCM KEY name), NEVER lpDisplayName — the display
                    // name is "Vivre Reboot <hex>" with spaces and would no-op the classifier. Copy the
                    // string now: the pointers point INTO the buffer freed below.
                    string? name = Marshal.PtrToStringUni(entry.lpServiceName);
                    if (RebootServiceReapPolicy.IsReapableName(name))
                    {
                        names.Add(name!);
                    }
                }

                if (ok)
                {
                    break; // all entries returned
                }

                if (err == ERROR_MORE_DATA)
                {
                    continue; // the resume handle advanced; more chunks remain
                }

                break; // any other false -> stop
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return names;
    }

    // Open a single candidate without SERVICE_START, re-confirm it is Stopped, and delete it.
    private void ReapService(IntPtr scm, string host, string name)
    {
        IntPtr svc = OpenService(scm, name, SERVICE_QUERY_STATUS | DELETE); // deliberately no SERVICE_START
        if (svc == IntPtr.Zero)
        {
            int openErr = Marshal.GetLastWin32Error();
            if (openErr == ERROR_SERVICE_DOES_NOT_EXIST)
            {
                return; // deleted between enum and open — benign
            }

            _activity.Warn(host, $"Couldn't remove orphaned reboot service '{name}' — {new Win32Exception(openErr).Message}");
            return;
        }

        try
        {
            // Re-query fresh state on the handle; a failed query maps to Unknown (which is skipped).
            var status = default(SERVICE_STATUS);
            RemoteServiceState state = QueryServiceStatus(svc, ref status)
                ? ToState(status.dwCurrentState)
                : RemoteServiceState.Unknown;

            if (RebootServiceReapPolicy.ShouldReap(name, state))
            {
                if (DeleteService(svc))
                {
                    _activity.Info(host, $"Removed an orphaned Vivre reboot service ({name}) left by an earlier reboot — if started, it would have rebooted this box.");
                }
                else
                {
                    int delErr = Marshal.GetLastWin32Error();
                    if (delErr is ERROR_SERVICE_DOES_NOT_EXIST or ERROR_SERVICE_MARKED_FOR_DELETE)
                    {
                        return; // already gone / already marked — benign (matches RemoteServiceController.Delete)
                    }

                    _activity.Warn(host, $"Couldn't remove orphaned reboot service '{name}' — {new Win32Exception(delErr).Message}");
                }
            }
            else
            {
                _activity.Warn(host, $"Found a Vivre reboot service ({name}) not in the Stopped state — left alone (a reboot may be in flight).");
            }
        }
        finally
        {
            CloseServiceHandle(svc);
        }
    }

    private static RemoteServiceState ToState(uint dwCurrentState) => dwCurrentState switch
    {
        1 => RemoteServiceState.Stopped,
        2 => RemoteServiceState.StartPending,
        3 => RemoteServiceState.StopPending,
        4 => RemoteServiceState.Running,
        _ => RemoteServiceState.Unknown,
    };

    // --- access rights / enumerate filters ---
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint DELETE = 0x10000;
    private const uint SERVICE_WIN32 = 0x00000030;
    private const uint SERVICE_STATE_ALL = 0x00000003;

    // --- Win32 error codes we treat as benign / actionable ---
    private const int ERROR_MORE_DATA = 234;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

    // --- advapi32 (read-enumerate-query-delete ONLY; no start/control/create binding) -------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUSW
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS Status;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusW(
        IntPtr hSCManager,
        uint dwServiceType,
        uint dwServiceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);
}
