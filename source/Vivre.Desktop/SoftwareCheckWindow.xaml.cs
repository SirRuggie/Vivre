using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vivre.Core.Models;
using Vivre.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// Asks for a product name (and optionally its service), then checks the in-scope machines — Vivre
/// fills each row's Software column with the matched product + version, and the service state when
/// asked. Read-only, so unlike Stage/Install it runs immediately on Check with no confirm.
///
/// <para>The service field defaults to the product name (the "is the service named like the product?"
/// shortcut) and is editable; the product→service pair is remembered (in <see cref="AppSettings"/>) so a
/// known agent's service is pre-filled next time. Seeded with the common agents (CrowdStrike → CSFalconService,
/// SentinelOne → SentinelAgent).</para>
/// </summary>
public partial class SoftwareCheckWindow : FluentWindow
{
    private readonly WorkspaceViewModel _vm;
    private readonly IReadOnlyList<Computer> _computers;
    private readonly AppSettingsStore _settings = new();
    private readonly Dictionary<string, string> _serviceMap;

    public SoftwareCheckWindow(WorkspaceViewModel vm, IReadOnlyList<Computer> computers)
    {
        InitializeComponent();
        _vm = vm;
        _computers = computers;
        _serviceMap = LoadServiceMap();

        Intro.Text = $"Check {computers.Count} machine(s) for an installed product. Type a product name "
            + "(or part of one) and Vivre fills each row's Software column with the match — handy to confirm "
            + "an agent like SentinelOne or CrowdStrike is present (and running) after a deploy.";
        CheckButton.Content = $"Check on {computers.Count}";
        Loaded += (_, _) => QueryBox.Focus();
    }

    private Dictionary<string, string> LoadServiceMap()
    {
        try
        {
            // Rebuild as case-insensitive — a JSON round-trip resets the stored dictionary to ordinal.
            return new Dictionary<string, string>(_settings.Load().SoftwareServiceMap, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A settings read failure just means no remembered services — the field still works.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>As the product is typed/picked, auto-fill a remembered service (and tick the box) for a
    /// known agent — so CrowdStrike pre-fills CSFalconService without you retyping it.</summary>
    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        string product = QueryBox.Text?.Trim() ?? string.Empty;
        if (product.Length > 0 && _serviceMap.TryGetValue(product, out string? remembered))
        {
            ServiceBox.Text = remembered;
            ServiceCheck.IsChecked = true;
        }
    }

    private void OnServiceToggled(object sender, RoutedEventArgs e)
    {
        ServicePanel.Visibility = ServiceCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        // When turned on with nothing entered, default the service to the product name (the common case)
        // or the remembered service for it.
        if (ServiceCheck.IsChecked == true && string.IsNullOrWhiteSpace(ServiceBox.Text))
        {
            string product = QueryBox.Text?.Trim() ?? string.Empty;
            ServiceBox.Text = product.Length > 0 && _serviceMap.TryGetValue(product, out string? remembered)
                ? remembered
                : product;
        }
    }

    private void OnQuickPick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        // Tag is "product|service": fill the product name and pre-fill + tick the service, but do NOT run
        // — let the user review (and untick the service if they don't want it) before clicking Check.
        string[] parts = tag.Split('|');
        QueryBox.Text = parts[0];
        if (parts.Length > 1 && parts[1].Length > 0)
        {
            ServiceCheck.IsChecked = true;
            ServiceBox.Text = parts[1];
        }

        QueryBox.Focus();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Run();
        }
    }

    private void OnCheck(object sender, RoutedEventArgs e) => Run();

    private void Run()
    {
        string query = QueryBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            ShowStatus("Enter a product name to check for.");
            return;
        }

        string? serviceName = null;
        if (ServiceCheck.IsChecked == true)
        {
            serviceName = ServiceBox.Text?.Trim();
            if (string.IsNullOrEmpty(serviceName))
            {
                ShowStatus("Enter the service name to check, or untick \"Also check its service\".");
                return;
            }

            RememberService(query, serviceName);
        }

        _ = _vm.CheckSoftwareSelectedAsync(_computers, query, serviceName);
        Close();
    }

    /// <summary>Persists the product→service pair so it pre-fills next time (best-effort).</summary>
    private void RememberService(string product, string serviceName)
    {
        try
        {
            AppSettings s = _settings.Load();
            // Keep the map case-insensitive on write so "crowdstrike" doesn't duplicate "CrowdStrike".
            var map = new Dictionary<string, string>(s.SoftwareServiceMap, StringComparer.OrdinalIgnoreCase)
            {
                [product] = serviceName,
            };
            s.SoftwareServiceMap = map;
            _settings.Save(s);
        }
        catch
        {
            // Remembering is a convenience; a save failure must not block the check.
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
