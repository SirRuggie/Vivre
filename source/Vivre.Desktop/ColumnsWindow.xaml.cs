using System.Windows;
using System.Windows.Controls;
using Vivre.Core.Columns;
using Vivre.Core.Models;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// The machine-grid Columns manager: tick/untick the built-in columns to show/hide them, and add custom
/// columns — either from a small predefined gallery or your own PowerShell one-liner. All edits go through
/// the <see cref="WorkspaceViewModel"/> (which persists them to AppData and the view rebuilds the grid);
/// adding a column runs it across the loaded machines. Read-only against the targets — nothing is changed
/// on them. Mirrors the small review dialogs (a <see cref="FluentWindow"/>).
/// </summary>
public partial class ColumnsWindow : FluentWindow
{
    /// <summary>The starter gallery — common, useful one-liners the user can add without writing anything.</summary>
    private static readonly CustomColumnSpec[] Gallery =
    [
        new("Serial", "(Get-CimInstance Win32_BIOS).SerialNumber"),
        new("Model", "(Get-CimInstance Win32_ComputerSystem).Model"),
        new("Days since reboot", "[int]((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalDays"),
        new("Free C: (GB)", "[math]::Round((Get-PSDrive C).Free / 1GB)"),
        // explorer.exe owner = interactive users (console AND RDP) — Win32_ComputerSystem.UserName is
        // blank for RDP sessions. Mirrors what Vitals/details show.
        new("Logged-on user", "@(Get-CimInstance Win32_Process -Filter \"Name='explorer.exe'\" -ErrorAction SilentlyContinue | ForEach-Object { $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner -ErrorAction SilentlyContinue; if ($o.User) { $o.User } } | Sort-Object -Unique) -join ', '"),
        // Full OS caption + version, matching the machine-details line (e.g. "Windows Server 2019 Standard — 10.0.17763").
        new("OS", "$o = Get-CimInstance Win32_OperatingSystem; \"$(($o.Caption -replace '^Microsoft ','').Trim()) — $($o.Version)\""),
    ];

    private readonly WorkspaceViewModel _vm;

    public ColumnsWindow(WorkspaceViewModel vm, IReadOnlyList<string> builtinHeaders)
    {
        InitializeComponent();
        _vm = vm;

        Intro.Text = "Customize the machine grid. Hide built-in columns you don't use, or add your own "
            + "script-backed columns (the value comes from a PowerShell one-liner run on each machine). "
            + "Your layout is saved across launches.";

        // A checkbox per built-in column (Name was already excluded by the caller). Ticked = shown.
        foreach (string header in builtinHeaders)
        {
            var box = new CheckBox
            {
                Content = header,
                IsChecked = !_vm.HiddenColumns.Contains(header),
                Margin = new Thickness(0, 2, 0, 2),
            };
            box.Checked += (_, _) => _vm.SetColumnHidden(header, hidden: false);
            box.Unchecked += (_, _) => _vm.SetColumnHidden(header, hidden: true);
            BuiltinList.Children.Add(box);
        }

        CustomList.ItemsSource = _vm.CustomColumns;   // live — updates as columns are added/removed
        _vm.CustomColumns.CollectionChanged += (_, _) => UpdateNoCustomHint();
        UpdateNoCustomHint();

        GalleryBox.ItemsSource = Gallery;
        GalleryBox.SelectedIndex = 0;
    }

    private void UpdateNoCustomHint() =>
        NoCustomHint.Visibility = _vm.CustomColumns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddFromGallery(object sender, RoutedEventArgs e)
    {
        if (GalleryBox.SelectedItem is CustomColumnSpec spec)
        {
            AddAndRun(spec);
        }
    }

    private void OnAddCustom(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text?.Trim() ?? string.Empty;
        string script = ScriptBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0 || script.Length == 0)
        {
            ShowStatus("Enter a column name and a PowerShell one-liner.");
            return;
        }

        if (name.IndexOfAny(['[', ']', ',']) >= 0)
        {
            ShowStatus("Column name can't contain [ ] or , (they break the cell binding).");
            return;
        }

        AddAndRun(new CustomColumnSpec(name, script));
        NameBox.Text = string.Empty;
        ScriptBox.Text = string.Empty;
    }

    private void OnRemoveColumn(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name })
        {
            _vm.RemoveCustomColumn(name);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        _ = _vm.RunCustomColumnsSelectedAsync([.. _vm.Computers]);

    private void AddAndRun(CustomColumnSpec spec)
    {
        StatusText.Visibility = Visibility.Collapsed;
        _vm.AddCustomColumn(spec);
        // Fill ONLY the new column across the loaded machines — existing columns keep their values
        // (use "Refresh values" to deliberately re-run everything).
        _ = _vm.RunCustomColumnAsync([.. _vm.Computers], spec);
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
