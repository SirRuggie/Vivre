using System.Windows;
using Wpf.Ui.Appearance;
using Vivre.Core.Columns;
using Vivre.Core.Computers;
using Vivre.Core.Credentials;
using Vivre.Core.Deploy;
using Vivre.Core.Net;
using Vivre.Core.PowerShell;
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

        // Shared singletons (one PowerShell host, one credential store, …) used by every tab.
        var powerShell = new PSRunspaceHost();
        var pinger = new HostPinger();
        var hostProbe = new WmiHostProbe();
        var rebootProbe = new HostRebootProbe(powerShell);
        var configMgr = new ConfigMgrClient(powerShell);
        var winRm = new WinRmEnabler();
        var lists = new ComputerListStore();
        var credentials = new CredentialStore();
        var activity = new ActivityLog();
        var scripts = new ScriptLibrary();
        var patch = new PatchService(powerShell);
        var vitals = new VitalsProbe(powerShell);
        var remediation = new RemediationService(powerShell);
        var deployment = new DeploymentService(powerShell);
        var software = new SoftwareProbe(powerShell);
        var customColumns = new CustomColumnProbe(powerShell);
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
            activity.Warn(null, $"Couldn't read settings — using defaults. {ex.Message}");
            settings = new AppSettings();
        }

        ApplyTheme(settings.Theme);

        // Factory for a fresh tab/workspace, capturing the shared services.
        WorkspaceViewModel NewWorkspace() => new(pinger, hostProbe, configMgr, winRm, credentials, lists, activity, scripts, patch, patchOptions, rebootProbe, powerShell, vitals, remediation, deployment, software, customColumns);

        var shell = new ShellViewModel(NewWorkspace, credentials, activity);
        var window = new MainWindow { DataContext = shell, Settings = settingsStore, Log = activity };
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
