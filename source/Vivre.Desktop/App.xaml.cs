using System.Windows;
using Wpf.Ui.Appearance;
using Vivre.Core.Columns;
using Vivre.Core.Computers;
using Vivre.Core.Configuration;
using Vivre.Core.Credentials;
using Vivre.Core.Deploy;
using Vivre.Core.Net;
using Vivre.Core.PowerShell;
using Vivre.Core.Rdp;
using Vivre.Core.Remediation;
using Vivre.Core.Remoting;
using Vivre.Core.Sccm;
using Vivre.Core.Scripts;
using Vivre.Core.Software;
using Vivre.Core.Updates;
using Vivre.Core.Vitals;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

/// <summary>
/// Composition root for the app. Builds the shared services once and wires up the
/// shell — there is no DI container; this single place owns object construction so
/// view models receive their dependencies instead of <c>new</c>-ing them.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Let the thread pool hand out worker threads immediately for the burst of concurrent WinRM connects a
        // fleet sweep makes. Each remote runspace open is a synchronous blocking call run on a Task.Run worker
        // (PSRunspaceHost); the sweep permits ~28 at once, but the pool's default min (= core count, e.g. 2) injects
        // new workers only ~1-2/sec, so the opens were trickling in serially across the slowest-connect window and
        // stalling the UI until they cleared. Raising the floor lets all ~28 start at once and run fully overlapped,
        // off the UI thread. This only speeds thread hand-out for already-throttled work — it does NOT widen any of
        // the app's concurrency caps (the sweep / monitor / per-host / install throttles still bound how many run).
        System.Threading.ThreadPool.SetMinThreads(64, 64);

#if DEBUG
        // Catch off-thread writes to a grid live-filtered property (UpdatePhase/RebootRequired → PatchState)
        // loudly, with the offending property name, instead of as an opaque "calling thread cannot access this
        // object" crash when the live CollectionView re-shapes off-thread. Wired only in DEBUG; Vivre.Core
        // stays UI-agnostic (it calls this injected check, never WPF). Runs on the UI thread.
        Vivre.Core.Models.Computer.LiveFilteredWriteIsOnUiThread =
            () => Current?.Dispatcher.CheckAccess() ?? true;
#endif

        // Shared singletons (one PowerShell host, one credential store, …) used by every tab.
        // Wrap the real WinRM host so a host that rejects Kerberos (0x80090322) is recorded once and
        // routed to SMB/DCOM instead of re-paying the ~20s doomed WinRM connect. Transparent to every
        // consumer (all take IPowerShellHost). The shared cache also feeds the Vitals Kerberos finding.
        var transportCache = new HostTransportCache();
        var powerShell = new RoutingPowerShellHost(new PSRunspaceHost(), transportCache);
        var pinger = new HostPinger();
        var hostProbe = new WmiHostProbe();
        var rebootProbe = new HostRebootProbe(powerShell);
        var configMgr = new ConfigMgrClient(powerShell);
        var winRm = new WinRmEnabler();
        var lists = new ComputerListStore();
        var rdpHosts = new RdpHostStore();
        var rdpCreds = new RdpCredentialStore();
        var credentials = new CredentialStore();
        var activity = new ActivityLog();
        // Settings-save failures surface through the activity log (a static hook because the store is
        // new()-constructed in several places — see AppSettingsStore.ActivityLog). The machine-wide shared
        // store (operational settings under C:\ProgramData\Vivre) reports read failures the same way.
        AppSettingsStore.ActivityLog = activity;
        SharedSettingsStore.ActivityLog = activity;
        var scripts = new ScriptLibrary();
        var patch = new PatchService(powerShell, activity);
        var vitals = new VitalsProbe(powerShell, new DcomVitalsProbe());
        var remediation = new RemediationService(powerShell);
        var deployment = new DeploymentService(powerShell);
        var software = new SoftwareProbe(powerShell, new DcomSoftwareReader());
        var customColumns = new CustomColumnProbe(powerShell);
        // One shared catalog-size lookup: its per-KB cache is process-wide, so many machines showing the same
        // KB hit the Microsoft Update Catalog once. Self-contained HTTPS GET; failures fall back silently.
        var catalogSize = new MicrosoftUpdateCatalogService();
        // Session-only, shared across tabs (consistent with the in-memory credential model).
        var patchOptions = new PatchOptions();
        // Reaps orphaned Vivre_Reboot_* services left when the SMB/SCM reboot fallback's best-effort
        // delete lost the race with the reboot; swept per host on list load (once per session).
        var reaper = new OrphanRebootServiceReaper(pinger, activity);

        // Global safety net: never die silently. Unhandled exceptions (e.g. from an async void
        // event handler) are logged to the activity log + rolling file instead of taking the app
        // down with nothing recorded — the failure mode the rewrite was meant to eliminate.
        DispatcherUnhandledException += (_, args) =>
        {
            activity.Error(null, $"Unexpected error: {args.Exception.GetType().Name}: {args.Exception.Message}");
            args.Handled = true; // keep the app alive; the operation that failed is logged
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                // Fatal, usually off the UI thread, during teardown — write straight to the file sink.
                // Routing through the dispatcher (activity.Error) can throw/block as it shuts down,
                // losing this line.
                activity.Fatal(null, $"Fatal error: {ex.GetType().Name}: {ex.Message}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            activity.Error(null, $"Background task error: {args.Exception.GetType().Name}: {args.Exception.Message}");
            args.SetObserved();
        };

        // Persisted preferences (theme). Read + apply the saved theme before the window shows so
        // the user's choice survives restarts; a read failure falls back to the App.xaml default.
        var settingsStore = new AppSettingsStore();
        AppSettings settings;
        try
        {
            settings = settingsStore.Load();
        }
        catch (Exception ex)
        {
            activity.Error(null, $"Couldn't read settings — using defaults for this session (staged-patching flags may be missing until the file is fixed). {ex.Message}");
            settings = new AppSettings();
        }

        ApplyTheme(settings.Theme);

        // Factory for a fresh tab/workspace, capturing the shared services.
        WorkspaceViewModel NewWorkspace() => new(pinger, hostProbe, configMgr, winRm, credentials, lists, activity, scripts, patch, patchOptions, rebootProbe, powerShell, vitals, remediation, deployment, software, customColumns, catalogSize, reaper);

        // Singleton Cross-Domain RDP view model — created once here and kept for the app lifetime.
        // The nav section's DataContext binds to ShellViewModel.RdpViewModel.
        var rdpViewModel = new CrossDomainRdpViewModel(rdpHosts, rdpCreds, activity);

        var shell = new ShellViewModel(NewWorkspace, rdpViewModel, credentials, activity);
        var window = new MainWindow
        {
            DataContext = shell,
            Settings = settingsStore,
            Log = activity,
            SavedTheme = settings.Theme,
            ScriptLibrary = scripts,
        };
        window.Show();
    }

    /// <summary>Applies a saved theme name ("Light" / "Dark" / "System").</summary>
    internal static void ApplyTheme(string theme)
    {
        switch (theme)
        {
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case "System":
                ApplicationThemeManager.ApplySystemTheme();
                break;
            default:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
        }
    }
}
