using System.Windows;
using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// The values shown side-by-side in <see cref="LcuPackageReadDialog"/>: the operator's currently-typed CU
/// settings against what Vivre read out of the .msu's embedded metadata, plus the identity/description line and
/// file name that back the read column, and whether the two already match.
/// </summary>
public sealed record LcuPackageReadComparison(
    string CurrentKb,
    string CurrentUbr,
    string CurrentArch,
    string ReadKb,
    string ReadUbr,
    string ReadArch,
    string IdentityDescription,
    string FileName,
    string FileDate,
    string CurrentMonthTag,
    string SuggestedMonthTag,
    bool Matches);

/// <summary>
/// The explicit read-and-confirm step for "Read from package": shows the operator exactly what Vivre read from
/// the .msu next to their current Settings values, and changes nothing on its own. "Use package values" returns
/// <see cref="Window.DialogResult"/> <c>true</c>; "Keep my settings" (the default — also Esc / close) returns
/// false so the caller leaves settings untouched. Values are set on named TextBlocks, never bound to Run.Text.
/// </summary>
public partial class LcuPackageReadDialog : FluentWindow
{
    public LcuPackageReadDialog(LcuPackageReadComparison comparison)
    {
        InitializeComponent();

        CurrentKbText.Text = comparison.CurrentKb;
        CurrentUbrText.Text = comparison.CurrentUbr;
        CurrentArchText.Text = comparison.CurrentArch;

        ReadKbText.Text = comparison.ReadKb;
        ReadUbrText.Text = comparison.ReadUbr;
        ReadArchText.Text = comparison.ReadArch;

        IdentityText.Text = $"Identified as: {comparison.IdentityDescription}";
        FileNameText.Text = $"From file: {comparison.FileName}";
        FileDateText.Text = $"File date: {comparison.FileDate}";

        CurrentMonthTagText.Text = comparison.CurrentMonthTag;
        MonthTagBox.Text = comparison.SuggestedMonthTag; // a suggestion the operator confirms/edits — never authoritative

        MatchesBar.IsOpen = comparison.Matches;

        // Keep-my-settings is the safe default — focus it so a stray Enter/Space never overwrites settings.
        Loaded += (_, _) => KeepButton.Focus();
    }

    /// <summary>The operator's final month label — read only after <see cref="Window.ShowDialog"/> returns true.</summary>
    public string ConfirmedMonthTag => MonthTagBox.Text.Trim();

    private void OnUsePackage(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
