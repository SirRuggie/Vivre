using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vivre.Core.Remoting;

/// <summary>The Service Control Manager state of a remote service (a subset of the Win32 SERVICE_STATE values).</summary>
public enum RemoteServiceState
{
    /// <summary>Could not be read (the service is gone, or the query failed).</summary>
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
}

/// <summary>
/// Creates, starts, stops, and deletes a service on a remote host through the Service Control Manager
/// over the SMB <c>\\host\IPC$\svcctl</c> named pipe — the BatchPatch/PsExec mechanism Vivre uses to
/// run the update agent as LocalSystem on boxes that reject WinRM/Kerberos (0x80090322). It rides the
/// operator's <b>current Windows login</b> via NTLM SSO (no Kerberos, no credential prompt): the SCM
/// RPC interface authenticates with the calling process token, exactly as <c>sc \\host …</c> does.
///
/// <para>This is a thin, single-service-per-instance wrapper over advapi32. <see cref="Dispose"/>
/// closes both the SCM and service handles; it does NOT delete the service (teardown calls
/// <see cref="Delete"/> explicitly after the agent has stopped, so a half-built run can still be reaped
/// deliberately).</para>
/// </summary>
public sealed class RemoteServiceController : IDisposable
{
    // --- access rights / service config constants ---
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const uint SERVICE_DEMAND_START = 0x00000003;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;

    // --- Win32 error codes we treat as benign / actionable ---
    private const int ERROR_SERVICE_EXISTS = 1073;
    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    private const int ERROR_DUPLICATE_SERVICE_NAME = 1078;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    private readonly string _name;
    private IntPtr _scm;
    private IntPtr _service;

    private RemoteServiceController(string name, IntPtr scm, IntPtr service)
    {
        _name = name;
        _scm = scm;
        _service = service;
    }

    /// <summary>
    /// Connects to <paramref name="host"/>'s SCM and creates a one-shot LocalSystem, demand-start,
    /// own-process service named <paramref name="serviceName"/> whose image command line is
    /// <paramref name="binPath"/> (full path + arguments, the EXE quoted if it contains spaces).
    /// Throws <see cref="Win32Exception"/> on failure (connect denied, name already exists, etc.).
    /// </summary>
    public static RemoteServiceController Create(string host, string serviceName, string displayName, string binPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(binPath);

        // advapi32 wants the UNC \\machine form for a remote SCM.
        string machine = host.StartsWith(@"\\", StringComparison.Ordinal) ? host : $@"\\{host}";

        IntPtr scm = OpenSCManager(machine, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Couldn't open the Service Control Manager on {host}.");
        }

        try
        {
            IntPtr service = CreateService(
                scm,
                serviceName,
                displayName,
                SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS,
                SERVICE_DEMAND_START,
                SERVICE_ERROR_NORMAL,
                binPath,
                lpLoadOrderGroup: null,
                lpdwTagId: IntPtr.Zero,
                lpDependencies: null,
                lpServiceStartName: null, // null => LocalSystem (S-1-5-18) — the account WUA install needs
                lpPassword: null);

            if (service == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                string detail = err switch
                {
                    ERROR_SERVICE_EXISTS => $"a service named '{serviceName}' already exists on {host}.",
                    ERROR_DUPLICATE_SERVICE_NAME => $"a service with the same display name already exists on {host}.",
                    ERROR_SERVICE_MARKED_FOR_DELETE => $"a prior '{serviceName}' on {host} is still being deleted; retry shortly.",
                    _ => $"couldn't create the update service on {host}.",
                };
                throw new Win32Exception(err, detail);
            }

            return new RemoteServiceController(serviceName, scm, service);
        }
        catch
        {
            CloseServiceHandle(scm);
            throw;
        }
    }

    /// <summary>Starts the service. The service-aware agent reports RUNNING immediately, so this returns
    /// without the SCM start-timeout (error 1053) a plain EXE would trigger.</summary>
    public void Start()
    {
        if (!StartService(_service, 0, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Couldn't start the update service '{_name}'.");
        }
    }

    /// <summary>Reads the current SCM state. Returns <see cref="RemoteServiceState.Unknown"/> if the
    /// query fails (e.g. the service was already deleted).</summary>
    public RemoteServiceState Query()
    {
        var status = default(SERVICE_STATUS);
        return QueryServiceStatus(_service, ref status)
            ? ToState(status.dwCurrentState)
            : RemoteServiceState.Unknown;
    }

    /// <summary>
    /// Sends a STOP control. Best-effort: a service that already stopped (the agent self-stops on
    /// completion) reports ERROR_SERVICE_NOT_ACTIVE, which is success for our purposes. Returns false
    /// only on an unexpected control failure (logged by the caller, never thrown — teardown must proceed).
    /// </summary>
    public bool TryStop()
    {
        var status = default(SERVICE_STATUS);
        if (ControlService(_service, SERVICE_CONTROL_STOP, ref status))
        {
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        return err is ERROR_SERVICE_NOT_ACTIVE or ERROR_SERVICE_DOES_NOT_EXIST;
    }

    /// <summary>Marks the service for deletion. Benign if it's already gone. Throws on an unexpected
    /// failure so the caller can surface a genuine leak (a leftover service is the one thing worth knowing).</summary>
    public void Delete()
    {
        if (DeleteService(_service))
        {
            return;
        }

        int err = Marshal.GetLastWin32Error();
        if (err is ERROR_SERVICE_DOES_NOT_EXIST or ERROR_SERVICE_MARKED_FOR_DELETE)
        {
            return;
        }

        throw new Win32Exception(err, $"Couldn't delete the update service '{_name}'.");
    }

    public void Dispose()
    {
        if (_service != IntPtr.Zero)
        {
            CloseServiceHandle(_service);
            _service = IntPtr.Zero;
        }

        if (_scm != IntPtr.Zero)
        {
            CloseServiceHandle(_scm);
            _scm = IntPtr.Zero;
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

    // --- advapi32 -----------------------------------------------------------

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

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);
}
