using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetadataExtractor;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// The side panel for the selected photo: a larger preview, its metadata, and editors for
/// tags, title, description and favourite. Edits are written to the file straight away.
/// </summary>
public partial class PhotoDetailsViewModel : ViewModelBase, IDisposable
{
    private const int PreviewSize = 1200;
    private readonly CancellationTokenSource _cts = new();
    private readonly PhotoMetadataWriter? _writer;
    private readonly KeywordSuggestions _suggestions;
    private readonly KeywordSuggestions _peopleSuggestions;
    private readonly PlaceSuggestions _places;
    private readonly BulkOperations _operations;
    private readonly PhotoRenderer _renderer;
    private readonly PopularKeywords? _popular;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private int _pendingSaves;
    private Task _lastSave = Task.CompletedTask;
    private bool _applying;
    private readonly Dictionary<TextField, string> _savedText = TextFields.All.ToDictionary(f => f, _ => "");
    private long _fileSize;

    public PhotoDetailsViewModel(PhotoItemViewModel photo, PhotoMetadataWriter? writer, KeywordSuggestions suggestions,
        KeywordSuggestions people, PlaceSuggestions places, BulkOperations operations, PhotoRenderer renderer,
        PopularKeywords? popular = null, IExactPlaceLookup? placeLookup = null)
    {
        _placeLookup = placeLookup;
        _peopleSuggestions = people;
        _places = places;
        Photo = photo;
        _renderer = renderer;
        _writer = writer;
        _suggestions = suggestions;
        _operations = operations;
        _operations.PropertyChanged += OnOperationsChanged;
        _operations.Completed += OnOperationCompleted;
        _popular = popular;
        if (_popular is not null) _popular.Changed += OnPopularChanged;
        Keywords.CollectionChanged += (_, _) => UpdateSuggestedKeywords();
    }

    public PhotoItemViewModel Photo { get; }
    public string FileName => Photo.FileName;
    public string? Folder => System.IO.Path.GetDirectoryName(Photo.Path);
    public bool ExifToolMissing => _writer is null;

    /// <summary>Where tags are saved, for RAW files and RAW+JPEG pairs; null for ordinary images.</summary>
    public string? FilesNote => Photo.File switch
    {
        { Companions: [var raw] } => $"Shot as RAW+JPEG. Tags are saved in this JPEG and in {SidecarName(raw)} beside {System.IO.Path.GetFileName(raw)}.",
        { Companions.Count: > 1 } => $"Shot with {Photo.File.Companions.Count} RAW files. Tags are saved in this JPEG and in each RAW's .xmp sidecar.",
        var f when PhotoFiles.IsRaw(f.Path) => $"RAW file. Tags are saved in {SidecarName(f.Path)} beside it; the RAW itself is never changed.",
        _ => null,
    };

    private static string SidecarName(string raw) =>
        System.IO.Path.GetFileName(PhotoFiles.FindSidecar(raw) ?? PhotoFiles.NewSidecarPath(raw));
    public ObservableCollection<string> KeywordSuggestions => _suggestions.Items;
    public ObservableCollection<string> PeopleSuggestions => _peopleSuggestions.Items;
    public ObservableCollection<string> LocationSuggestions => _places.For(TextField.Location);
    public ObservableCollection<string> CitySuggestions => _places.For(TextField.City);
    public ObservableCollection<string> StateSuggestions => _places.For(TextField.State);
    public ObservableCollection<string> CountrySuggestions => _places.For(TextField.Country);

