using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// The side panel when several photos are selected: which tags they have (and on how many),
/// and editors that apply a change to all of them.
/// </summary>
public partial class BulkDetailsViewModel : ViewModelBase, IDisposable
{
    private readonly BulkOperations _operations;
    private readonly KeywordSuggestions _suggestions;
    private readonly CancellationTokenSource _cts = new();

    public BulkDetailsViewModel(IReadOnlyList<PhotoItemViewModel> photos, BulkOperations operations, KeywordSuggestions suggestions)
    {
        Photos = photos;
        _operations = operations;
        _suggestions = suggestions;
        Stars = [.. Enumerable.Range(1, 5).Select(i => new StarViewModel(i))];
        _operations.PropertyChanged += OnOperationsChanged;
        _operations.Completed += OnOperationCompleted;
    }

    public IReadOnlyList<PhotoItemViewModel> Photos { get; }
    public string Heading => $"{Photos.Count:N0} photos selected";
    public bool ExifToolMissing => !_operations.IsAvailable;
    public ObservableCollection<string> KeywordSuggestions => _suggestions.Items;
    public ObservableCollection<BulkKeywordViewModel> Keywords { get; } = [];
    public IReadOnlyList<StarViewModel> Stars { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty] public partial string? LoadingText { get; private set; }
    [ObservableProperty] public partial string? NewKeyword { get; set; }

    /// <summary>The rating shared by every selected photo, or 0 if they differ or have none.</summary>
    [ObservableProperty] public partial int CommonRating { get; private set; }

    [ObservableProperty] public partial string? RatingNote { get; private set; }

    public bool CanEdit => IsLoaded && _operations.IsAvailable && !_operations.IsBusy;

    /// <summary>Reads metadata for any selected photo we haven't seen yet, then builds the summary.</summary>
    public async Task LoadAsync()
    {
        var token = _cts.Token;
        var missing = Photos.Where(p => p.Metadata is null).ToList();
        if (missing.Count > 0)
        {
            LoadingText = $"Reading tags from {missing.Count:N0} photos…";
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token };
                await Parallel.ForEachAsync(missing, options, (photo, _) =>
                {
                    try
                    {
                        photo.Metadata = PhotoMetadata.Read(photo.Path);
                    }
                    catch (Exception e) when (e is IOException or MetadataExtractor.ImageProcessingException)
                    {
                        photo.Metadata = new PhotoMetadata(); // unreadable: treat as untagged
                    }
                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        LoadingText = null;
        Refresh();
        IsLoaded = true;
    }

    [RelayCommand]
    private async Task AddKeyword()
    {
        var keywords = PhotoMetadataWriter.NormalizeKeywords((NewKeyword ?? "").Split(','));
        NewKeyword = "";
        if (keywords.Count > 0) await _operations.AddKeywordsAsync(Photos, keywords);
    }

    /// <summary>Adds a tag that only some photos have to the rest of them.</summary>
    [RelayCommand]
    private Task ApplyToAll(BulkKeywordViewModel keyword) => _operations.AddKeywordsAsync(Photos, [keyword.Keyword]);

    [RelayCommand]
    private Task RemoveKeyword(BulkKeywordViewModel keyword) => _operations.RemoveKeywordsAsync(Photos, [keyword.Keyword]);

    /// <summary>Clicking the rating they all share clears it.</summary>
    [RelayCommand]
    private Task SetRating(int stars) => _operations.SetRatingAsync(Photos, stars == CommonRating ? 0 : stars);

    private void Refresh()
    {
        var metadata = Photos.Select(p => p.Metadata ?? new PhotoMetadata()).ToList();

        // Tags, most common first, keeping the spelling first seen.
        var counts = new Dictionary<string, (string Spelling, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in metadata.SelectMany(m => m.Keywords.Distinct(StringComparer.OrdinalIgnoreCase)))
            counts[keyword] = counts.TryGetValue(keyword, out var c) ? (c.Spelling, c.Count + 1) : (keyword, 1);

        Keywords.Clear();
        foreach (var (spelling, count) in counts.Values.OrderByDescending(c => c.Count).ThenBy(c => c.Spelling, StringComparer.CurrentCultureIgnoreCase))
            Keywords.Add(new BulkKeywordViewModel(spelling, count, Photos.Count));
        _suggestions.Add(counts.Values.Select(c => c.Spelling));

        var ratings = metadata.Select(m => m.Rating ?? 0).Distinct().ToList();
        CommonRating = ratings is [var only] ? only : 0;
        RatingNote = ratings.Count > 1 ? "Mixed ratings; clicking a star sets it on all of them." : null;
        foreach (var star in Stars) star.IsFilled = star.Value <= CommonRating;
    }

    private void OnOperationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BulkOperations.IsBusy)) OnPropertyChanged(nameof(CanEdit));
    }

    private void OnOperationCompleted(object? sender, BulkResult result)
    {
        if (IsLoaded) Refresh();
    }

    public void Dispose()
    {
        _operations.PropertyChanged -= OnOperationsChanged;
        _operations.Completed -= OnOperationCompleted;
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed class BulkKeywordViewModel(string keyword, int count, int total)
{
    public string Keyword { get; } = keyword;
    public int Count { get; } = count;
    public bool IsOnAll { get; } = count == total;

    /// <summary>"3/5" for tags only some photos have; empty when all have it.</summary>
    public string CountText { get; } = count == total ? "" : $"{count}/{total}";

    public string ToolTip { get; } = count == total
        ? $"On all {total} photos"
        : $"On {count} of {total} photos";
}
