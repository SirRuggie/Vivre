using System.Diagnostics;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Vivre.Core.People;

namespace Vivre.Desktop.ViewModels;

public sealed class PersonLookupRow(string input) : ObservableObject
{
    public string Input { get; private set; } = input;
    public string Status { get; private set; } = "Queued";
    public string Detail { get; private set; } = "";
    public DirectoryPerson? Person { get; private set; }
    public IReadOnlyList<PersonCandidate> Candidates { get; private set; } = [];
    public string MatchedName => Person?.Name ?? "";
    public string Username => Person?.Username ?? "";
    public string Email => Person?.Email ?? "";
    public string Department => Person?.Department ?? "";
    public string Account => Person is null ? "" : Person.Enabled ? "Enabled" : "Disabled";
    public void Set(string status, string detail = "", DirectoryPerson? person = null, IReadOnlyList<PersonCandidate>? candidates = null)
    {
        Status = status; Detail = detail; Person = person;
        if (candidates is not null) Candidates = candidates;
        OnPropertyChanged(string.Empty);
    }
    public void Resolve(DirectoryPerson person, string reason) =>
        Set(string.IsNullOrWhiteSpace(person.Email) ? "No email" : "Resolved", reason, person);
    public void Edit(string input)
    {
        Input = input; Candidates = []; Set("Queued");
    }
}

