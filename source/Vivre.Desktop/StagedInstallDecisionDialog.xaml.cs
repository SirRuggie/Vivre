using System.Linq;
using System.Windows;
using Vivre.Core.Updates;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>The operator's choice from the "Server 2016 staged update required" dialog.</summary>
public enum StagedInstallChoice
{
    /// <summary>Dismissed — nothing runs.</summary>
    Cancel,

    /// <summary>Stage this month's CU on the flagged boxes first (the recommended path).</summary>
    StageCu,

    /// <summary>Install the non-cumulative updates now via Windows Update; the CU is installed separately.</summary>
    MinorOnly,

    /// <summary>Skip the CU machines and install only the explicitly listed remainder.</summary>
    SkipCu,
}

/// <summary>
/// Shown when an Install / Install-all run includes Server 2016 boxes that are configured for the CU lane but
/// whose monthly CU hasn't been staged yet. It forces a real choice — never silent, never automatic: stage the
/// CU first (recommended), install only minor updates, skip CU machines, or cancel the batch. If Settings' CU KB disagrees
/// with what the boxes actually scanned, a warning is shown at the top. The minor-only path first reveals an
/// inline reboot caution the operator must confirm.
/// </summary>
public partial class StagedInstallDecisionDialog : FluentWindow
{
    private const int MaxNamesShown = 15;

    /// <summary>The operator's choice (defaults to <see cref="StagedInstallChoice.Cancel"/> until a button sets it).</summary>
    public StagedInstallChoice Choice { get; private set; } = StagedInstallChoice.Cancel;

    public StagedInstallDecisionDialog(StagedInstallPlan plan)
    {
        InitializeComponent();

        BoxListText.Text = FormatNames(plan.FlaggedNotStaged.Select(c => c.Name).ToList());

        if (plan.Normal.Count > 0)
        {
            OthersNote.Visibility = Visibility.Visible;
            OthersNote.Text = $"The installation choices also install updates on {plan.Normal.Count} other machine(s). Cancel starts no installations.";
            SkipCuButton.Content = $"Skip CU — install other {plan.Normal.Count}";
            SkipCuButton.Visibility = Visibility.Visible;
        }

        if (plan.Mismatches.Count > 0)
        {
            MismatchBar.Message = FormatMismatch(plan.Mismatches);
            MismatchBar.IsOpen = true;
        }
    }

    private static string FormatNames(System.Collections.Generic.IReadOnlyList<string> names)
    {
        string shown = string.Join("\n", names.Take(MaxNamesShown));
        return names.Count > MaxNamesShown ? $"{shown}\n+{names.Count - MaxNamesShown} more" : shown;
    }

    /// <summary>"Settings show KBx but the scan found a different CU on: BOX (KBy), … — update Settings before installing the CU."</summary>
    private static string FormatMismatch(System.Collections.Generic.IReadOnlyList<StagedCuKbMismatch> mismatches)
    {
        string settingsKb = mismatches[0].SettingsKb; // a single Settings value drives every comparison
        string boxes = string.Join(", ",
            mismatches.Take(MaxNamesShown).Select(m => $"{m.MachineName} (KB{m.ScanKb})"));
        if (mismatches.Count > MaxNamesShown)
        {
            boxes += $", +{mismatches.Count - MaxNamesShown} more";
        }

        return $"Settings show KB{settingsKb}, but a different cumulative update was found on: {boxes}. "
             + "Update Settings ▸ Server 2016 cumulative update before installing the CU.";
    }

    private void OnStageCu(object sender, RoutedEventArgs e)
    {
        Choice = StagedInstallChoice.StageCu;
        DialogResult = true;
        Close();
    }

    /// <summary>"Install minor updates only" — reveal the inline reboot caution; the operator confirms via Proceed.</summary>
    private void OnMinorOnly(object sender, RoutedEventArgs e)
    {
        MinorRebootBar.IsOpen = true;
        ChoiceButtons.Visibility = Visibility.Collapsed;     // swap the footer to Proceed / Back
        MinorConfirmButtons.Visibility = Visibility.Visible;
        MinorProceedButton.Focus();
    }

    private void OnMinorBack(object sender, RoutedEventArgs e)
    {
        MinorRebootBar.IsOpen = false;
        MinorConfirmButtons.Visibility = Visibility.Collapsed;
        ChoiceButtons.Visibility = Visibility.Visible;
    }

    private void OnMinorProceed(object sender, RoutedEventArgs e)
    {
        Choice = StagedInstallChoice.MinorOnly;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = StagedInstallChoice.Cancel;
        DialogResult = false;
        Close();
    }

    private void OnSkipCu(object sender, RoutedEventArgs e)
    {
        Choice = StagedInstallChoice.SkipCu;
        DialogResult = true;
        Close();
    }
}
