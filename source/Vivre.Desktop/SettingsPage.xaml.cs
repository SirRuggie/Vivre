using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vivre.Core.Credentials;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Vivre.Desktop;

/// <summary>
/// The Settings section shown inside the NavigationView content area. Consolidates:
/// theme toggle, session credential, auto-check, grid columns shortcut, WhatsUp Gold server,
/// package library folder, and links to Help / About. Lives in the keep-alive content grid —
/// never rebuilt.
/// </summary>
public partial class SettingsPage : UserControl
{
    private AppSettingsStore? _settingsStore;
    private Core.Logging.IActivityLog? _log;
    private SettingsViewModel? _credVm;
    private Window? _ownerWindow;

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>Called once by MainWindow after the page is placed in the visual tree.</summary>
    public void Initialize(AppSettingsStore settingsStore, Core.Logging.IActivityLog log, CredentialStore credentials, Window owner)
    {
        _settingsStore = settingsStore;
        _log = log;
        _ownerWindow = owner;

        _credVm = new SettingsViewModel(credentials);
        CredentialCard.DataContext = _credVm;

        // Seed the behaviour fields from persisted settings.
        AppSettings s = settingsStore.Load();
        AutoToggle.IsChecked = s.AutoCheckOnLoad;
        WugServerBox.Text = s.WugServer;
        PackagesFolderBox.Text = s.PackagesFolder;
        LcuKbBox.Text = s.MonthlyCu?.Kb ?? string.Empty;
        LcuUbrBox.Text = s.MonthlyCu?.TargetUbr.ToString() ?? string.Empty;
        LcuPackagesFolderBox.Text = s.LcuPackagesFolder;
        MaxInstallsBox.Text = s.MaxSimultaneousInstalls.ToString();

        // Inline version in the Help & about expander.
        VersionText.Text = $"Vivre {AboutWindow.RunningVersion()}";

        // Tick the right theme radio.
        UpdateThemeChecks(s.Theme);
    }

    // ── Theme ──────────────────────────────────────────────────────────────

    // Guard flag: prevents OnThemeComboChanged from re-applying/re-saving on programmatic index sets.
    private bool _themeComboLoaded;

