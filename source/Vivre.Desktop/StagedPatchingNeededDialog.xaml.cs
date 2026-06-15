using Wpf.Ui.Controls;

namespace Vivre.Desktop;

/// <summary>
/// Guidance shown when the operator clicks a Server 2016 panel action (Clean up / Stage / Verify) but no box
/// is marked for staged patching, so the action would silently do nothing. It explains that those actions
/// only apply to flagged 2016 boxes and how to flag one (right-click ▸ "Mark as Staged patching"), and notes
/// that unmarked 2016 boxes patch via normal Windows Update. Information-only — a single "Got it" closes it;
/// no box is touched. Shown BEFORE any action runs, mirroring the Stage button's package pre-check.
/// </summary>
public partial class StagedPatchingNeededDialog : FluentWindow
{
    public StagedPatchingNeededDialog() => InitializeComponent();
}