    [ObservableProperty] public partial Bitmap? Preview { get; private set; }
    [ObservableProperty] public partial string? Error { get; private set; }
    [ObservableProperty] public partial string? DateTaken { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraSummary))]
    public partial string? Camera { get; private set; }

    /// <summary>One line under the file name: "Fujifilm X-T30 · 1/500 s · f/2 · ISO 160 · 23 mm".</summary>
    public string? CameraSummary => JoinNonEmpty(" · ", Camera, Exposure);

    [ObservableProperty] public partial string? Lens { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraSummary))]
    public partial string? Exposure { get; private set; }
    [ObservableProperty] public partial string? Dimensions { get; private set; }
    /// <summary>GPS position, as "50.04213, -5.65432".</summary>
    [ObservableProperty] public partial string? Coordinates { get; private set; }

    /// <summary>The GPS position on OpenStreetMap, for "Open in map"; null without GPS.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGps), nameof(CanLookUpPlace))]
    public partial Uri? MapUri { get; private set; }

    public bool HasGps => MapUri is not null;

    // --- Look up exact place (online) ---------------------------------------------------------
    // One request per click, only when asked: the photo's position goes to OpenStreetMap.

    private readonly IExactPlaceLookup? _placeLookup;

    public bool CanLookUpPlace => _placeLookup is not null && HasGps;

    [ObservableProperty] public partial bool IsShowingLookup { get; private set; }
    [ObservableProperty] public partial bool IsLookingUp { get; private set; }

    /// <summary>"OpenStreetMap: Porthcurno Beach, St Levan, Cornwall, United Kingdom", or why there's nothing.</summary>
    [ObservableProperty] public partial string? LookupText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseLookup))]
    public partial ExactPlace? LookedUp { get; private set; }

    public bool CanUseLookup => LookedUp is not null;

    [RelayCommand]
    private async Task LookUpExactPlace()
    {
        if (_gps is not { } gps || _placeLookup is null) return;
        (LookedUp, LookupText, IsShowingLookup, IsLookingUp) = (null, "Asking OpenStreetMap…", true, true);
        try
        {
            LookedUp = await _placeLookup.LookUpAsync(gps.Latitude, gps.Longitude);
            LookupText = LookedUp is null ? "OpenStreetMap has nothing at this spot." : $"OpenStreetMap: {LookedUp}";
        }
        catch (PlaceLookupException e)
        {
            LookupText = e.Message;
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    [RelayCommand]
    private Task FillFromLookup() => UseLookupAsync(onlyWhereEmpty: true);

    [RelayCommand]
    private Task ReplaceWithLookup() => UseLookupAsync(onlyWhereEmpty: false);

    [RelayCommand]
    private void CloseLookup() => IsShowingLookup = false;

    private async Task UseLookupAsync(bool onlyWhereEmpty)
    {
        if (LookedUp is not { } place) return;
        IsShowingLookup = false;
        var current = new PhotoMetadata { Location = Location, City = City, State = State, Country = Country };
        var changes = place.ToChanges(onlyWhereEmpty, current);
        if (changes.IsEmpty)
        {
            FillNote = onlyWhereEmpty ? "Nothing to fill: those boxes already have something in them." : "Already the same as OpenStreetMap.";
            return;
        }
        FillNote = "From OpenStreetMap (© OpenStreetMap contributors).";
        await ApplyPlaceChangesAsync(changes);
    }
    private (double Latitude, double Longitude)? _gps;

    /// <summary>What "Fill from GPS" did, e.g. "Nearest town: St Ives (0.2 km away)".</summary>
    [ObservableProperty] public partial string? FillNote { get; private set; }

    /// <summary>An OpenStreetMap page with a marker at the position, zoomed to street level.</summary>
    internal static Uri MapLink(double latitude, double longitude) => new(string.Create(CultureInfo.InvariantCulture,
        $"https://www.openstreetmap.org/?mlat={latitude:F6}&mlon={longitude:F6}#map=16/{latitude:F6}/{longitude:F6}"));

    // --- Editable metadata ---------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>
    /// True once metadata has loaded, if ExifTool is available and no bulk edit is running
    /// (which might be rewriting this photo's tags).
    /// </summary>
    public bool CanEdit => IsLoaded && _writer is not null && !_operations.IsBusy;

    public ObservableCollection<string> Keywords { get; } = [];
    public ObservableCollection<string> People { get; } = [];

    [ObservableProperty] public partial string? NewKeyword { get; set; }
    [ObservableProperty] public partial string? NewPerson { get; set; }
    [ObservableProperty] public partial string? Title { get; set; }
    [ObservableProperty] public partial string? Description { get; set; }
    [ObservableProperty] public partial string? Location { get; set; }
    [ObservableProperty] public partial string? City { get; set; }
    [ObservableProperty] public partial string? State { get; set; }
    [ObservableProperty] public partial string? Country { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouriteToolTip), nameof(FavouriteLabel))]
    public partial bool IsFavourite { get; private set; }

    public string FavouriteToolTip => IsFavourite ? "Remove from favourites (F)" : "Add to favourites (F)";

    /// <summary>The favourite button's label: what it is, or what clicking does.</summary>
    public string FavouriteLabel => IsFavourite ? "Favourite" : "Mark favourite";

    [ObservableProperty] public partial string? SaveStatus { get; private set; }
    [ObservableProperty] public partial bool SaveFailed { get; private set; }

    /// <summary>Raised on the UI thread after each successful save.</summary>
    public event EventHandler? Saved;

    /// <summary>Completes when every save started so far has finished (saves run in order).</summary>
    public Task SaveCompletion => _lastSave;

    public async Task LoadAsync()
    {
        var token = _cts.Token;
        var path = Photo.Path;

        // Start both at once; metadata is usually ready well before the preview.
        var metadataTask = Task.Run(() => (Metadata: PhotoMetadata.Read(path), Size: new FileInfo(path).Length), token);
        var previewTask = Task.Run(async () => new Bitmap(new MemoryStream(await _renderer.RenderAsync(path, PreviewSize, token))), token);

        try
        {
            var (metadata, size) = await metadataTask;
            if (!token.IsCancellationRequested)
            {
                Photo.Metadata = metadata;
                Apply(metadata, size);
                IsLoaded = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is ImageProcessingException or IOException or InvalidDataException)
        {
            Error = "This file looks damaged, or isn't an image PhotoTag can read.";
        }
        catch (Exception e)
        {
            Error = $"Couldn't read metadata: {e.Message}";
        }

        try
        {
            var preview = await previewTask;
            if (token.IsCancellationRequested) preview.Dispose();
            else Preview = preview;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Error ??= $"Couldn't open image: {e.Message}";
        }
    }

    [RelayCommand]
    private Task AddKeyword()
    {
        var text = NewKeyword;
        NewKeyword = "";
        return AddToListAsync(ListField.Tags, Keywords, text);
    }

    [RelayCommand]
    private Task RemoveKeyword(string keyword) => RemoveFromListAsync(ListField.Tags, Keywords, keyword);

    /// <summary>The library's most used tags that this photo doesn't have yet, for one-click adding.</summary>
    public ObservableCollection<string> SuggestedKeywords { get; } = [];

    [RelayCommand]
    private Task AddSuggestedKeyword(string keyword) => AddToListAsync(ListField.Tags, Keywords, keyword);

    private void OnPopularChanged(object? sender, EventArgs e) => UpdateSuggestedKeywords();

    partial void OnIsLoadedChanged(bool value) => UpdateSuggestedKeywords();

    private void UpdateSuggestedKeywords()
    {
        var suggested = IsLoaded && _popular is not null ? _popular.Suggest(Keywords, 6).ToList() : [];
        if (suggested.SequenceEqual(SuggestedKeywords)) return;
        SuggestedKeywords.Clear();
        foreach (var keyword in suggested) SuggestedKeywords.Add(keyword);
    }

    [RelayCommand]
    private Task AddPerson()
    {
        var text = NewPerson;
        NewPerson = "";
        return AddToListAsync(ListField.People, People, text);
    }

    [RelayCommand]
    private Task RemovePerson(string name) => RemoveFromListAsync(ListField.People, People, name);

    /// <summary>"beach, family" adds both.</summary>
    private async Task AddToListAsync(ListField field, ObservableCollection<string> list, string? text)
    {
        var toAdd = (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(v => !list.Contains(v, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toAdd.Count == 0) return;

        foreach (var value in toAdd) list.Add(value);
        await SaveAsync(new MetadataChanges().With(field, [.. list]));
    }

    private async Task RemoveFromListAsync(ListField field, ObservableCollection<string> list, string value)
    {
        if (!list.Remove(value)) return;
        await SaveAsync(new MetadataChanges().With(field, [.. list]));
    }

    partial void OnIsFavouriteChanged(bool value) => Photo.IsFavourite = value; // the grid tile's ♥

    [RelayCommand]
    private async Task ToggleFavourite()
    {
        IsFavourite = !IsFavourite;
        await SaveAsync(new MetadataChanges { Favourite = IsFavourite });
    }

    // The text boxes bind with UpdateSourceTrigger=LostFocus, so these fire once per edit.
    partial void OnTitleChanged(string? value) => OnTextChanged(TextField.Title, value);
    partial void OnDescriptionChanged(string? value) => OnTextChanged(TextField.Description, value);
    partial void OnLocationChanged(string? value) => OnTextChanged(TextField.Location, value);
    partial void OnCityChanged(string? value) => OnTextChanged(TextField.City, value);
    partial void OnStateChanged(string? value) => OnTextChanged(TextField.State, value);
    partial void OnCountryChanged(string? value) => OnTextChanged(TextField.Country, value);

    private void OnTextChanged(TextField field, string? value)
    {
        if (_applying || NormalizeText(value) == _savedText[field]) return;
        _ = SaveAsync(new MetadataChanges().With(field, NormalizeText(value)));
    }

    /// <summary>Fills the empty place boxes from the photo's GPS, using the nearest town (offline).</summary>
    [RelayCommand]
    private async Task FillPlacesFromGps()
    {
        if (_gps is not { } gps) return;
        var finder = await PlaceFinder.LoadAsync();
        var current = new PhotoMetadata { Latitude = gps.Latitude, Longitude = gps.Longitude, City = City, State = State, Country = Country };
        var place = finder.Find(gps.Latitude, gps.Longitude);
        if (place is null)
        {
            FillNote = $"No town within {PlaceFinder.MaxDistanceKm:F0} km of this spot.";
            return;
        }

        var near = string.Create(CultureInfo.CurrentCulture, $"Nearest town: {place.City} ({place.DistanceKm:F1} km away).");
        if (BulkMetadataEditor.PlacesFromGps(current, finder) is not { } changes)
        {
            FillNote = $"{near} City, state/province and country are already filled in.";
            return;
        }

        FillNote = near;
        await ApplyPlaceChangesAsync(changes);
    }

    /// <summary>Shows new place values in the boxes and saves them as one edit.</summary>
    private Task ApplyPlaceChangesAsync(MetadataChanges changes)
    {
        _applying = true;
        try
        {
            foreach (var field in TextFields.Places)
                if (changes.Get(field) is { } value) SetText(field, value);
        }
        finally
        {
            _applying = false;
        }
        return SaveAsync(changes);
    }

    private void SetText(TextField field, string? value)
    {
        switch (field)
        {
            case TextField.Title: Title = value; break;
            case TextField.Description: Description = value; break;
            case TextField.Location: Location = value; break;
            case TextField.City: City = value; break;
            case TextField.State: State = value; break;
            case TextField.Country: Country = value; break;
        }
    }

    // Text boxes use the platform's line endings (\r\n on Windows); files store \n.
    private static string NormalizeText(string? value) => PhotoMetadataWriter.NormalizeText(value);

    private Task SaveAsync(MetadataChanges changes)
    {
        if (_writer is null) return Task.CompletedTask;
        return _lastSave = SaveCoreAsync(_writer, changes);
    }

    private async Task SaveCoreAsync(PhotoMetadataWriter writer, MetadataChanges changes)
    {
        _pendingSaves++;
        SaveFailed = false;
        SaveStatus = "Saving…";

        // Saves for one photo run in order. They aren't cancelled when the photo is
        // deselected: a half-finished write is worse than a late one.
        await _saveGate.WaitAsync();
        try
        {
            await writer.WriteAsync(Photo.File, changes); // a RAW+JPEG pair gets both
            Photo.Metadata = null; // re-read next time it's needed
            Saved?.Invoke(this, EventArgs.Empty);
            foreach (var field in TextFields.All)
            {
                if (changes.Get(field) is not { } text) continue;
                _savedText[field] = text;
                _places.Add(field, text);
            }
            if (changes.Keywords is { } keywords) _suggestions.Add(keywords);
            if (changes.People is { } people) _peopleSuggestions.Add(people);
            if (!SaveFailed) SaveStatus = _pendingSaves == 1 ? "Saved" : "Saving…";
        }
        catch (Exception e) when (e is ExifToolException or IOException or UnauthorizedAccessException)
        {
            SaveFailed = true;
            SaveStatus = $"Couldn't save: {e.Message}";
            await ReloadAsync(); // show what's actually in the file
        }
        finally
        {
            _pendingSaves--;
            _saveGate.Release();
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            var path = Photo.Path;
            var (metadata, size) = await Task.Run(() => (PhotoMetadata.Read(path), new FileInfo(path).Length));
            Apply(metadata, size);
        }
        catch (Exception e) when (e is ImageProcessingException or IOException)
        {
        }
    }

    private void OnOperationsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BulkOperations.IsBusy)) OnPropertyChanged(nameof(CanEdit));
    }

    // A bulk edit that included this photo may have changed its tags or favourite.
    private void OnOperationCompleted(object? sender, BulkResult result)
    {
        if (IsLoaded && result.After.TryGetValue(Photo.Path, out var metadata)) Apply(metadata, _fileSize);
    }

    private void Apply(PhotoMetadata m, long fileSize)
    {
        _fileSize = fileSize;
        var culture = CultureInfo.CurrentCulture;

        DateTaken = m.DateTaken?.ToString("dddd d MMMM yyyy, HH:mm", culture);
        Camera = m.Camera;
        Lens = m.LensModel;
        Exposure = JoinNonEmpty(" · ", m.ExposureTime, m.FNumber, m.Iso is { } iso ? $"ISO {iso}" : null, m.FocalLength);
        Dimensions = JoinNonEmpty(" · ",
            m.Width is { } w && m.Height is { } h ? $"{w:N0} × {h:N0}" : null,
            FormatBytes(fileSize));
        if (m.Latitude is { } lat && m.Longitude is { } lon)
        {
            _gps = (lat, lon);
            Coordinates = string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lon:F5}");
            MapUri = MapLink(lat, lon);
        }
        else
        {
            _gps = null;
            Coordinates = null;
            MapUri = null;
        }

        _applying = true;
        try
        {
            Keywords.Clear();
            foreach (var keyword in m.Keywords) Keywords.Add(keyword);
            _suggestions.Add(m.Keywords);
            People.Clear();
            foreach (var person in m.People) People.Add(person);
            _peopleSuggestions.Add(m.People);

            foreach (var field in TextFields.All)
            {
                _savedText[field] = NormalizeText(m.Get(field));
                SetText(field, m.Get(field));
            }
            _places.Add(m);
            IsFavourite = Photo.IsFavourite = m.IsFavourite; // the file wins over a stale index
        }
        finally
        {
            _applying = false;
        }
    }

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var joined = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return joined.Length == 0 ? null : joined;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} bytes",
    };

    public void Dispose()
    {
        _operations.PropertyChanged -= OnOperationsChanged;
        _operations.Completed -= OnOperationCompleted;
        if (_popular is not null) _popular.Changed -= OnPopularChanged;
        _cts.Cancel();
        _cts.Dispose();
        Preview?.Dispose();
        Preview = null;
    }
}
