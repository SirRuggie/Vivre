using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Vivre.Core.Columns;
using Vivre.Desktop.ViewModels;

namespace Vivre.Desktop;

public partial class PeopleView : UserControl, IDisposable
{
    public PeopleViewModel Model { get; }
    public PeopleView() : this(new PeopleViewModel()) { }
    public PeopleView(PeopleViewModel model)
    {
        InitializeComponent();
        DataContext = Model = model;
    }
    private async void OnPaste(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = Clipboard.GetText();
            if (Model.Add(text) == 0) return;
            await Model.LookupAsync();
        }
        catch (Exception ex) { Model.Message = $"Could not paste: {ex.Message}"; }
    }
    private async void OnRetry(object sender, RoutedEventArgs e) { Model.QueueRetry(); await Model.LookupAsync(); }
    public bool ConfirmCanClose() => (!Model.IsBusy && !Model.HasUnexportedResults) ||
        System.Windows.MessageBox.Show(Window.GetWindow(this),
            "People lookup has session-only results or work in progress. Exit and discard unexported results? Any remaining lookup will stop.",
            "Exit People lookup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Model.IsBusy || Model.Rows.Count == 0) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this), "Clear all People lookup results and review decisions?",
            "Clear People results", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) Model.Clear();
    }
    private void OnStop(object sender, RoutedEventArgs e) => Model.Stop();
    private void OnReview(object sender, RoutedEventArgs e) => Model.StartReview();
    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (Model.SelectedCandidate is null) { Model.Message = "Select an account before using a match."; return; }
        Model.Accept();
    }
    private void OnSkip(object sender, RoutedEventArgs e) => Model.Skip();
    private void OnCloseReview(object sender, RoutedEventArgs e) => Model.CloseReview();
    private void OnCopyResolved(object sender, RoutedEventArgs e) => CopyEmails(Model.Rows.ToArray());
    private void CopyEmails(PersonLookupRow[] rows)
    {
        var emails = PeopleViewModel.Emails(rows);
        int excluded = rows.Count(r => r.Status != "Resolved" || string.IsNullOrWhiteSpace(r.Email));
        if (CopyText(string.Join(Environment.NewLine, emails)))
            Model.Message = $"{emails.Length} unique emails copied; {excluded} unresolved/no-email entries excluded.";
    }
    private bool CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { Model.Message = "Nothing to copy; the clipboard was left unchanged."; return false; }
        try { Clipboard.SetText(text); return true; }
        catch (Exception ex) { Model.Message = $"Could not copy: {ex.Message}"; return false; }
    }
    public static string Csv(IEnumerable<PersonLookupRow> rows)
    {
        var output = new StringBuilder("Original input,Matched name,Username,Email,Department,Account,Status,Detail\r\n");
        foreach (var row in rows)
            output.AppendLine(string.Join(",", new[] { row.Input, row.MatchedName, row.Username, row.Email, row.Department, row.Account, row.Status, row.Detail }.Select(GridReportCsv.Escape)));
        return output.ToString();
    }
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Model.Rows.Count == 0) { Model.Message = "There are no results to export."; return; }
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "People-email-lookup.csv", AddExtension = true, DefaultExt = ".csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dialog.FileName, Csv(Model.Rows), new UTF8Encoding(true)); Model.MarkExported(); Model.Message = $"Exported {Model.Rows.Count} entries, including unresolved statuses."; }
        catch (Exception ex) { Model.Message = $"Could not export: {ex.Message}"; }
    }
    private static DataGridRow? RowFrom(DependencyObject? element)
    {
        while (element is not null && element is not DataGridRow) element = VisualTreeHelper.GetParent(element);
        return element as DataGridRow;
    }
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFrom(e.OriginalSource as DependencyObject) is { Item: PersonLookupRow row } gridRow && !gridRow.IsSelected)
        {
            ResultsGrid.SelectedItems.Clear(); ResultsGrid.SelectedItem = row;
        }
    }
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFrom(e.OriginalSource as DependencyObject)?.Item is PersonLookupRow row) Model.StartReview(row);
    }
    private void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var rows = ResultsGrid.SelectedItems.Cast<PersonLookupRow>().ToArray();
        var menu = ResultsGrid.ContextMenu;
        menu.Items.Clear();
        void Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        string scope = $"selected ({rows.Length})";
        Add($"Copy names - {scope}", () => CopyField(rows, r => r.MatchedName), rows.Length > 0);
        Add($"Copy usernames - {scope}", () => CopyField(rows, r => r.Username), rows.Length > 0);
        Add($"Copy emails - {scope}", () => CopyEmails(rows), rows.Length > 0);
        Add($"Copy rows as CSV - {scope}", () => CopyText(Csv(rows)), rows.Length > 0);
        menu.Items.Add(new Separator());
        Add("Review/change match...", () => Model.StartReview(rows[0]), rows.Length == 1 && rows[0].Candidates.Count > 0);
        Add("Edit entry and retry...", () => Edit(rows[0]), rows.Length == 1 && !Model.IsBusy);
    }
    private void CopyField(PersonLookupRow[] rows, Func<PersonLookupRow, string> field)
    {
        var values = rows.Where(r => r.Person is not null).Select(field).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (CopyText(string.Join(Environment.NewLine, values))) Model.Message = $"{values.Length} values copied from {rows.Length} selected entries.";
    }
    private async void Edit(PersonLookupRow row)
    {
        var dialog = new TextPromptWindow("Edit lookup entry", "Name, username, or email:", row.Input)
        { Owner = Window.GetWindow(this), AcceptText = "Look up" };
        dialog.ValidateAndAccept = input =>
        {
            try { Model.Edit(row, input); return null; }
            catch (Exception ex) { return ex.Message; }
        };
        if (dialog.ShowDialog() == true) await Model.LookupAsync();
    }
    public void Dispose() => Model.Dispose();
}
