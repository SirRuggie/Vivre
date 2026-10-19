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
    public PeopleViewModel() : this(new AdPeopleLookup()) { }
    public PeopleViewModel(IPeopleLookup lookup)
    {
        _lookup = lookup;
        Results = CollectionViewSource.GetDefaultView(Rows);
        Results.Filter = item => item is PersonLookupRow row &&
            (Filter == "All" || row.Status == Filter);
    }
    public ObservableCollection<PersonLookupRow> Rows { get; } = [];
    public ICollectionView Results { get; }
    public string[] Filters { get; } = ["All", "Resolved", "Needs review", "Not found", "No email", "Lookup failed", "Stopped", "Skipped"];
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _message = "Paste one username, name, or email per line. Reads your current AD domain using your Windows login; results last for this session.";
    [ObservableProperty] private PersonLookupRow? _reviewRow;
    [ObservableProperty] private PersonCandidate? _selectedCandidate;
    public bool HasUnexportedResults { get; private set; }
    public void MarkExported() => HasUnexportedResults = false;
    public void Clear()
    {
        if (IsBusy) return;
        CloseReview(); Rows.Clear(); Filter = "All"; HasUnexportedResults = false; Refresh();
        Message = "Results cleared. Paste people to start a new lookup.";
    }
    public string Summary => $"{Rows.Count} entries | {Rows.Count(r => r.Status == "Resolved")} resolved | {Rows.Count(r => r.Status == "Needs review")} need review";
    public string ReviewLabel => $"Review matches ({Rows.Count(r => r.Status == "Needs review")})";
    public bool HasCandidate => SelectedCandidate is not null;
    partial void OnSelectedCandidateChanged(PersonCandidate? value) => OnPropertyChanged(nameof(HasCandidate));
    public bool HasReview => ReviewRow is not null;
    public string ReviewTitle => ReviewRow is null ? "" : $"Review: {ReviewRow.Input}";
    partial void OnFilterChanged(string value) => Results.Refresh();
    partial void OnReviewRowChanged(PersonLookupRow? value)
    {
        SelectedCandidate = null;
        OnPropertyChanged(nameof(HasReview)); OnPropertyChanged(nameof(ReviewTitle));
    }
    public int Add(string text)
    {
        string[] parsed = PeopleInput.Parse(text);
        if (parsed.Any(s => s.Length > 256)) throw new ArgumentException("Each entry must be at most 256 characters.");
        var existing = Rows.Select(r => r.Input).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = parsed.Where(existing.Add).ToArray();
        if (Rows.Count + additions.Length > PeopleInput.MaxEntries) throw new ArgumentException($"Use at most {PeopleInput.MaxEntries} entries per session.");
        foreach (string input in additions) Rows.Add(new(input));
        Message = $"{additions.Length} added; {parsed.Length - additions.Length} existing entries retained.";
        Refresh();
        return additions.Length;
    }
    public async Task LookupAsync()
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        using var stop = new CancellationTokenSource();
        _stop = stop;
        try
        {
            while (Rows.FirstOrDefault(r => r.Status == "Queued") is { } row)
            {
                if (stop.IsCancellationRequested) break;
                row.Set("Looking up"); Refresh();
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
                Refresh();
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
    public void Stop() { _stop?.Cancel(); Message = "Stopping lookup; waiting for the bounded directory request to finish..."; }
    public void QueueRetry()
    {
        if (IsBusy) return;
        foreach (var row in Rows.Where(r => r.Status is "Lookup failed" or "Stopped" or "Not found"))
            row.Set("Queued", candidates: []);
        Refresh();
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
        if (IsBusy) throw new InvalidOperationException("Stop lookup before editing an entry.");
        input = input.Trim();
        if (input.Length is 0 or > 256 || input.Contains('\r') || input.Contains('\n')) throw new ArgumentException("Enter one name, username, or email (1-256 characters).");
        if (Rows.Any(r => r != row && string.Equals(r.Input, input, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That entry already exists.");
        if (ReviewRow == row) CloseReview();
        row.Edit(input); Refresh();
    }
    public static string[] Emails(IEnumerable<PersonLookupRow> rows) => rows.Where(r => r.Status == "Resolved")
        .Select(r => r.Email.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public void Refresh() { if (Rows.Count > 0) HasUnexportedResults = true; Results.Refresh(); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(ReviewLabel)); }
    public void Dispose() { _disposed = true; _stop?.Cancel(); }
}
