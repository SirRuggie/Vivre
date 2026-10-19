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
    private bool _disposed;
    public PeopleView() : this(new PeopleViewModel()) { }
    public PeopleView(PeopleViewModel model)
    {
        InitializeComponent();
        DataContext = Model = model;
    }
    private async void OnPaste(object sender, RoutedEventArgs e)
    {
        string? entries = null;
        var dialog = new LoadComputersWindow("Paste people", "Paste one name, username, or email per line. Commas within names are kept. Existing results stay in this list.", "Add people", input =>
        {
            // Validate before closing; no directory work or collection mutation in the dialog.
            Model.ValidateAdd(input);
            entries = input;
        }) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && entries is not null) await AddEntriesAsync(entries);
    }
    private async void OnQuickAdd(object sender, RoutedEventArgs e) => await QuickAddAsync();
    private async void OnQuickAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await QuickAddAsync();
    }
    private async Task QuickAddAsync()
    {
        string text = QuickAddBox.Text;
        try
        {
            if (Vivre.Core.People.PeopleInput.Parse(text).Length != 1)
                throw new ArgumentException("Add one person here, or use Paste list for several people.");
            Model.ValidateAdd(text);
        }
        catch (Exception ex) { Model.Message = ex.Message; return; }
        QuickAddBox.Clear();
        if (!await AddEntriesAsync(text) && string.IsNullOrEmpty(QuickAddBox.Text)) QuickAddBox.Text = text;
    }
    private async Task<bool> AddEntriesAsync(string text)
    {
        try
        {
            int stopVersion = Model.StopVersion;
            Model.Message = "Adding people…";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (_disposed || stopVersion != Model.StopVersion) return false;
            var selection = ResultsGrid.SelectedItems.Cast<PersonLookupRow>().ToArray();
            int added = await Model.AddAsync(text);
            foreach (var row in selection) if (Model.Results.Contains(row) && !ResultsGrid.SelectedItems.Contains(row)) ResultsGrid.SelectedItems.Add(row);
            if (added > 0 && stopVersion == Model.StopVersion)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                if (!_disposed && stopVersion == Model.StopVersion) await Model.LookupAsync();
            }
            return true;
        }
        catch (Exception ex) { Model.Message = $"Could not add people: {ex.Message}"; return false; }
    }
    private void OnActions(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string label, RoutedEventHandler action, bool enabled)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += action; menu.Items.Add(item);
        }
        Add("Copy all resolved emails", OnCopyResolved, PeopleViewModel.Emails(Model.Rows).Length > 0);
        Add("Export all to CSV…", OnExport, Model.HasRows);
        menu.Items.Add(new Separator());
        Add("Clear results…", OnClear, Model.CanClear);
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => RowMenuButton.IsEnabled = ResultsGrid.SelectedItems.Count > 0;
    private void OnRowActions(object sender, RoutedEventArgs e)
    {
        BuildRowMenu();
        ResultsGrid.ContextMenu.PlacementTarget = (UIElement)sender;
        ResultsGrid.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        ResultsGrid.ContextMenu.IsOpen = true;
    }
    private void OnOpenReview(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is PersonLookupRow row && row.Candidates.Count > 0) Model.StartReview(row);
        else Model.StartReview();
        RevealReview();
    }
    private void RevealReview()
    {
        if (Model.HasReview) { UpdateLayout(); ReviewPanel.BringIntoView(); }
    }
    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnOpenReview(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && Model.HasReview) { Model.CloseReview(); e.Handled = true; }
    }
    private async void OnRetry(object sender, RoutedEventArgs e) { Model.QueueRetry(); await Model.LookupAsync(); }
    public bool ConfirmCanClose() => (!Model.IsBusy && !Model.IsAdding && !Model.HasUnexportedResults) ||
        System.Windows.MessageBox.Show(Window.GetWindow(this),
            "People lookup has session-only results or work in progress. Exit and discard unexported results? Any remaining lookup will stop.",
            "Exit People lookup", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Model.IsBusy || Model.IsAdding || Model.Rows.Count == 0) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this), "Clear all People lookup results and review decisions?",
            "Clear People results", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) Model.Clear();
    }
    private void OnStop(object sender, RoutedEventArgs e) => Model.Stop();
    private void OnReview(object sender, RoutedEventArgs e) { Model.StartReview(); RevealReview(); }
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
        ResultsGrid.ContextMenu.PlacementTarget = ResultsGrid;
        ResultsGrid.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        if (RowFrom(e.OriginalSource as DependencyObject) is { Item: PersonLookupRow row } gridRow && !gridRow.IsSelected)
        {
            ResultsGrid.SelectedItems.Clear(); ResultsGrid.SelectedItem = row;
        }
    }
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowFrom(e.OriginalSource as DependencyObject)?.Item is PersonLookupRow row) { Model.StartReview(row); RevealReview(); }
    }
    private void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        BuildRowMenu();

    }
    private void BuildRowMenu()
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
        Add("Review/change match...", () => { Model.StartReview(rows[0]); RevealReview(); }, rows.Length == 1 && rows[0].Candidates.Count > 0);
        Add("Edit entry and retry...", () => Edit(rows[0]), rows.Length == 1 && !Model.IsBusy && !Model.IsAdding);
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
    public void Dispose() { _disposed = true; Model.Dispose(); }
}