    private void OnThemeComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_themeComboLoaded) return;

        string theme = ThemeCombo.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };

        App.ApplyTheme(theme);
        PersistSettings(s => s.Theme = theme);
    }

    public void UpdateThemeChecks(string theme)
    {
        // May be called before InitializeComponent when Initialize() is triggered early.
        if (ThemeCombo is null) return;

        // Suppress the SelectionChanged handler while setting the index programmatically.
        _themeComboLoaded = false;
        ThemeCombo.SelectedIndex = theme switch
        {
            "Light"  => 1,
            "Dark"   => 2,
            _        => 0,  // "System" or any unrecognised value
        };
        _themeComboLoaded = true;
    }

    // ── Credentials ────────────────────────────────────────────────────────

    private async void OnApplyCredentials(object sender, RoutedEventArgs e)
    {
        if (_credVm is null) return;

        if (_credVm.UseExplicitCredentials && string.IsNullOrWhiteSpace(_credVm.UserName))
        {
            var warn = new MessageBox
            {
                Title = "Username required",
                Content = "Enter a username for the explicit credential, or choose \"Use my Windows login\".",
                CloseButtonText = "OK",
            };
            await warn.ShowDialogAsync();
            UserNameBox.Focus();
            return;
        }

        _credVm.Apply(PasswordBox.SecurePassword);
    }

    // ── Behaviour ──────────────────────────────────────────────────────────

    private void OnAutoCheckChanged(object sender, RoutedEventArgs e)
    {
        bool value = AutoToggle.IsChecked == true;
        PersistSettings(s => s.AutoCheckOnLoad = value);
    }

    private void OnWugServerChanged(object sender, RoutedEventArgs e)
    {
        string value = WugServerBox.Text.Trim();
        PersistSettings(s => s.WugServer = value);
    }

    private void OnPackagesFolderChanged(object sender, RoutedEventArgs e)
    {
        string value = PackagesFolderBox.Text.Trim();
        PersistSettings(s => s.PackagesFolder = value);
    }

    private void OnBrowsePackagesFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pick your package library folder" };
        string? current = PackagesFolderBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(_ownerWindow) != true)
        {
            return;
        }

        PackagesFolderBox.Text = dialog.FolderName;
        PersistSettings(s => s.PackagesFolder = dialog.FolderName);
    }

    // ── Server 2016 cumulative update ──────────────────────────────────────

    private void OnLcuKbChanged(object sender, RoutedEventArgs e)
    {
        string value = LcuKbBox.Text.Trim();
        PersistSettings(s => { s.MonthlyCu ??= new MonthlyCu(); s.MonthlyCu.Kb = value; });
    }

    private void OnLcuUbrChanged(object sender, RoutedEventArgs e)
    {
        string raw = LcuUbrBox.Text.Trim();
        if (!int.TryParse(raw, out int ubr))
        {
            // Non-numeric input: snap the field back to the saved value rather than persisting junk.
            LcuUbrBox.Text = _settingsStore?.Load().MonthlyCu?.TargetUbr.ToString() ?? string.Empty;
            return;
        }

        PersistSettings(s => { s.MonthlyCu ??= new MonthlyCu(); s.MonthlyCu.TargetUbr = ubr; });
    }

    private void OnMaxInstallsChanged(object sender, RoutedEventArgs e)
    {
        string raw = MaxInstallsBox.Text.Trim();
        if (!int.TryParse(raw, out int parsed) || parsed < 1 || parsed > 200)
        {
            // Non-numeric or out-of-range: snap the field back to the saved value rather than persisting junk.
            MaxInstallsBox.Text = _settingsStore?.Load().MaxSimultaneousInstalls.ToString() ?? "50";
            return;
        }

        // parsed is already in [1, 200] (guarded above), so no clamp is needed — just normalize the field
        // text (e.g. "050" → "50") so it never displays junk.
        if (parsed.ToString() != raw)
        {
            MaxInstallsBox.Text = parsed.ToString();
        }

        PersistSettings(s => s.MaxSimultaneousInstalls = parsed);
    }

    private void OnLcuPackagesFolderChanged(object sender, RoutedEventArgs e)
    {
        string value = LcuPackagesFolderBox.Text.Trim();
        PersistSettings(s => s.LcuPackagesFolder = value);
    }

    private void OnBrowseLcuPackagesFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pick the CU package folder (.msu drop location)" };
        string? current = LcuPackagesFolderBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(_ownerWindow) != true)
        {
            return;
        }

        LcuPackagesFolderBox.Text = dialog.FolderName;
        PersistSettings(s => s.LcuPackagesFolder = dialog.FolderName);
    }

    // ── Staged patching machines ───────────────────────────────────────────

    // Bound to the staged-hosts ListBox. Re-seeded from the persisted set each time the card is expanded so it
    // reflects flags added/removed elsewhere (e.g. via the grid right-click) since the page was first shown.
    private readonly ObservableCollection<string> _stagedHosts = [];

    private void OnStagedHostsExpanded(object sender, RoutedEventArgs e) => ReseedStagedHosts();

    private void ReseedStagedHosts()
    {
        if (_settingsStore is null)
        {
            return;
        }

        _stagedHosts.Clear();
        foreach (string host in _settingsStore.Load().StagedHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            _stagedHosts.Add(host);
        }

        StagedHostsList.ItemsSource = _stagedHosts;
        UpdateStagedHostsEmptyState();
    }

    private void UpdateStagedHostsEmptyState() =>
        StagedHostsEmpty.Visibility = _stagedHosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnRemoveStagedHost(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string host })
        {
            return;
        }

        _stagedHosts.Remove(host);
        PersistSettings(s => s.StagedHosts.Remove(host));
        UpdateStagedHostsEmptyState();

        // Keep any loaded rows in step so a removed flag doesn't linger and mis-route a later install.
        (_ownerWindow as MainWindow)?.ResyncStagedPatchingFlags();
    }

    private async void OnClearStagedHosts(object sender, RoutedEventArgs e)
    {
        if (_stagedHosts.Count == 0)
        {
            return;
        }

        var confirm = new MessageBox
        {
            Title = "Clear staged patching list",
            Content = $"Remove all {_stagedHosts.Count} machine(s) from the staged-patching list? "
                      + "They'll patch via normal Windows Update.",
            PrimaryButtonText = "Clear all",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
        {
            return;
        }

        _stagedHosts.Clear();
        PersistSettings(s => s.StagedHosts.Clear());
        UpdateStagedHostsEmptyState();
        (_ownerWindow as MainWindow)?.ResyncStagedPatchingFlags();
    }

    // ── Tools ──────────────────────────────────────────────────────────────

    private void OnOpenColumns(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is MainWindow main)
        {
            main.OpenColumnsWindow();
        }
    }

    private void OnOpenHelp(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is MainWindow main)
        {
            main.ShowHelpPublic();
        }
    }

    private void OnOpenAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = _ownerWindow }.ShowDialog();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void PersistSettings(Action<AppSettings> mutate)
    {
        if (_settingsStore is null) return;
        try
        {
            AppSettings s = _settingsStore.Load();
            mutate(s);
            _settingsStore.Save(s);
        }
        catch (Exception ex)
        {
            _log?.Warn(null, $"Couldn't save settings. {ex.Message}");
        }
    }
}
