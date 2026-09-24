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
        _operations.PropertyChanged += OnOperationsChanged;
        _operations.Completed += OnOperationCompleted;
    }

    public IReadOnlyList<PhotoItemViewModel> Photos { get; }
    public string Heading => $"{Photos.Count:N0} photos selected";
    public bool ExifToolMissing => !_operations.IsAvailable;
    public ObservableCollection<string> KeywordSuggestions => _suggestions.Items;
    public ObservableCollection<BulkKeywordViewModel> Keywords { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanApplyText))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty] public partial string? LoadingText { get; private set; }
    [ObservableProperty] public partial string? NewKeyword { get; set; }

    /// <summary>True if every selected photo is a favourite.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouriteGlyph), nameof(FavouriteToolTip))]
    public partial bool AllFavourites { get; private set; }

    public string FavouriteGlyph => AllFavourites ? "♥" : "♡";
    public string FavouriteToolTip => AllFavourites ? "Remove all of them from favourites" : "Add all of them to favourites";

    [ObservableProperty] public partial string? FavouriteNote { get; private set; }

    public bool CanEdit => IsLoaded && _operations.IsAvailable && !_operations.IsBusy;

    // --- Title and description --------------------------------------------------------------
    // The boxes show the value the photos share, if they all have the same one. Typing in a box
    // marks it as edited; nothing is saved until Apply, which first warns what will be replaced.

    private bool _showingText;
    private bool _titleEdited;
    private bool _descriptionEdited;

    [ObservableProperty] public partial string? Title { get; set; }
    [ObservableProperty] public partial string? Description { get; set; }
    [ObservableProperty] public partial string? TitlePlaceholder { get; private set; }
    [ObservableProperty] public partial string? DescriptionPlaceholder { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApplyText))]
    public partial bool IsConfirmingText { get; private set; }

    /// <summary>What Apply will do, e.g. "This sets the title on all 5 photos, replacing the title 3 of them already have."</summary>
    [ObservableProperty] public partial string? TextWarning { get; private set; }

    public bool HasTextEdits => _titleEdited || _descriptionEdited;
    public bool CanApplyText => CanEdit && HasTextEdits;
    public bool ShowApplyText => HasTextEdits && !IsConfirmingText;

    partial void OnTitleChanged(string? value)
    {
        if (_showingText) return;
        _titleEdited = true;
        OnTextEdited();
    }

    partial void OnDescriptionChanged(string? value)
    {
        if (_showingText) return;
        _descriptionEdited = true;
        OnTextEdited();
    }

    private void OnTextEdited()
    {
        IsConfirmingText = false;
        OnPropertyChanged(nameof(HasTextEdits));
        OnPropertyChanged(nameof(CanApplyText));
        OnPropertyChanged(nameof(ShowApplyText));
    }

    /// <summary>Apply (or Enter in the title box): shows what will be replaced, to confirm.</summary>
    [RelayCommand]
    private void ReviewText()
    {
        if (!CanApplyText) return;
        var metadata = Photos.Select(p => p.Metadata ?? new PhotoMetadata()).ToList();
        var parts = new List<string>();
        if (_titleEdited) parts.Add(DescribeChange("title", Title, metadata.Select(m => m.Title)));
        if (_descriptionEdited) parts.Add(DescribeChange("description", Description, metadata.Select(m => m.Description)));
        parts.Add("You can undo it afterwards.");
        TextWarning = string.Join(" ", parts);
        IsConfirmingText = true;
    }

    private string DescribeChange(string field, string? value, IEnumerable<string?> current)
    {
        var text = PhotoMetadataWriter.NormalizeText(value);
        var existing = current.Select(PhotoMetadataWriter.NormalizeText).Where(t => t.Length > 0).ToList();
        if (text.Length == 0)
            return existing.Count == 0 ? $"None of them has a {field} to clear." : $"This clears the {field} on {PhotoCount(existing.Count)}.";
        var replaced = existing.Count(t => t != text);
        return replaced == 0
            ? $"This sets the {field} on all {Photos.Count:N0} photos."
            : $"This sets the {field} on all {Photos.Count:N0} photos, replacing the {field} {replaced:N0} of them already {(replaced == 1 ? "has" : "have")}.";
    }

    [RelayCommand]
    private Task ApplyText()
    {
        IsConfirmingText = false;
        var title = _titleEdited ? PhotoMetadataWriter.NormalizeText(Title) : null;
        var description = _descriptionEdited ? PhotoMetadataWriter.NormalizeText(Description) : null;
        _titleEdited = _descriptionEdited = false;
        OnTextEdited(); // the boxes refresh from the photos when the edit completes
        return title is null && description is null ? Task.CompletedTask : _operations.SetTextAsync(Photos, title, description);
    }

    /// <summary>Puts the boxes back to what the photos have.</summary>
    [RelayCommand]
    private void CancelText()
    {
        _titleEdited = _descriptionEdited = false;
        OnTextEdited();
        ShowText(Photos.Select(p => p.Metadata ?? new PhotoMetadata()).ToList());
    }

    private void ShowText(IReadOnlyList<PhotoMetadata> metadata)
    {
        _showingText = true;
        if (!_titleEdited) (Title, TitlePlaceholder) = Common(metadata.Select(m => m.Title), "title");
        if (!_descriptionEdited) (Description, DescriptionPlaceholder) = Common(metadata.Select(m => m.Description), "description");
        _showingText = false;
    }

    private static (string Text, string? Placeholder) Common(IEnumerable<string?> values, string field)
    {
        var distinct = values.Select(PhotoMetadataWriter.NormalizeText).Distinct().ToList();
        return distinct switch
        {
            [""] => ("", $"Add a {field} to all of them"),
            [var shared] => (shared, null),
            _ => ("", "Different on each photo. Type to replace them all."),
        };
    }

    private static string PhotoCount(int count) => count == 1 ? "1 photo" : $"{count:N0} photos";

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

    /// <summary>Unless they're all favourites already, the heart makes them all favourites.</summary>
    [RelayCommand]
    private Task ToggleFavourite() => _operations.SetFavouriteAsync(Photos, !AllFavourites);

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

        var favourites = metadata.Count(m => m.IsFavourite);
        AllFavourites = favourites == metadata.Count;
        FavouriteNote = favourites > 0 && !AllFavourites ? $"{favourites:N0} of {metadata.Count:N0} are favourites; the heart adds the rest." : null;

        ShowText(metadata);
    }

    private void OnOperationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BulkOperations.IsBusy)) return;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanApplyText));
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
