using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vivre.Core.Configuration;
using Vivre.Core.Credentials;
using Vivre.Core.Updates;
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
    // Machine-wide operational settings (WUG server, package folders, this month's CU, install concurrency,
    // staged-machine list) shared by every operator on the box. Stateless and self-contained, so field-init
    // it — it doesn't need injecting like the per-user store.
    private readonly SharedSettingsStore _sharedStore = new();
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

        // Seed the behaviour fields: personal preferences from the per-user (Roaming) store, operational
        // settings from the machine-wide shared store.
        AppSettings s = settingsStore.Load();
        SharedSettings shared = _sharedStore.Load();
        AutoToggle.IsChecked = s.AutoCheckOnLoad;
        WugServerBox.Text = shared.WugServer;
        PackagesFolderBox.Text = shared.PackagesFolder;
        LcuKbBox.Text = shared.MonthlyCu?.Kb ?? string.Empty;
        LcuUbrBox.Text = shared.MonthlyCu?.TargetUbr.ToString() ?? string.Empty;
        LcuMonthTagBox.Text = shared.MonthlyCu?.MonthTag ?? string.Empty;
        LcuPackagesFolderBox.Text = shared.LcuPackagesFolder;
        MaxInstallsBox.Text = shared.MaxSimultaneousInstalls.ToString();
        WugStateConcurrencyBox.Text = shared.WugStateConcurrency.ToString();

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
        PersistShared(s => s.WugServer = value);
    }

    private void OnPackagesFolderChanged(object sender, RoutedEventArgs e)
    {
        string value = PackagesFolderBox.Text.Trim();
        PersistShared(s => s.PackagesFolder = value);
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
        PersistShared(s => s.PackagesFolder = dialog.FolderName);
    }

    // ── Server 2016 cumulative update ──────────────────────────────────────

    private void OnLcuKbChanged(object sender, RoutedEventArgs e)
    {
        string value = LcuKbBox.Text.Trim();
        PersistShared(s => { s.MonthlyCu ??= new MonthlyCu(); s.MonthlyCu.Kb = value; });
    }

    private void OnLcuUbrChanged(object sender, RoutedEventArgs e)
    {
        string raw = LcuUbrBox.Text.Trim();
        if (!int.TryParse(raw, out int ubr))
        {
            // Non-numeric input: snap the field back to the saved value rather than persisting junk.
            LcuUbrBox.Text = _sharedStore.Load().MonthlyCu?.TargetUbr.ToString() ?? string.Empty;
            return;
        }

        PersistShared(s => { s.MonthlyCu ??= new MonthlyCu(); s.MonthlyCu.TargetUbr = ubr; });
    }

    private void OnLcuMonthTagChanged(object sender, RoutedEventArgs e)
    {
        string value = LcuMonthTagBox.Text.Trim();
        PersistShared(s => { s.MonthlyCu ??= new MonthlyCu(); s.MonthlyCu.MonthTag = value; });
    }

    private void OnMaxInstallsChanged(object sender, RoutedEventArgs e)
    {
        string raw = MaxInstallsBox.Text.Trim();
        if (!int.TryParse(raw, out int parsed) || parsed < 1 || parsed > 200)
        {
            // Non-numeric or out-of-range: snap the field back to the saved value rather than persisting junk.
            MaxInstallsBox.Text = _sharedStore.Load().MaxSimultaneousInstalls.ToString();
            return;
        }

        // parsed is already in [1, 200] (guarded above), so no clamp is needed — just normalize the field
        // text (e.g. "050" → "50") so it never displays junk.
        if (parsed.ToString() != raw)
        {
            MaxInstallsBox.Text = parsed.ToString();
        }

        PersistShared(s => s.MaxSimultaneousInstalls = parsed);
    }

    private void OnWugStateConcurrencyChanged(object sender, RoutedEventArgs e)
    {
        string raw = WugStateConcurrencyBox.Text.Trim();
        if (!int.TryParse(raw, out int parsed) || parsed < 1 || parsed > Vivre.Core.Wug.WugMaintenance.StateReadMaxConcurrency)
        {
            // Non-numeric or out-of-range: snap the field back to the saved value rather than persisting junk.
            WugStateConcurrencyBox.Text = _sharedStore.Load().WugStateConcurrency.ToString();
            return;
        }

        // parsed is already in [1, StateReadMaxConcurrency] (guarded above), so no clamp is needed — just
        // normalize the field text (e.g. "02" → "2") so it never displays junk.
        if (parsed.ToString() != raw)
        {
            WugStateConcurrencyBox.Text = parsed.ToString();
        }

        PersistShared(s => s.WugStateConcurrency = parsed);
    }

    private void OnLcuPackagesFolderChanged(object sender, RoutedEventArgs e)
    {
        string value = LcuPackagesFolderBox.Text.Trim();
        PersistShared(s => s.LcuPackagesFolder = value);
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
        PersistShared(s => s.LcuPackagesFolder = dialog.FolderName);
    }

    /// <summary>
    /// "Read from package": read the KB / target UBR / architecture off the single .msu in the CU package
    /// folder and, only after the operator confirms in a side-by-side dialog, persist them into the SAME
    /// MonthlyCu fields the typed flow uses. An accelerator, never automatic — a refusal (wrong product,
    /// renamed file, SSU, two files) shows the reason and changes nothing; a decline changes nothing.
    /// </summary>
    private async void OnReadLcuPackage(object sender, RoutedEventArgs e)
    {
        if (_settingsStore is null)
        {
            return;
        }

        ReadLcuPackageButton.IsEnabled = false;
        try
        {
            // Read from the current box text (trimmed); fall back to the saved folder if the box is empty.
            string folder = LcuPackagesFolderBox.Text.Trim();
            if (folder.Length == 0)
            {
                folder = _sharedStore.Load().LcuPackagesFolder;
            }

            // Task.Run keeps the reader's synchronous prologue (Directory.Exists/GetFiles against a possibly
            // dead UNC path — a many-second SMB timeout) off the dispatcher. The continuation resumes on the
            // UI context, so the dialog/persist/box-mirroring below stay UI-thread-safe.
            var reader = new MsuPackageReader();
            MsuReadResult result = await Task.Run(() => reader.ReadAsync(folder, CancellationToken.None));

            if (result.Identity is MsuIdentityResult.Refused refused)
            {
                var box = new MessageBox
                {
                    Title = "Couldn't read the package",
                    Content = refused.Reason,
                    CloseButtonText = "OK",
                };
                await box.ShowDialogAsync();
                return;
            }

            var accepted = (MsuIdentityResult.Accepted)result.Identity;

            SharedSettings s = _sharedStore.Load();
            string currentKb = s.MonthlyCu?.Kb?.Trim() ?? string.Empty;
            string currentUbr = s.MonthlyCu?.TargetUbr.ToString() ?? string.Empty;
            string currentArch = s.MonthlyCu?.Arch ?? "x64";
            string currentMonthTag = s.MonthlyCu?.MonthTag?.Trim() ?? string.Empty;
            string readUbr = accepted.TargetUbr.ToString();

            // A convenience guess from the file's own timestamp (a download date, NOT a release date) — the
            // operator confirms/edits it in the dialog. Localize before formatting; the helper is pure.
            string suggestedMonthTag = MonthTagSuggestion.SuggestFrom(result.FileModifiedUtc?.ToLocalTime());

            bool matches =
                string.Equals(Lcu2016CuMatcher.NormalizeKb(currentKb), Lcu2016CuMatcher.NormalizeKb(accepted.Kb),
                              StringComparison.OrdinalIgnoreCase)
                && currentUbr == readUbr
                && string.Equals(currentArch, accepted.Arch, StringComparison.OrdinalIgnoreCase);

            var comparison = new LcuPackageReadComparison(
                CurrentKb: currentKb.Length == 0 ? "(not set)" : currentKb,
                CurrentUbr: currentUbr.Length == 0 ? "(not set)" : currentUbr,
                CurrentArch: currentArch,
                ReadKb: accepted.Kb,
                ReadUbr: readUbr,
                ReadArch: accepted.Arch,
                IdentityDescription: $"{accepted.Description} ({accepted.IdentityName} {accepted.Version})",
                FileName: result.FileName ?? string.Empty,
                FileDate: result.FileModifiedUtc?.ToLocalTime().ToString("d MMM yyyy HH:mm") ?? "(unknown)",
                CurrentMonthTag: currentMonthTag.Length == 0 ? "(not set)" : currentMonthTag,
                SuggestedMonthTag: suggestedMonthTag,
                Matches: matches);

            var dialog = new LcuPackageReadDialog(comparison) { Owner = _ownerWindow };
            if (dialog.ShowDialog() != true)
            {
                return; // "Keep my settings" / Esc / close — change nothing.
            }

            // Confirmed — persist into the same shared fields the typed flow writes, then mirror the visible
            // boxes (all are LostFocus-only wired, so setting .Text here is safe and won't re-trigger a handler).
            // The month tag saves atomically in the SAME mutate as KB/UBR/arch. PersistShared surfaces its own
            // failure (log + message box); only claim success + mirror when it saved.
            string confirmedMonthTag = dialog.ConfirmedMonthTag;
            if (!PersistShared(sx =>
            {
                sx.MonthlyCu ??= new MonthlyCu();
                sx.MonthlyCu.Kb = accepted.Kb;
                sx.MonthlyCu.TargetUbr = accepted.TargetUbr;
                sx.MonthlyCu.Arch = accepted.Arch;
                sx.MonthlyCu.MonthTag = confirmedMonthTag;
            }))
            {
                return;
            }

            LcuKbBox.Text = accepted.Kb;
            LcuUbrBox.Text = readUbr;
            LcuMonthTagBox.Text = confirmedMonthTag;

            string tagSuffix = confirmedMonthTag.Length == 0 ? string.Empty : $" ({confirmedMonthTag})";
            _log?.Info(null, $"Read {accepted.Kb} / build {accepted.TargetUbr} ({accepted.Arch}) from {result.FileName} — settings updated{tagSuffix}.");
        }
        catch (Exception ex)
        {
            // This is an async void handler — an escaping exception would crash the app from a Settings
            // button (e.g. a locked settings.json making Load() throw). Surface it instead; nothing persisted.
            _log?.Warn(null, $"Read from package failed: {ex.Message}");
            var box = new MessageBox
            {
                Title = "Couldn't read the package",
                Content = ex.Message,
                CloseButtonText = "OK",
            };
            await box.ShowDialogAsync();
        }
        finally
        {
            ReadLcuPackageButton.IsEnabled = true;
        }
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
        foreach (string host in _sharedStore.Load().StagedHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
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
        if (PersistShared(s => s.StagedHosts.Remove(host)))
        {
            UpdateStagedHostsEmptyState();

            // Keep any loaded rows in step so a removed flag doesn't linger and mis-route a later install.
            (_ownerWindow as MainWindow)?.ResyncStagedPatchingFlags();
        }
        else
        {
            // Save failed (PersistShared already logged + showed it). The removal didn't persist, so re-seed the
            // list from the store — the UI must match what's actually saved, not the optimistic removal.
            ReseedStagedHosts();
        }
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
        if (PersistShared(s => s.StagedHosts.Clear()))
        {
            UpdateStagedHostsEmptyState();
            (_ownerWindow as MainWindow)?.ResyncStagedPatchingFlags();
        }
        else
        {
            // Save failed (PersistShared already logged + showed it). Re-seed from the store so the list reflects
            // what's actually persisted rather than the optimistic clear.
            ReseedStagedHosts();
        }
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

    /// <summary>Load-modify-save for the MACHINE-WIDE shared operational settings (C:\ProgramData\Vivre\settings.json).
    /// The shared store's Save is SYNCHRONOUS and throws on failure — unlike the per-user store's fire-and-forget
    /// write — so a failed save is made unmissable here: it logs an Error AND shows a message box (settings edits
    /// are infrequent; a silent "looks saved but wasn't" would mis-route boxes). Returns true on success, false on
    /// failure, so a caller that changed UI state first can roll it back to match what actually persisted.</summary>
    private bool PersistShared(Action<SharedSettings> mutate)
    {
        try
        {
            SharedSettings s = _sharedStore.Load();
            mutate(s);
            _sharedStore.Save(s);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error(null, $"Couldn't save shared settings to C:\\ProgramData\\Vivre\\settings.json. {ex.Message}");
            _ = new MessageBox
            {
                Title = "Couldn't save shared settings",
                Content = "The change couldn't be saved to the machine-wide settings file "
                          + $"(C:\\ProgramData\\Vivre\\settings.json), so it won't stick:\n\n{ex.Message}",
                CloseButtonText = "OK",
            }.ShowDialogAsync();
            return false;
        }
    }
}
