using System.Windows;
using Vivre.Core.Computers;
using Vivre.Core.Credentials;
using Vivre.Core.Net;
using Vivre.Core.PowerShell;
using Vivre.Core.Remoting;
using Vivre.Core.Sccm;
using Vivre.Core.Scripts;
using Vivre.Core.Updates;
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

        // Shared singletons (one PowerShell host, one credential store, …) used by every tab.
        var powerShell = new PSRunspaceHost();
        var pinger = new HostPinger();
        var hostProbe = new WmiHostProbe();
        var configMgr = new ConfigMgrClient(powerShell);
        var winRm = new WinRmEnabler();
        var lists = new ComputerListStore();
        var credentials = new CredentialStore();
        var activity = new ActivityLog();
        var scripts = new ScriptLibrary();
        var patch = new PatchService(powerShell);
        // Session-only, shared across tabs (consistent with the in-memory credential model).
        var patchOptions = new PatchOptions();

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
                activity.Error(null, $"Fatal error: {ex.GetType().Name}: {ex.Message}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            activity.Error(null, $"Background task error: {args.Exception.GetType().Name}: {args.Exception.Message}");
            args.SetObserved();
        };

        // Factory for a fresh tab/workspace, capturing the shared services.
        WorkspaceViewModel NewWorkspace() => new(pinger, hostProbe, configMgr, winRm, credentials, lists, activity, scripts, patch, patchOptions);

        var shell = new ShellViewModel(NewWorkspace, credentials, activity);
        var window = new MainWindow { DataContext = shell };
        window.Show();
    }
}
