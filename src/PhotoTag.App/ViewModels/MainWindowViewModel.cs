using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>How a click or key press changes the selection.</summary>
public enum SelectionGesture
{
    /// <summary>Plain click: select just this photo.</summary>
    Replace,

    /// <summary>Ctrl/⌘-click: add or remove this photo.</summary>
    Toggle,

    /// <summary>Shift-click: select everything from the anchor to this photo.</summary>
    Range,

    /// <summary>Ctrl/⌘+Shift-click: add that range to the current selection.</summary>
    AddRange,
}

/// <summary>One step of the path above the grid: "Photos / 2024 / Cornwall".</summary>
public sealed record BreadcrumbItem(string Name, string? Path, bool IsLast);

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ThumbnailCache _thumbnails;
    private readonly AppSettings _settings;
    private readonly PhotoMetadataWriter? _writer;
    private readonly PhotoRenderer _renderer;
    private readonly KeywordSuggestions _keywordSuggestions = new();
    private readonly PlaceSuggestions _placeSuggestions = new();
    private readonly KeywordSuggestions _peopleSuggestions = new();
    private readonly PopularKeywords _popularKeywords = new();
    private readonly HashSet<PhotoItemViewModel> _selection = [];
    private CancellationTokenSource? _photosLoad;
    private int _anchorIndex = -1;

    /// <summary>The grid's photos in the order they were loaded (file name, or folder then name for results).</summary>
    private IReadOnlyList<PhotoItemViewModel> _loaded = [];

    private readonly IExactPlaceLookup? _placeLookup;

    /// <summary>Bumped per refresh, so a slow index query can't overwrite a newer one's results.</summary>
    private int _summariesVersion;

    /// <param name="placeLookup">For "Look up exact place" (online); without one, the link isn't shown.</param>
    public MainWindowViewModel(ThumbnailCache thumbnails, AppSettings settings, PhotoMetadataWriter? writer, LibraryIndex index,
        PhotoRenderer? renderer = null, IAppUpdater? updater = null, IExactPlaceLookup? placeLookup = null)
    {
        _placeLookup = placeLookup;
        Updates = new UpdatesViewModel(updater ?? new NoUpdates(), settings);
        _renderer = renderer ?? PhotoRenderer.ImagesOnly;
        _thumbnails = thumbnails;
        _settings = settings;
        _writer = writer;
        if (writer is not null) writer.PreserveModifiedTime = settings.PreserveModifiedTime;
        Library = new LibraryViewModel(index, _keywordSuggestions, _peopleSuggestions, _placeSuggestions);
        Library.CountsChanged += (_, _) => _ = RefreshFolderCountsAsync();
        Library.CountsChanged += (_, _) => _ = RefreshSummariesAsync();
        Operations = new BulkOperations(writer, _keywordSuggestions, _peopleSuggestions);
        Operations.Summary += (_, summary) => StatusText = summary;
        Operations.Completed += (_, result) => _ = Library.PhotosChangedAsync(result.After);
        TagManager = new TagManagerViewModel(Library, Operations, () => RootPath, () => Photos);
    }

    public ObservableCollection<FolderNodeViewModel> RootFolders { get; } = [];
    public BulkOperations Operations { get; }
    public LibraryViewModel Library { get; }
    public TagManagerViewModel TagManager { get; }
    public UpdatesViewModel Updates { get; }

    /// <summary>Why tags can't be edited, shown in each panel when there's no writer.</summary>
    public string ExifToolMissingText { get; init; } = ExifToolMissingMessage(ExifToolStatus.NotFound);

    public static string ExifToolMissingMessage(ExifToolStatus status) => status switch
    {
        ExifToolStatus.PerlMissing when OperatingSystem.IsMacOS() =>
            "Editing tags needs Perl, which ExifTool runs on, and this Mac doesn't have it. Install it (brew install perl), then restart PhotoTag.",
        ExifToolStatus.PerlMissing =>
            "Editing tags needs Perl, which ExifTool runs on, and it isn't installed. Install your system's perl package, then restart PhotoTag.",
        _ => "Editing tags needs ExifTool (exiftool.org). Install it, then restart PhotoTag.",
    };
    public ObservableCollection<string> KeywordSuggestions => _keywordSuggestions.Items;

    [ObservableProperty] public partial string? RootPath { get; private set; }
    [ObservableProperty] public partial FolderNodeViewModel? SelectedFolder { get; set; }

    /// <summary>The photo last clicked or moved to with the keyboard.</summary>
    [ObservableProperty] public partial PhotoItemViewModel? CurrentPhoto { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText), nameof(HasSeveralSelected))]
    public partial int SelectedCount { get; private set; }

    public string? SelectionText => SelectedCount > 1 ? $"{SelectedCount:N0} selected" : null;

    /// <summary>Shows the command bar over the grid.</summary>
    public bool HasSeveralSelected => SelectedCount > 1;

    /// <summary>A <see cref="PhotoDetailsViewModel"/> for one photo, a <see cref="BulkDetailsViewModel"/> for several.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewerDetails))]
    public partial ViewModelBase? Details { get; private set; }

    [ObservableProperty] public partial string StatusText { get; private set; } = "Open a folder to get started.";

    /// <summary>
    /// The grid's photos in display order. A plain list, replaced wholesale per folder, search or
    /// sort, so the grid gets one reset notification instead of one per photo.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<PhotoItemViewModel> Photos { get; private set; } = [];

    /// <summary>What the grid shows: the photos, with a <see cref="DayHeaderViewModel"/> before each day when grouped.</summary>
    public ResettableList<object> GridItems { get; } = new();

    /// <summary>The day headings, for "Jump to day". Empty unless grouped by day.</summary>
    [ObservableProperty] public partial IReadOnlyList<DayHeaderViewModel> Days { get; private set; } = [];

    /// <summary>"Jump to day": the days nested in years and months.</summary>
    [ObservableProperty] public partial IReadOnlyList<DateNodeViewModel> DateTree { get; private set; } = [];

    /// <summary>Raised when tiles change size (a favourite added while favourites are highlighted), so the grid re-lays out.</summary>
    public event EventHandler? GridLayoutChanged;

    /// <summary>Selected photos in display order.</summary>
    public IReadOnlyList<PhotoItemViewModel> SelectedPhotos => [.. _selection.OrderBy(p => p.Index)];

    /// <summary>Completes when the current folder or search results have loaded. For tests.</summary>
    public Task PhotosLoading { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the tiles have their tags, dates and favourites from the index. For tests.</summary>
    public Task SummariesLoading { get; private set; } = Task.CompletedTask;

    public void OpenRoot(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path))
        {
            StatusText = $"Folder not found: {path}";
            return;
        }

        RootPath = path;
        RootFolders.Clear();
        var root = FolderNodeViewModel.CreateRoot(path, Library.Index);
        RootFolders.Add(root);
        root.IsExpanded = true;
        SelectedFolder = root;
        Library.StartScan(path);

        _settings.LastFolder = path;
        _settings.Save();
    }

    // --- Settings ------------------------------------------------------------------------

    /// <summary>Whether saving tags keeps each photo's "date modified". Saved in settings.json.</summary>
    public bool PreserveModifiedTime
    {
        get => _settings.PreserveModifiedTime;
        set
        {
            if (value == _settings.PreserveModifiedTime) return;
            _settings.PreserveModifiedTime = value;
            if (_writer is not null) _writer.PreserveModifiedTime = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool CanEditTags => _writer is not null;

    /// <summary>The status bar's Undo button, or Ctrl/⌘+Z in the grid: undoes the last bulk edit.</summary>
    [RelayCommand]
    private Task Undo() => Operations.UndoAsync(Photos);

    private async Task RefreshFolderCountsAsync()
    {
        foreach (var root in RootFolders.ToList()) await root.RefreshCountsAsync();
    }

    // --- Sort and group ------------------------------------------------------------------

    /// <summary>File name, date taken, or grouped by day. Saved in settings.json.</summary>
    public PhotoSort Sort
    {
        get => _settings.Sort;
        set
        {
            if (value == _settings.Sort) return;
            _settings.Sort = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SortIndex));
            OnPropertyChanged(nameof(IsGroupedByDay));
            Arrange();
        }
    }

    /// <summary><see cref="Sort"/> for the sort menu.</summary>
    public int SortIndex
    {
        get => (int)Sort;
        set => Sort = Enum.IsDefined((PhotoSort)value) ? (PhotoSort)value : PhotoSort.FileName;
    }

    public bool IsGroupedByDay => Sort == PhotoSort.Days;

    /// <summary>When grouped by day, show favourites at double size. Saved in settings.json.</summary>
    public bool HighlightFavourites
    {
        get => _settings.HighlightFavourites;
        set
        {
            if (value == _settings.HighlightFavourites) return;
            _settings.HighlightFavourites = value;
            _settings.Save();
            OnPropertyChanged();
            UpdateFeatured();
        }
    }

    /// <summary>Orders the loaded photos by the current sort, and adds day headings if grouped.</summary>
    private void Arrange()
    {
        IReadOnlyList<PhotoItemViewModel> ordered = Sort == PhotoSort.FileName
            ? _loaded
            : [.. _loaded.OrderBy(p => p.DateTaken is null).ThenBy(p => p.DateTaken)]; // stable: ties keep file order

        var items = new List<object>(ordered.Count);
        var days = new List<DayHeaderViewModel>();
        if (Sort == PhotoSort.Days)
        {
            foreach (var day in ordered.GroupBy(p => p.DateTaken is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null))
            {
                var header = new DayHeaderViewModel(day.Key, [.. day], items.Count);
                days.Add(header);
                items.Add(header);
                items.AddRange(day);
            }
        }
        else
        {
            items.AddRange(ordered);
        }

        for (var i = 0; i < ordered.Count; i++) ordered[i].Index = i;
        for (var i = 0; i < items.Count; i++)
            if (items[i] is PhotoItemViewModel photo) photo.GridIndex = i;

        _anchorIndex = CurrentPhoto?.Index ?? -1;
        Photos = ordered;
        Days = days;
        DateTree = DateNodeViewModel.Build(days);
        GridItems.Reset(items);
        UpdateFeatured(notify: false);
    }

    /// <summary>Favourites are shown at double size when grouped by day with favourites highlighted.</summary>
    private void UpdateFeatured(bool notify = true)
    {
        var highlight = Sort == PhotoSort.Days && HighlightFavourites;
        var changed = false;
        foreach (var photo in Photos)
        {
            var featured = highlight && photo.IsFavourite;
            if (photo.IsFeatured == featured) continue;
            photo.IsFeatured = featured;
            changed = true;
        }
        if (changed && notify) GridLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhotoItemViewModel.IsFavourite)) return;
        UpdateFeatured();
        foreach (var day in Days) day.Refresh();
        foreach (var node in DateTree) node.Refresh();
        UpdateHeading();
    }

    // --- Heading, breadcrumb and library counts ------------------------------------------

    /// <summary>Above the grid: the folder's name, or "Favourites", "Untagged", "All photos", or the search.</summary>
    [ObservableProperty] public partial string? Heading { get; private set; }

    /// <summary>Under the heading: "14 of 21 tagged · 5 favourites".</summary>
    [ObservableProperty] public partial string? Subheading { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<BreadcrumbItem> Breadcrumb { get; private set; } = [];

    /// <summary>Counts beside "All photos", "Favourites" and "Untagged", for the whole library.</summary>
    [ObservableProperty] public partial string? AllCount { get; private set; }

    [ObservableProperty] public partial string? FavouriteCount { get; private set; }
    [ObservableProperty] public partial string? UntaggedCount { get; private set; }

    private void UpdateHeading()
    {
        if (RootPath is null)
        {
            Heading = null;
            Subheading = null;
            SetBreadcrumb([]);
            return;
        }

        var rootName = RootFolders.FirstOrDefault()?.Name ?? Path.GetFileName(RootPath);
        var tagged = Photos.Count(p => p.Keywords.Count > 0);
        var favourites = Photos.Count(p => p.IsFavourite);
        var favouriteText = favourites == 1 ? "1 favourite" : $"{favourites:N0} favourites";

        if (IsSearching)
        {
            var terms = PhotoMetadataWriter.NormalizeKeywords((SearchText ?? "").Split(','));
            Heading = (ShowAllPhotos, ShowFavourites, ShowUntagged, terms.Count > 0) switch
            {
                (_, _, _, true) => string.Join(", ", terms),
                (_, true, true, _) => "Untagged favourites",
                (_, true, _, _) => "Favourites",
                (_, _, true, _) => "Untagged",
                _ => "All photos",
            };
            var photos = Photos.Count == 1 ? "1 photo" : $"{Photos.Count:N0} photos";
            Subheading = ShowUntagged || (ShowFavourites && terms.Count == 0)
                ? $"{photos} in {rootName}"
                : $"{photos} in {rootName} · {favouriteText}";
            SetBreadcrumb([new BreadcrumbItem(rootName, RootPath, false), new BreadcrumbItem(Heading, null, true)]);
            return;
        }

        if (SelectedFolder is not { IsPlaceholder: false } folder) return;
        Heading = folder.Name;
        Subheading = Photos.Count == 0 ? "No photos in this folder" : $"{tagged:N0} of {Photos.Count:N0} tagged · {favouriteText}";

        // Root, then each folder down to this one.
        var crumbs = new List<BreadcrumbItem>();
        var relative = Path.GetRelativePath(RootPath, folder.Path);
        crumbs.Add(new BreadcrumbItem(rootName, RootPath, relative == "."));
        if (relative != ".")
        {
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = RootPath;
            for (var i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
                crumbs.Add(new BreadcrumbItem(parts[i], path, i == parts.Length - 1));
            }
        }
        SetBreadcrumb(crumbs);
    }

    /// <summary>
    /// Replaces the breadcrumb only when it changes. The heading is recomputed whenever counts refresh
    /// (after a scan or an edit), and rebuilding identical buttons could swallow a click on one.
    /// </summary>
    private void SetBreadcrumb(IReadOnlyList<BreadcrumbItem> crumbs)
    {
        if (!crumbs.SequenceEqual(Breadcrumb)) Breadcrumb = crumbs;
    }

    /// <summary>A breadcrumb click: shows that folder.</summary>
    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (path is null || FindFolder(path) is not { } node) return;
        SelectedFolder = node;
    }

    /// <summary>Finds a loaded folder in the tree, expanding the way to it.</summary>
    private FolderNodeViewModel? FindFolder(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        foreach (var root in RootFolders)
        {
            var node = root;
            while (IsSameOrUnder(path, node.Path))
            {
                if (IsSameOrUnder(node.Path, path)) return node;
                var next = node.Children.FirstOrDefault(c => !c.IsPlaceholder && IsSameOrUnder(path, c.Path));
                if (next is null) break;
                node.IsExpanded = true;
                node = next;
            }
        }
        return null;

        static bool IsSameOrUnder(string path, string folder)
        {
            folder = Path.TrimEndingDirectorySeparator(folder);
            path = Path.TrimEndingDirectorySeparator(path);
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gives the tiles their tags, dates and favourites from the index, in one query rather than a file
    /// read per tile (which matters on network shares), and updates the library counts. Runs again
    /// whenever the index changes: a scan finishing, an edit.
    /// </summary>
    private Task RefreshSummariesAsync() => SummariesLoading = RefreshSummariesCoreAsync();

    private async Task RefreshSummariesCoreAsync()
    {
        if (RootPath is not { } root) return;
        var version = ++_summariesVersion;
        var summaries = await Library.Index.GetSummariesAsync(root);
        if (version != _summariesVersion || root != RootPath) return;

        var hadDates = _loaded.Select(p => p.DateTaken).ToList();
        foreach (var photo in _loaded) photo.Apply(summaries.GetValueOrDefault(photo.Path));

        // Dates arriving (the first scan of a folder) change the order when sorted by date.
        if (Sort != PhotoSort.FileName && !hadDates.SequenceEqual(_loaded.Select(p => p.DateTaken))) Arrange();
        else UpdateFeatured();
        foreach (var day in Days) day.Refresh();
        foreach (var node in DateTree) node.Refresh();

        AllCount = $"{summaries.Count:N0}";
        FavouriteCount = $"{summaries.Values.Count(s => s.IsFavourite):N0}";
        UntaggedCount = $"{summaries.Values.Count(s => s.Keywords.Count == 0):N0}";
        _popularKeywords.Reset(summaries.Values
            .SelectMany(s => s.Keywords)
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Key));
        UpdateHeading();
    }

    // --- Favourites ----------------------------------------------------------------------

    /// <summary>A tile's ♥: favourites or unfavourites that photo, whether or not it's selected.</summary>
    public Task ToggleFavouriteAsync(PhotoItemViewModel photo)
    {
        if (Details is PhotoDetailsViewModel details && details.Photo == photo)
            return details.CanEdit ? details.ToggleFavouriteCommand.ExecuteAsync(null) : Task.CompletedTask;
        return Operations.SetFavouriteAsync([photo], !photo.IsFavourite);
    }

    /// <summary>F in the grid or viewer: favourites the selected photos (or unfavourites them, if they all are).</summary>
    public Task ToggleSelectedFavouritesAsync() => Details switch
    {
        PhotoDetailsViewModel details => ToggleFavouriteAsync(details.Photo),
        BulkDetailsViewModel { CanEdit: true } bulk => bulk.ToggleFavouriteCommand.ExecuteAsync(null),
        _ => Task.CompletedTask,
    };

    // --- Viewer --------------------------------------------------------------------------

    /// <summary>The large viewer, when it's open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewerDetails))]
    public partial ViewerViewModel? Viewer { get; private set; }

    /// <summary>The details beside the viewer: only while it's open, so its controls aren't built behind the grid.</summary>
    public ViewModelBase? ViewerDetails => Viewer is null ? null : Details;

    /// <summary>
    /// Opens the viewer at <paramref name="start"/> (or the current photo). With several photos selected
    /// it steps through just those; otherwise through everything in the grid.
    /// </summary>
    public void OpenViewer(PhotoItemViewModel? start = null)
    {
        var photos = SelectedCount > 1 ? SelectedPhotos : Photos;
        if (photos.Count == 0) return;
        start = start is not null && photos.Contains(start) ? start : CurrentPhoto is { } c && photos.Contains(c) ? c : photos[0];
        Viewer?.Dispose();
        Viewer = new ViewerViewModel(this, photos, start, _renderer, Heading ?? "");
    }

    [RelayCommand]
    public void CloseViewer()
    {
        Viewer?.Dispose();
        Viewer = null;
    }

    // --- Search --------------------------------------------------------------------------

    /// <summary>Comma-separated search terms; photos must match all of them (a whole tag, or part of the title, description or file name).</summary>
    [ObservableProperty] public partial string? SearchText { get; set; }

    /// <summary>Every photo in the library, from every folder.</summary>
    [ObservableProperty] public partial bool ShowAllPhotos { get; set; }

    /// <summary>Show photos with no tags at all, to find what still needs tagging.</summary>
    [ObservableProperty] public partial bool ShowUntagged { get; set; }

    /// <summary>Only show favourites; combines with the search or "Untagged".</summary>
    [ObservableProperty] public partial bool ShowFavourites { get; set; }

    [ObservableProperty] public partial bool IsSearching { get; private set; }

    // Text search and "Untagged" are alternatives: switching one on switches the other off. "All photos"
    // is the library without any filter, so any filter switches it off.
    private bool _changingFilters;

    /// <summary>Enter in the search box.</summary>
    [RelayCommand]
    private void Search()
    {
        _changingFilters = true;
        ShowUntagged = false;
        ShowAllPhotos = false;
        _changingFilters = false;
        RunSearch();
    }

    partial void OnShowAllPhotosChanged(bool value)
    {
        if (_changingFilters) return;
        if (value)
        {
            _changingFilters = true;
            SearchText = null;
            ShowUntagged = false;
            ShowFavourites = false;
            _changingFilters = false;
        }
        RunSearch();
    }

    partial void OnShowUntaggedChanged(bool value)
    {
        if (_changingFilters) return;
        if (value)
        {
            _changingFilters = true;
            SearchText = null;
            ShowAllPhotos = false;
            _changingFilters = false;
            RunSearch();
        }
        else if (IsSearching)
        {
            RunSearch(); // back to the folder view, unless "Favourites" is still on
        }
    }

    partial void OnShowFavouritesChanged(bool value)
    {
        if (_changingFilters) return;
        if (value)
        {
            _changingFilters = true;
            ShowAllPhotos = false;
            _changingFilters = false;
        }
        if (value || IsSearching) RunSearch();
    }

    private void RunSearch()
    {
        var terms = PhotoMetadataWriter.NormalizeKeywords((SearchText ?? "").Split(','));
        if (RootPath is null || (terms.Count == 0 && !ShowUntagged && !ShowFavourites && !ShowAllPhotos))
        {
            ClearSearch();
            return;
        }

        IsSearching = true;
        SelectedFolder = null; // the grid now shows results from the whole library, not one folder
        var root = RootPath;
        var query = new PhotoQuery { Terms = terms, UntaggedOnly = ShowUntagged, FavouritesOnly = ShowFavourites };
        var description = (ShowUntagged, ShowFavourites, terms.Count > 0) switch
        {
            (true, true, _) => "untagged favourites",
            (true, false, _) => "untagged photos",
            (false, true, true) => $"favourites matching {string.Join(" + ", terms)}",
            (false, true, false) => "favourites",
            (false, false, true) => $"photos matching {string.Join(" + ", terms)}",
            _ => "photos",
        };

        PhotosLoading = ShowPhotosAsync(async () =>
            {
                var paths = await Library.Index.SearchAsync(root, query);
                // The index may lag deletions; results need their RAW companions to be tagged as pairs.
                return await Task.Run(() => paths.Where(File.Exists).Select(PhotoFiles.WithCompanions).ToList());
            },
            count => count == 0 ? $"No {description}" : $"{count:N0} {description} in {Path.GetFileName(root)}");
    }

    /// <summary>Esc in the search box, or the clear button: back to the folder view.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        var wasSearching = IsSearching;
        IsSearching = false;
        _changingFilters = true;
        SearchText = null;
        ShowUntagged = false;
        ShowFavourites = false;
        ShowAllPhotos = false;
        _changingFilters = false;
        if (wasSearching && SelectedFolder is null && RootFolders.FirstOrDefault() is { } root) SelectedFolder = root;
    }

    // --- Selection -----------------------------------------------------------------------

    public void Select(PhotoItemViewModel photo, SelectionGesture gesture = SelectionGesture.Replace)
    {
        if (_anchorIndex < 0 || _anchorIndex >= Photos.Count) _anchorIndex = photo.Index;

        switch (gesture)
        {
            case SelectionGesture.Replace:
                ClearSelectionCore();
                SetSelected(photo, true);
                _anchorIndex = photo.Index;
                break;
            case SelectionGesture.Toggle:
                SetSelected(photo, !photo.IsSelected);
                _anchorIndex = photo.Index;
                break;
            case SelectionGesture.Range:
                ClearSelectionCore();
                SelectRange(_anchorIndex, photo.Index);
                break;
            case SelectionGesture.AddRange:
                SelectRange(_anchorIndex, photo.Index);
                break;
        }

        CurrentPhoto = photo;
        OnSelectionChanged();
    }

    public void SelectAll()
    {
        foreach (var photo in Photos) SetSelected(photo, true);
        OnSelectionChanged();
    }

    [RelayCommand]
    public void ClearSelection()
    {
        ClearSelectionCore();
        OnSelectionChanged();
    }

    /// <summary>Arrow keys: move by <paramref name="delta"/> photos; with Shift, extend the selection.</summary>
    public void MoveCurrent(int delta, bool extend)
    {
        if (Photos.Count == 0) return;

        var target = CurrentPhoto is null
            ? (delta >= 0 ? 0 : Photos.Count - 1)
            : Math.Clamp(CurrentPhoto.Index + delta, 0, Photos.Count - 1);
        Select(Photos[target], extend ? SelectionGesture.Range : SelectionGesture.Replace);
    }

    /// <summary>↑ and ↓: move to the tile above or below (the grid works out which, as tiles can be double size).</summary>
    public void MoveTo(PhotoItemViewModel photo, bool extend) =>
        Select(photo, extend ? SelectionGesture.Range : SelectionGesture.Replace);

    private void SelectRange(int from, int to)
    {
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) SetSelected(Photos[i], true);
    }

    private void SetSelected(PhotoItemViewModel photo, bool selected)
    {
        photo.IsSelected = selected;
        if (selected) _selection.Add(photo);
        else _selection.Remove(photo);
    }

    private void ClearSelectionCore()
    {
        foreach (var photo in _selection) photo.IsSelected = false;
        _selection.Clear();
    }

    private void OnSelectionChanged()
    {
        SelectedCount = _selection.Count;

        // Keep the single-photo panel if it's already showing this photo (e.g. a no-op click).
        if (_selection.Count == 1 && Details is PhotoDetailsViewModel single && _selection.Contains(single.Photo)) return;

        (Details as IDisposable)?.Dispose();
        switch (_selection.Count)
        {
            case 0:
                Details = null;
                break;
            case 1:
                var photo = _selection.First();
                var details = new PhotoDetailsViewModel(photo, _writer, _keywordSuggestions, _peopleSuggestions, _placeSuggestions,
                    Operations, _renderer, _popularKeywords, _placeLookup);
                details.Saved += (_, _) => _ = Library.PhotoChangedAsync(photo.Path);
                Details = details;
                _ = details.LoadAsync();
                break;
            default:
                var bulk = new BulkDetailsViewModel(SelectedPhotos, Operations, _keywordSuggestions, _peopleSuggestions, _placeSuggestions);
                Details = bulk;
                _ = bulk.LoadAsync();
                break;
        }
    }

    // --- Folders -------------------------------------------------------------------------

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        if (value is null) return; // search results are showing
        if (IsSearching)
        {
            IsSearching = false;
            _changingFilters = true;
            SearchText = null;
            ShowUntagged = false;
            ShowFavourites = false;
            ShowAllPhotos = false;
            _changingFilters = false;
        }
        if (value.IsPlaceholder) return;

        PhotosLoading = ShowPhotosAsync(
            () => Task.Run(() => PhotoFiles.EnumeratePhotos(value.Path)),
            count => count switch
            {
                0 => $"No photos in {value.Name}",
                1 => $"1 photo in {value.Name}",
                var n => $"{n:N0} photos in {value.Name}",
            });
    }

    /// <summary>Replaces the grid's photos with a folder's contents or search results.</summary>
    private async Task ShowPhotosAsync(Func<Task<IReadOnlyList<PhotoFile>>> load, Func<int, string> describe)
    {
        _photosLoad?.Cancel();
        _photosLoad?.Dispose();
        var cts = _photosLoad = new CancellationTokenSource();

        CloseViewer();
        ClearSelection();
        CurrentPhoto = null;
        _anchorIndex = -1;
        foreach (var photo in _loaded)
        {
            photo.PropertyChanged -= OnPhotoChanged;
            photo.Unload();
        }
        _loaded = [];
        Arrange();
        StatusText = "Loading…";

        try
        {
            var files = await load();
            if (cts.IsCancellationRequested) return;

            var photos = files.Select((f, i) => new PhotoItemViewModel(f, i, _thumbnails)).ToList();
            // Tags, dates and favourites before the first layout, so a date sort doesn't reshuffle on screen.
            if (RootPath is { } root)
            {
                var summaries = await Library.Index.GetSummariesAsync(root);
                if (cts.IsCancellationRequested) return;
                foreach (var photo in photos) photo.Apply(summaries.GetValueOrDefault(photo.Path));
            }
            foreach (var photo in photos) photo.PropertyChanged += OnPhotoChanged;
            _loaded = photos;
            Arrange();
            StatusText = describe(files.Count);
            await RefreshSummariesAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!cts.IsCancellationRequested) StatusText = $"Couldn't load photos: {e.Message}";
        }
    }

    public void Dispose()
    {
        _photosLoad?.Cancel();
        _photosLoad?.Dispose();
        Library.Cancel();
        Viewer?.Dispose();
        (Details as IDisposable)?.Dispose();
        foreach (var photo in _loaded) photo.Unload();
    }
}