public sealed partial class PeopleViewModel : ObservableObject, IDisposable
{
    private readonly IPeopleLookup _lookup;
    private CancellationTokenSource? _stop;
    private bool _disposed;
    private long _lastRefresh;
    private (int Index, PersonLookupRow Row)[] _removedRows = [];
    public bool HasUndoRemoval => _removedRows.Length > 0;
    public bool CanUndoRemoval => HasUndoRemoval && !CanStop;
    public string UndoRemovalLabel => $"Undo removal ({_removedRows.Length})";
    public bool CanExportComplete => HasRows && !CanStop;
    public string ExitSummary => $"{Rows.Count} People entries are kept only for this session. Export all entries and their match/status details before exiting?";
    public int StopVersion { get; private set; }
    public bool CanStop => IsBusy || IsAdding || IsSubmitting;
    public PeopleViewModel() : this(new AdPeopleLookup()) { }
    public PeopleViewModel(IPeopleLookup lookup)
    {
        _lookup = lookup;
        Results = CollectionViewSource.GetDefaultView(Rows);
        Results.Filter = item => item is PersonLookupRow row &&
            (Filter == "All" || row.Status == Filter) &&
            (string.IsNullOrWhiteSpace(SearchText) || new[] { row.Input, row.MatchedName, row.Username, row.Email }
                .Any(value => value.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)));
    }
    public ObservableCollection<PersonLookupRow> Rows { get; } = [];
    public ICollectionView Results { get; }
    public string[] Filters { get; } = ["All", "Resolved", "Needs review", "Not found", "No email", "Lookup failed", "Stopped", "Skipped"];
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private bool _isSubmitting;
    public bool CanAdd => !IsAdding && !IsSubmitting;
    partial void OnIsSubmittingChanged(bool value) => OnIsAddingChanged(value);
    partial void OnIsAddingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(CanClear)); OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanUndoRemoval)); OnPropertyChanged(nameof(CanExportComplete));
        NotifyEmptyState();
    }
    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => RefreshFilters();
    [ObservableProperty] private string _message = "Paste one username, name, or email per line. Reads your current AD domain using your Windows login; results last for this session.";
    [ObservableProperty] private PersonLookupRow? _reviewRow;
    [ObservableProperty] private PersonCandidate? _selectedCandidate;
    [ObservableProperty] private PersonLookupRow? _selectedResult;
    partial void OnSelectedResultChanged(PersonLookupRow? value) => OnPropertyChanged(nameof(CanOpenReview));
    public bool HasUnexportedResults { get; private set; }
    public void MarkExported() => HasUnexportedResults = CanStop;
    public void Clear()
    {
        if (CanStop) return;
        CloseReview(); SelectedResult = null; Rows.Clear(); Filter = "All"; SearchText = ""; HasUnexportedResults = false; Refresh();
        Message = "Results cleared. Paste people to start a new lookup.";
    }
    public int RemoveRows(IEnumerable<PersonLookupRow> rows)
    {
        if (_disposed) return 0;
        if (CanStop) { Message = "Wait for adding to finish and stop lookup before removing entries."; return 0; }
        var targets = rows.ToHashSet();
        if (targets.Count == 0) return 0;
        if (targets.Any(row => !Rows.Contains(row))) { Message = "The results changed. Select the entries again."; return 0; }
        var removed = Rows.Select((row, index) => (Index: index, Row: row)).Where(item => targets.Contains(item.Row)).ToArray();
        if (ReviewRow is { } review && targets.Contains(review)) CloseReview();
        if (SelectedResult is { } selected && targets.Contains(selected)) SelectedResult = null;
        foreach (var item in removed.Reverse()) Rows.RemoveAt(item.Index);
        if (Rows.Count == 0) HasUnexportedResults = false;
        Refresh();
        _removedRows = removed; NotifyUndo();
        Message = $"Removed {removed.Length} entries from these results only. AD accounts are unchanged; Undo is available until the next result change.";
        return removed.Length;
    }
    public void UndoRemoval()
    {
        if (_disposed || !CanUndoRemoval) return;
        var existing = Rows.Select(row => row.Input).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Rows.Count + _removedRows.Length > PeopleInput.MaxEntries || _removedRows.Any(item => !existing.Add(item.Row.Input)))
        { ClearUndo(); Message = "Results changed; undo cannot replace existing entries."; return; }
        var removed = _removedRows;
        ClearUndo();
        foreach (var item in removed) Rows.Insert(Math.Min(item.Index, Rows.Count), item.Row);
        Refresh();
        Message = $"Restored {removed.Length} entries and their previous results.";
    }
    private void ClearUndo()
    {
        if (!HasUndoRemoval) return;
        _removedRows = []; NotifyUndo();
    }
    private void NotifyUndo()
    {
        OnPropertyChanged(nameof(HasUndoRemoval)); OnPropertyChanged(nameof(CanUndoRemoval)); OnPropertyChanged(nameof(UndoRemovalLabel));
    }
    public int VisibleCount { get; private set; }
    public int PendingReviewCount => Rows.Count(r => r.Status == "Needs review");
    public string Summary => $"Showing {VisibleCount} of {Rows.Count} · {Rows.Count(r => r.Status == "Resolved")} resolved · {PendingReviewCount} need review";
    public string ReviewLabel => $"Review matches ({PendingReviewCount})";
    public bool HasPendingReview => PendingReviewCount > 0;
    public bool CanCopyResolved => Rows.Any(r => r.Status == "Resolved" && !string.IsNullOrWhiteSpace(r.Email));
    public bool HasCandidate => SelectedCandidate is not null;
    public string CandidateNotice => SelectedCandidate is not { } candidate ? "Select an account to compare, then use the selected match."
        : string.Join(" ", new[]
        {
            candidate.Person.Enabled ? "" : "This account is disabled.",
            string.IsNullOrWhiteSpace(candidate.Person.Email) ? "No email is recorded; this match will not be included when copying emails." : ""
        }.Where(s => s.Length > 0));
    partial void OnSelectedCandidateChanged(PersonCandidate? value)
    {
        OnPropertyChanged(nameof(HasCandidate)); OnPropertyChanged(nameof(CandidateNotice));
    }
    public bool HasReview => ReviewRow is not null;
    public string ReviewTitle => ReviewRow is null ? "" : $"Review: {ReviewRow.Input} · {PendingReviewCount} need review";
    public bool CanRetry => !CanStop && Rows.Any(r => r.Status is "Lookup failed" or "Stopped" or "Not found");
    public bool HasRows => Rows.Count > 0;
    public bool CanOpenReview => SelectedResult?.Candidates.Count > 0 || HasPendingReview;
    public bool CanClear => !CanStop && HasRows;
    public bool HasFilters => Filter != "All" || !string.IsNullOrWhiteSpace(SearchText);
    public bool ShowEmptyState => VisibleCount == 0;
    public bool ShowEmptyPaste => !HasRows && !CanStop;
    public string EmptyTitle => IsAdding || IsSubmitting ? "Adding people…" : IsBusy ? "Looking up people…"
        : HasRows ? "No people match these filters" : "Add or paste people to get started";
    public string EmptyDetail => CanStop ? "Results will appear here as lookup progresses."
        : HasRows ? "Clear the filters to see all entries; your results are kept."
        : "Use Add for one person, or paste a list with one name, username, or email per line.";
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(CanClear)); OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanUndoRemoval)); OnPropertyChanged(nameof(CanExportComplete));
        NotifyEmptyState();
    }
    partial void OnFilterChanged(string value) => RefreshFilters();
    public void ClearFilters() { Filter = "All"; SearchText = ""; }
    private void RefreshFilters()
    {
        Results.Refresh();
        RefreshViewState();
    }
    private void RefreshViewState()
    {
        VisibleCount = Results.Cast<PersonLookupRow>().Count();
        OnPropertyChanged(nameof(VisibleCount)); OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasFilters));
        NotifyEmptyState();
    }
    private void NotifyEmptyState()
    {
        OnPropertyChanged(nameof(ShowEmptyState)); OnPropertyChanged(nameof(ShowEmptyPaste));
        OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyDetail));
    }
    partial void OnReviewRowChanged(PersonLookupRow? value)
    {
        SelectedCandidate = null;
        OnPropertyChanged(nameof(HasReview)); OnPropertyChanged(nameof(ReviewTitle));
    }
    private string[] PrepareAdd(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsAdding) throw new InvalidOperationException("Wait for the current list to finish adding.");
        if (text.Length > 600000) throw new ArgumentException("Use at most 2000 entries, one per line.");
        string[] parsed = PeopleInput.Parse(text);
        if (parsed.Length == 0) throw new ArgumentException("Enter at least one name, username, or email.");
        if (parsed.Any(s => s.Length > 256)) throw new ArgumentException("Each entry must be at most 256 characters.");
        var existing = Rows.Select(r => r.Input).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = parsed.Where(existing.Add).ToArray();
        if (Rows.Count + additions.Length > PeopleInput.MaxEntries) throw new ArgumentException($"Use at most {PeopleInput.MaxEntries} entries per session.");
        return additions;
    }
    public void ValidateAdd(string text) => PrepareAdd(text);
    public int Add(string text)
    {
        var additions = PrepareAdd(text);
        foreach (string input in additions) Rows.Add(new(input));
        Message = $"{additions.Length} added; existing results retained.";
        Refresh(changed: additions.Length > 0);
        return additions.Length;
    }
    public async Task<int> AddAsync(string text)
    {
        var additions = PrepareAdd(text);
        if (additions.Length == 0) { Message = "No new people; existing results retained."; return 0; }
        int stopVersion = StopVersion;
        IsAdding = true;
        int added = 0;
        try
        {
            foreach (string input in additions)
            {
                if (_disposed) break;
                var row = new PersonLookupRow(input);
                if (stopVersion != StopVersion) row.Set("Stopped", "Not checked after Stop; retry when ready.");
                Rows.Add(row); added++;
                HasUnexportedResults = true;
                if (added % 100 == 0 && SynchronizationContext.Current is DispatcherSynchronizationContext)
                {
                    Message = $"Adding people: {added} of {additions.Length}…";
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
            Message = stopVersion == StopVersion ? $"{added} added; existing results retained." : $"{added} added; lookup stopped. Retry when ready.";
            return added;
        }
        finally { IsAdding = false; Refresh(changed: added > 0); }
    }
    public async Task LookupAsync()
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        using var stop = new CancellationTokenSource();
        _stop = stop;
        try
        {
            int completed = 0;
            while (Rows.FirstOrDefault(r => r.Status == "Queued") is { } row)
            {
                if (stop.IsCancellationRequested) break;
                row.Set("Looking up"); Refresh(force: false);
                Message = $"Looking up entry {Rows.IndexOf(row) + 1} of {Rows.Count}...";
                try
                {
                    var result = await _lookup.LookupAsync(row.Input, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    row.Set(result.Candidates.Count == 0 ? "Not found" : "Needs review",
                        result.Notice ?? (result.Exact ? "Multiple or disabled exact-match accounts; choose a person." : "Suggested accounts require your review."),
                        candidates: result.Candidates);
                    if (PersonMatching.CanResolve(result))
                        row.Resolve(result.Candidates[0].Person, "Unique exact match");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { row.Set("Stopped", "Not checked; retry when ready."); break; }
                catch (ArgumentException ex) { row.Set("Lookup failed", ex.Message); }
                catch (Exception ex)
                {
                    row.Set("Lookup failed", ex.Message);
                    Message = $"Lookup stopped: {ex.Message}";
                    foreach (var pending in Rows.Where(r => r.Status == "Queued")) pending.Set("Stopped", "Not checked after a directory error.");
                    return;
                }
                Refresh(force: false);
                // Fast/completed responses must not monopolize the dispatcher.
                if (++completed % 25 == 0 && SynchronizationContext.Current is DispatcherSynchronizationContext)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }
            Message = stop.IsCancellationRequested ? "Lookup stopped; completed results kept. Retry unresolved entries when ready."
                : $"Lookup complete: {Rows.Count(r => r.Status == "Resolved")} resolved; {PendingReviewCount} need review. Copy resolved emails or export all entries to CSV.";
        }
        finally
        {
            foreach (var pending in Rows.Where(r => r.Status == "Queued")) pending.Set("Stopped", "Not checked.");
            _stop = null; IsBusy = false; Refresh();
        }
    }
    public void Stop()
    {
        StopVersion++;
        _stop?.Cancel();
        var queued = Rows.Where(r => r.Status == "Queued").ToArray();
        foreach (var row in queued) row.Set("Stopped", "Not checked after Stop; retry when ready.");
        Message = "Stopping lookup; completed results kept. Pending directory requests may take a moment to finish.";
        Refresh(changed: queued.Length > 0);
    }
    public void QueueRetry()
    {
        if (CanStop) return;
        var retry = Rows.Where(r => r.Status is "Lookup failed" or "Stopped" or "Not found").ToArray();
        foreach (var row in retry) row.Set("Queued", candidates: []);
        Refresh(changed: retry.Length > 0);
    }
    public void StartReview(PersonLookupRow? row = null)
    {
        if (row is not null && !Rows.Contains(row)) { Message = "That entry is no longer in these results."; return; }
        if (row is not null && row.Candidates.Count == 0)
        {
            CloseReview();
            Message = $"No candidate accounts for {row.Input}. Edit the entry and retry.";
            return;
        }
        ReviewRow = row ?? Rows.FirstOrDefault(r => r.Status == "Needs review");
        Message = ReviewRow is null ? "No uncertain matches to review." : $"Reviewing {ReviewRow.Input}; choose an account or skip for now.";
    }
    public void Accept()
    {
        if (ReviewRow is not { } row || !Rows.Contains(row) || SelectedCandidate is not { } candidate || !row.Candidates.Contains(candidate)) return;
        row.Resolve(candidate.Person, $"Reviewed: {candidate.Reason}");
        Refresh(); StartReview();
    }
    public void Skip()
    {
        if (ReviewRow is not { } row || !Rows.Contains(row)) return;
        row.Set("Skipped", "Skipped for now; use Review match on this row to return.");
        Refresh(); StartReview();
    }
    public void CloseReview() => ReviewRow = null;
    public void Edit(PersonLookupRow row, string input)
    {
        if (CanStop) throw new InvalidOperationException("Wait for adding to finish and stop lookup before editing an entry.");
        if (!Rows.Contains(row)) throw new InvalidOperationException("That entry is no longer in these results.");
        input = input.Trim();
        if (input.Length is 0 or > 256 || input.Contains('\r') || input.Contains('\n')) throw new ArgumentException("Enter one name, username, or email (1-256 characters).");
        if (Rows.Any(r => r != row && string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That entry already exists.");
        if (ReviewRow == row) CloseReview();
        row.Edit(input); Refresh();
    }
    public static string[] Emails(IEnumerable<PersonLookupRow> rows) => rows.Where(r => r.Status == "Resolved")
        .Select(r => r.Email.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public void Refresh(bool changed = true, bool force = true)
    {
        if (changed) ClearUndo();
        if (changed && Rows.Count > 0) HasUnexportedResults = true;
        if (!force && Stopwatch.GetElapsedTime(_lastRefresh).TotalMilliseconds < 100) return;
        _lastRefresh = Stopwatch.GetTimestamp();
        // Row notifications update cells; only filtered membership needs a view reset.
        if (Filter != "All" || !string.IsNullOrWhiteSpace(SearchText)) Results.Refresh();
        RefreshViewState();
        OnPropertyChanged(nameof(ReviewLabel)); OnPropertyChanged(nameof(ReviewTitle));
        OnPropertyChanged(nameof(HasPendingReview)); OnPropertyChanged(nameof(CanCopyResolved));
        OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CanOpenReview)); OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanExportComplete)); OnPropertyChanged(nameof(ExitSummary));
    }
    public void Dispose() { _disposed = true; ClearUndo(); _stop?.Cancel(); }
}
