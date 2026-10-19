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
    // Machine-wide operational settings (WUG server, package folders, this month's CU, staged-machine list)
    // shared by every operator on the box. Stateless and self-contained, so field-init it — it doesn't need
    // injecting like the per-user store. (The install cap and WUG state-check concurrency are PERSONAL — they
    // live in the per-user AppSettings store, seeded/persisted via _settingsStore below.)
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
        LoadCuDraft(shared.MonthlyCu ?? new MonthlyCu());
        LcuPackagesFolderBox.Text = shared.LcuPackagesFolder;
        // The install cap and WUG state-check concurrency are PERSONAL — seed them from the per-user snapshot.
        MaxInstallsBox.Text = s.MaxSimultaneousInstalls.ToString();
        WugStateConcurrencyBox.Text = s.WugStateConcurrency.ToString();
        ServicingWaitBox.Text = s.ServicingWaitMinutes.ToString();

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

    private MonthlyCu _cuBaseline = new();
    private bool _loadingCu;

    private void LoadCuDraft(MonthlyCu cu)
    {
        _cuBaseline = new MonthlyCu { Kb = cu.Kb, TargetUbr = cu.TargetUbr, MonthTag = cu.MonthTag, Arch = cu.Arch };
        _loadingCu = true;
        try
        {
            LcuKbBox.Text = cu.Kb;
            LcuUbrBox.Text = cu.TargetUbr == 0 ? string.Empty : cu.TargetUbr.ToString();
            LcuMonthTagBox.Text = cu.MonthTag;
        }
        finally { _loadingCu = false; }
        CuSettingsStatus.Text = "CU settings loaded. Edit the fields, then Save CU settings.";
    }

    private void OnCuDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingCu && CuSettingsStatus is not null) CuSettingsStatus.Text = "Unsaved CU changes — Save CU settings to apply all three fields.";
    }

    private void OnSaveCuSettings(object sender, RoutedEventArgs e)
    {
        string kb = Lcu2016CuMatcher.NormalizeKb(LcuKbBox.Text.Trim());
        string raw = LcuUbrBox.Text.Trim();
        string month = LcuMonthTagBox.Text.Trim();
        bool clear = kb.Length == 0 && raw.Length == 0 && month.Length == 0;
        int ubr = 0;
        if (!clear && (kb.Length == 0 || kb.Any(c => c < '0' || c > '9')))
        { CuSettingsStatus.Text = "Enter a KB number such as KB5094122 (digits with an optional KB prefix). Nothing saved."; return; }
        if (!clear && (!int.TryParse(raw, out ubr) || ubr <= 0))
        { CuSettingsStatus.Text = "Enter a positive whole build revision, e.g. 9234 for 14393.9234. Nothing saved."; return; }
        TrySaveCu(new MonthlyCu { Kb = clear ? "" : "KB" + kb, TargetUbr = ubr, MonthTag = month, Arch = _cuBaseline.Arch }, _cuBaseline);
    }

    private bool TrySaveCu(MonthlyCu candidate, MonthlyCu expected)
    {
        try
        {
            _sharedStore.Update(s =>
            {
                var current = s.MonthlyCu ?? new MonthlyCu();
                if (current.Kb != expected.Kb || current.TargetUbr != expected.TargetUbr || current.MonthTag != expected.MonthTag || current.Arch != expected.Arch)
                    throw new InvalidOperationException("CU settings changed elsewhere. Reload saved CU, review the current values, and try again.");
                s.MonthlyCu = candidate;
            });
            LoadCuDraft(candidate);
            CuSettingsStatus.Text = "CU settings saved.";
            return true;
        }
        catch (Exception ex)
        {
            CuSettingsStatus.Text = $"Nothing saved: {ex.Message}";
            _log?.Error(null, $"Couldn't save CU settings: {ex.Message}");
            return false;
        }
    }

    private void OnReloadCuSettings(object sender, RoutedEventArgs e)
    {
        try { LoadCuDraft(_sharedStore.LoadForEditing().MonthlyCu ?? new MonthlyCu()); }
        catch (Exception ex) { CuSettingsStatus.Text = $"Could not reload; your draft is unchanged: {ex.Message}"; }
    }

    private void SetCuEditingEnabled(bool enabled)
    {
        LcuKbBox.IsEnabled = LcuUbrBox.IsEnabled = LcuMonthTagBox.IsEnabled = enabled;
        SaveCuButton.IsEnabled = ReloadCuButton.IsEnabled = enabled;
    }

    private void OnMaxInstallsChanged(object sender, RoutedEventArgs e)
    {
        string raw = MaxInstallsBox.Text.Trim();
        MaxInstallsError.Text = string.Empty;
        if (!int.TryParse(raw, out int parsed) || parsed < 1 || parsed > 200)
        {
            // Keep invalid input visible with an explanation; leave the active setting unchanged.
            MaxInstallsError.Text = "Enter a whole number from 1–200. Your previous setting remains active.";
            return;
        }

        // parsed is already in [1, 200] (guarded above), so no clamp is needed — just normalize the field
        // text (e.g. "050" → "50") so it never displays junk.
        if (parsed.ToString() != raw)
        {
            MaxInstallsBox.Text = parsed.ToString();
        }

        // Personal, per operator — persists to the per-user store (not the machine-wide shared file).
        PersistSettings(s => s.MaxSimultaneousInstalls = parsed);
    }

    private void OnWugStateConcurrencyChanged(object sender, RoutedEventArgs e)
    {
        string raw = WugStateConcurrencyBox.Text.Trim();
        WugStateConcurrencyError.Text = string.Empty;
        if (!int.TryParse(raw, out int parsed) || parsed < 1 || parsed > Vivre.Core.Wug.WugMaintenance.StateReadMaxConcurrency)
        {
            // Keep invalid input visible with an explanation; leave the active setting unchanged.
            WugStateConcurrencyError.Text = "Enter a whole number from 1–4. Your previous setting remains active.";
            return;
        }

        // parsed is already in [1, StateReadMaxConcurrency] (guarded above), so no clamp is needed — just
        // normalize the field text (e.g. "02" → "2") so it never displays junk.
        if (parsed.ToString() != raw)
        {
            WugStateConcurrencyBox.Text = parsed.ToString();
        }

        // Personal, per operator — persists to the per-user store (not the machine-wide shared file).
        PersistSettings(s => s.WugStateConcurrency = parsed);
    }

    private void OnServicingWaitChanged(object sender, RoutedEventArgs e)
    {
        string raw = ServicingWaitBox.Text.Trim();
        ServicingWaitError.Text = string.Empty;
        if (!int.TryParse(raw, out int parsed) || parsed < 5 || parsed > 120)
        {
            // Keep invalid input visible with an explanation; leave the active setting unchanged.
            ServicingWaitError.Text = "Enter a whole number from 5–120. Your previous setting remains active.";
            return;
        }

        // parsed is already in [5, 120] (guarded above), so no clamp is needed — just normalize the field
        // text (e.g. "020" → "20") so it never displays junk.
        if (parsed.ToString() != raw)
        {
            ServicingWaitBox.Text = parsed.ToString();
        }

        // Personal, per operator — persists to the per-user store (not the machine-wide shared file).
        PersistSettings(s => s.ServicingWaitMinutes = parsed);
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
        SetCuEditingEnabled(false);
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

            SharedSettings s = _sharedStore.LoadForEditing();
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

            // Package and typed edits share one atomic save and stale-settings check.
            string confirmedMonthTag = dialog.ConfirmedMonthTag;
            if (!TrySaveCu(new MonthlyCu { Kb = accepted.Kb, TargetUbr = accepted.TargetUbr, Arch = accepted.Arch, MonthTag = confirmedMonthTag }, s.MonthlyCu ?? new MonthlyCu()))
                return;
            CuSettingsStatus.Text = "CU settings saved from package.";

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
            SetCuEditingEnabled(true);
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
            // Sibling-safe: Update reads the shared file fresh and merges ONLY the edited key back. A degraded
            // read (unreadable/corrupt file) makes Update REFUSE (throw) rather than stomp the other keys with
            // defaults — surfaced below as an unmissable "couldn't save" so the operator fixes the file.
            _sharedStore.Update(mutate);
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
