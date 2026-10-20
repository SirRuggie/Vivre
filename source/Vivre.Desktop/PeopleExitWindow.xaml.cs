using System.Windows;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

public partial class PeopleExitWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly PeopleViewModel _model;
    private readonly Func<Window, bool> _export;
    public PeopleExitWindow(PeopleViewModel model, Func<Window, bool> export)
    {
        InitializeComponent();
        DataContext = _model = model;
        _export = export;
        Loaded += (_, _) => CancelButton.Focus();
    }
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (!_model.CanExportComplete) return;
        if (_export(this) && !_model.HasUnexportedResults && !_model.CanStop) DialogResult = true;
        else ExportStatus.Text = _model.Message;
    }
    // Approval is provisional: another shell close safeguard may still veto exit.
    // Dispose/Stop belongs to actual window closure, not to this dialog.
    private void OnDiscard(object sender, RoutedEventArgs e) => DialogResult = true;
}
