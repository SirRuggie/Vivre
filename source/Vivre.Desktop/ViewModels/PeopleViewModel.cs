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
    public int StopVersion { get; private set; }
    public bool CanStop => IsBusy || IsAdding;
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
    public bool CanAdd => !IsAdding;
    partial void OnIsAddingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAdd)); OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(CanClear)); OnPropertyChanged(nameof(CanStop));
    }
    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => Results.Refresh();
    [ObservableProperty] private string _message = "Paste one username, name, or email per line. Reads your current AD domain using your Windows login; results last for this session.";
    [ObservableProperty] private PersonLookupRow? _reviewRow;
    [ObservableProperty] private PersonCandidate? _selectedCandidate;
    public bool HasUnexportedResults { get; private set; }
    public void MarkExported() => HasUnexportedResults = false;
    public void Clear()
    {
        if (IsBusy || IsAdding) return;
        CloseReview(); Rows.Clear(); Filter = "All"; SearchText = ""; HasUnexportedResults = false; Refresh();
        Message = "Results cleared. Paste people to start a new lookup.";
    }
    public string Summary => $"{Rows.Count} entries | {Rows.Count(r => r.Status == "Resolved")} resolved | {Rows.Count(r => r.Status == "Needs review")} need review";
    public string ReviewLabel => $"Review matches ({Rows.Count(r => r.Status == "Needs review")})";
    public bool HasCandidate => SelectedCandidate is not null;
    partial void OnSelectedCandidateChanged(PersonCandidate? value) => OnPropertyChanged(nameof(HasCandidate));
    public bool HasReview => ReviewRow is not null;
    public string ReviewTitle => ReviewRow is null ? "" : $"Review: {ReviewRow.Input}";
    public bool CanRetry => !IsBusy && !IsAdding && Rows.Any(r => r.Status is "Lookup failed" or "Stopped" or "Not found");
    public bool HasRows => Rows.Count > 0;
    public bool HasReviewable => Rows.Any(r => r.Candidates.Count > 0);
    public bool CanClear => !IsBusy && !IsAdding && HasRows;
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(CanClear)); OnPropertyChanged(nameof(CanStop)); }
    partial void OnFilterChanged(string value) => Results.Refresh();
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
                : "Lookup complete. Review uncertain matches; copy or export resolved results.";
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
        foreach (var row in Rows.Where(r => r.Status == "Queued")) row.Set("Stopped", "Not checked after Stop; retry when ready.");
        Message = "Stopping lookup; completed results kept. Pending directory requests may take a moment to finish.";
        Refresh();
    }
    public void QueueRetry()
    {
        if (IsBusy || IsAdding) return;
        var retry = Rows.Where(r => r.Status is "Lookup failed" or "Stopped" or "Not found").ToArray();
        foreach (var row in retry) row.Set("Queued", candidates: []);
        Refresh(changed: retry.Length > 0);
    }
    public void StartReview(PersonLookupRow? row = null)
    {
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
        if (ReviewRow is not { } row || SelectedCandidate is not { } candidate || !row.Candidates.Contains(candidate)) return;
        row.Resolve(candidate.Person, $"Reviewed: {candidate.Reason}");
        Refresh(); StartReview();
    }
    public void Skip()
    {
        if (ReviewRow is not { } row) return;
        row.Set("Skipped", "Skipped for now; use Review match on this row to return.");
        Refresh(); StartReview();
    }
    public void CloseReview() => ReviewRow = null;
    public void Edit(PersonLookupRow row, string input)
    {
        if (IsBusy || IsAdding) throw new InvalidOperationException("Wait for adding to finish and stop lookup before editing an entry.");
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
        if (changed && Rows.Count > 0) HasUnexportedResults = true;
        if (!force && Stopwatch.GetElapsedTime(_lastRefresh).TotalMilliseconds < 100) return;
        _lastRefresh = Stopwatch.GetTimestamp();
        // Row notifications update cells; only filtered membership needs a view reset.
        if (Filter != "All" || !string.IsNullOrWhiteSpace(SearchText)) Results.Refresh();
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(ReviewLabel));
        OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasReviewable)); OnPropertyChanged(nameof(CanClear));
    }
    public void Dispose() { _disposed = true; _stop?.Cancel(); }
}
