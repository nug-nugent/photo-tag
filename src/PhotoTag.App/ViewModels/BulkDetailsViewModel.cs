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
    private readonly KeywordSuggestions _peopleSuggestions;
    private readonly PlaceSuggestions _places;
    private readonly CancellationTokenSource _cts = new();

    public BulkDetailsViewModel(IReadOnlyList<PhotoItemViewModel> photos, BulkOperations operations, KeywordSuggestions suggestions,
        KeywordSuggestions people, PlaceSuggestions places)
    {
        _peopleSuggestions = people;
        Photos = photos;
        _operations = operations;
        _suggestions = suggestions;
        _places = places;
        TitleField = new BulkTextFieldViewModel(this, TextField.Title, null);
        DescriptionField = new BulkTextFieldViewModel(this, TextField.Description, null);
        LocationField = new BulkTextFieldViewModel(this, TextField.Location, places.For(TextField.Location));
        CityField = new BulkTextFieldViewModel(this, TextField.City, places.For(TextField.City));
        StateField = new BulkTextFieldViewModel(this, TextField.State, places.For(TextField.State));
        CountryField = new BulkTextFieldViewModel(this, TextField.Country, places.For(TextField.Country));
        _textFields = [TitleField, DescriptionField, LocationField, CityField, StateField, CountryField];
        _operations.PropertyChanged += OnOperationsChanged;
        _operations.Completed += OnOperationCompleted;
    }

    public IReadOnlyList<PhotoItemViewModel> Photos { get; }
    public string Heading => $"{Photos.Count:N0} photos selected";
    public bool ExifToolMissing => !_operations.IsAvailable;
    public ObservableCollection<string> KeywordSuggestions => _suggestions.Items;
    public ObservableCollection<BulkKeywordViewModel> Keywords { get; } = [];
    public ObservableCollection<BulkKeywordViewModel> People { get; } = [];
    public ObservableCollection<string> PeopleSuggestions => _peopleSuggestions.Items;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanApplyText))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty] public partial string? LoadingText { get; private set; }
    [ObservableProperty] public partial string? NewKeyword { get; set; }
    [ObservableProperty] public partial string? NewPerson { get; set; }

    /// <summary>True if every selected photo is a favourite.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouriteToolTip))]
    public partial bool AllFavourites { get; private set; }

    public string FavouriteToolTip => AllFavourites ? "Remove all of them from favourites" : "Add all of them to favourites";

    [ObservableProperty] public partial string? FavouriteNote { get; private set; }

    public bool CanEdit => IsLoaded && _operations.IsAvailable && !_operations.IsBusy;

    // --- Title, description and place -------------------------------------------------------
    // Each box shows the value the photos share, if they all have the same one. Typing in a box
    // marks it as edited; nothing is saved until Apply, which first warns what will be replaced.

    private bool _showingText;
    private readonly IReadOnlyList<BulkTextFieldViewModel> _textFields;

    public BulkTextFieldViewModel TitleField { get; }
    public BulkTextFieldViewModel DescriptionField { get; }
    public BulkTextFieldViewModel LocationField { get; }
    public BulkTextFieldViewModel CityField { get; }
    public BulkTextFieldViewModel StateField { get; }
    public BulkTextFieldViewModel CountryField { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApplyText))]
    public partial bool IsConfirmingText { get; private set; }

    /// <summary>What Apply will do, e.g. "This sets the city on all 5 photos, replacing the city 3 of them already have."</summary>
    [ObservableProperty] public partial string? TextWarning { get; private set; }

    public bool HasTextEdits => _textFields.Any(f => f.IsEdited);
    public bool CanApplyText => CanEdit && HasTextEdits;
    public bool ShowApplyText => HasTextEdits && !IsConfirmingText;

    internal void OnTextEdited(BulkTextFieldViewModel field)
    {
        if (_showingText) return;
        field.IsEdited = true;
        OnTextEditsChanged();
    }

    private void OnTextEditsChanged()
    {
        IsConfirmingText = false;
        OnPropertyChanged(nameof(HasTextEdits));
        OnPropertyChanged(nameof(CanApplyText));
        OnPropertyChanged(nameof(ShowApplyText));
    }

    /// <summary>Apply (or Enter in a one-line box): shows what will be replaced, to confirm.</summary>
    [RelayCommand]
    private void ReviewText()
    {
        if (!CanApplyText) return;
        var metadata = Photos.Select(p => p.Metadata ?? new PhotoMetadata()).ToList();
        var parts = _textFields.Where(f => f.IsEdited)
            .Select(f => DescribeChange(f.Field.Lower(), f.Text, metadata.Select(m => m.Get(f.Field))))
            .Append("You can undo it afterwards.");
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
        var values = new Dictionary<TextField, string>();
        foreach (var field in _textFields.Where(f => f.IsEdited))
        {
            values[field.Field] = PhotoMetadataWriter.NormalizeText(field.Text);
            _places.Add(field.Field, field.Text);
            field.IsEdited = false;
        }
        OnTextEditsChanged(); // the boxes refresh from the photos when the edit completes
        return values.Count == 0 ? Task.CompletedTask : _operations.SetTextAsync(Photos, values);
    }

    /// <summary>Puts the boxes back to what the photos have.</summary>
    [RelayCommand]
    private void CancelText()
    {
        foreach (var field in _textFields) field.IsEdited = false;
        OnTextEditsChanged();
        ShowText(Photos.Select(p => p.Metadata ?? new PhotoMetadata()).ToList());
    }

    private void ShowText(IReadOnlyList<PhotoMetadata> metadata)
    {
        _showingText = true;
        foreach (var field in _textFields.Where(f => !f.IsEdited))
            (field.Text, field.Placeholder) = Common(metadata.Select(m => m.Get(field.Field)));
        _showingText = false;
    }

    /// <summary>The value they all share, or "Mixed" as a placeholder when they differ.</summary>
    private static (string Text, string? Placeholder) Common(IEnumerable<string?> values)
    {
        var distinct = values.Select(PhotoMetadataWriter.NormalizeText).Distinct().ToList();
        return distinct is [var shared] ? (shared, null) : ("", "Mixed");
    }

    private static string PhotoCount(int count) => count == 1 ? "1 photo" : $"{count:N0} photos";

    // --- Fill places from GPS --------------------------------------------------------------

    [ObservableProperty] public partial bool IsConfirmingFill { get; private set; }

    /// <summary>What "Fill from GPS" would do, e.g. "Fills in the place on 14 of 21 photos…".</summary>
    [ObservableProperty] public partial string? FillSummary { get; private set; }

    /// <summary>False when there's nothing to fill, so the summary is just information.</summary>
    [ObservableProperty] public partial bool CanConfirmFill { get; private set; }

    public bool AnyGps => Photos.Any(p => p.Metadata is { Latitude: not null, Longitude: not null });

    /// <summary>Shows what filling places from GPS would change, to confirm.</summary>
    [RelayCommand]
    private async Task ReviewFillPlaces()
    {
        var finder = await PlaceFinder.LoadAsync();
        var metadata = Photos.Select(p => p.Metadata ?? new PhotoMetadata()).ToList();
        var noGps = metadata.Count(m => m.Latitude is null || m.Longitude is null);
        var fills = metadata
            .Select(m => (Photo: m, Changes: BulkMetadataEditor.PlacesFromGps(m, finder)))
            .Where(f => f.Changes is not null)
            .ToList();
        var untouched = metadata.Count - noGps - fills.Count;

        var parts = new List<string>();
        if (fills.Count > 0)
        {
            // Name the most common place, as found, so the summary is concrete.
            var places = fills.Select(f => finder.Find(f.Photo.Latitude!.Value, f.Photo.Longitude!.Value)!)
                .GroupBy(p => string.Join(", ", new[] { p.City, p.State, p.Country }.OfType<string>()))
                .OrderByDescending(g => g.Count())
                .ToList();
            var others = places.Count - 1;
            parts.Add($"This fills in the place on {fills.Count:N0} of {metadata.Count:N0} photos: {places[0].Key}"
                      + (others == 0 ? "." : $", and {others:N0} other {(others == 1 ? "place" : "places")}."));
        }
        else
        {
            parts.Add("There's nothing to fill in.");
        }
        if (noGps > 0) parts.Add($"{PhotoCount(noGps)} {(noGps == 1 ? "has" : "have")} no GPS.");
        if (untouched > 0) parts.Add($"{PhotoCount(untouched)} already {(untouched == 1 ? "has" : "have")} a place, or {(untouched == 1 ? "is" : "are")} far from any town.");
        if (fills.Count > 0) parts.Add("Places you've typed are kept, and you can undo it afterwards.");

        FillSummary = string.Join(" ", parts);
        CanConfirmFill = fills.Count > 0;
        IsConfirmingFill = true;
    }

    [RelayCommand]
    private Task ApplyFill()
    {
        IsConfirmingFill = false;
        return _operations.FillPlacesFromGpsAsync(Photos);
    }

    [RelayCommand]
    private void CancelFill() => IsConfirmingFill = false;

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
        if (keywords.Count > 0) await _operations.AddAsync(Photos, ListField.Tags, keywords);
    }

    /// <summary>Adds a tag that only some photos have to the rest of them.</summary>
    [RelayCommand]
    private Task ApplyToAll(BulkKeywordViewModel keyword) => _operations.AddAsync(Photos, ListField.Tags, [keyword.Keyword]);

    [RelayCommand]
    private Task RemoveKeyword(BulkKeywordViewModel keyword) => _operations.RemoveAsync(Photos, ListField.Tags, [keyword.Keyword]);

    [RelayCommand]
    private async Task AddPerson()
    {
        var names = PhotoMetadataWriter.NormalizeKeywords((NewPerson ?? "").Split(','));
        NewPerson = "";
        if (names.Count > 0) await _operations.AddAsync(Photos, ListField.People, names);
    }

    /// <summary>Adds a person that only some photos have to the rest of them.</summary>
    [RelayCommand]
    private Task ApplyPersonToAll(BulkKeywordViewModel person) => _operations.AddAsync(Photos, ListField.People, [person.Keyword]);

    [RelayCommand]
    private Task RemovePerson(BulkKeywordViewModel person) => _operations.RemoveAsync(Photos, ListField.People, [person.Keyword]);

    /// <summary>Unless they're all favourites already, the heart makes them all favourites.</summary>
    [RelayCommand]
    private Task ToggleFavourite() => _operations.SetFavouriteAsync(Photos, !AllFavourites);

    private void Refresh()
    {
        var metadata = Photos.Select(p => p.Metadata ?? new PhotoMetadata()).ToList();

        ShowCounts(Keywords, metadata.Select(m => m.Keywords), _suggestions);
        ShowCounts(People, metadata.Select(m => m.People), _peopleSuggestions);

        var favourites = metadata.Count(m => m.IsFavourite);
        AllFavourites = favourites == metadata.Count;
        FavouriteNote = favourites > 0 && !AllFavourites ? $"{favourites:N0} of {metadata.Count:N0} are favourites; the heart adds the rest." : null;

        ShowText(metadata);
        foreach (var field in TextFields.Places)
            foreach (var m in metadata) _places.Add(field, m.Get(field)); // e.g. places just filled from GPS
        OnPropertyChanged(nameof(AnyGps));
    }

    /// <summary>Tags or people and how many of the photos have each, most common first, keeping the spelling first seen.</summary>
    private void ShowCounts(ObservableCollection<BulkKeywordViewModel> target, IEnumerable<IReadOnlyList<string>> lists,
        KeywordSuggestions suggestions)
    {
        var counts = new Dictionary<string, (string Spelling, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in lists.SelectMany(l => l.Distinct(StringComparer.OrdinalIgnoreCase)))
            counts[value] = counts.TryGetValue(value, out var c) ? (c.Spelling, c.Count + 1) : (value, 1);

        target.Clear();
        foreach (var (spelling, count) in counts.Values.OrderByDescending(c => c.Count).ThenBy(c => c.Spelling, StringComparer.CurrentCultureIgnoreCase))
            target.Add(new BulkKeywordViewModel(spelling, count, Photos.Count));
        suggestions.Add(counts.Values.Select(c => c.Spelling));
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

/// <summary>One text box in the bulk panel: its value, and whether the user has typed in it.</summary>
public partial class BulkTextFieldViewModel(BulkDetailsViewModel owner, TextField textField, ObservableCollection<string>? suggestions)
    : ViewModelBase
{
    public TextField Field => textField;
    public string Label => textField.Label();
    public ObservableCollection<string>? Suggestions => suggestions;

    [ObservableProperty] public partial string? Text { get; set; }
    [ObservableProperty] public partial string? Placeholder { get; internal set; }

    public bool IsEdited { get; internal set; }

    partial void OnTextChanged(string? value) => owner.OnTextEdited(this);
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
