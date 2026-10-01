using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
        Settings = new SettingsViewModel(settings, writer, thumbnails, Updates);
        Library = new LibraryViewModel(index, _keywordSuggestions, _peopleSuggestions, _placeSuggestions);
        Library.CountsChanged += (_, _) => _ = RefreshFolderCountsAsync();
        Library.CountsChanged += (_, _) => _ = RefreshSummariesAsync();
        Operations = new BulkOperations(writer, _keywordSuggestions, _peopleSuggestions);
        Operations.Summary += (_, summary) => StatusText = summary;
        Operations.Completed += (_, result) => _ = Library.PhotosChangedAsync(result.After);
        Operations.PhotoSaved += (_, _) =>
        {
            // "14 of 21 tagged" follows a bulk edit as it goes, a few times a second at most.
            if (_sinceHeading.ElapsedMilliseconds < 250) return;
            _sinceHeading.Restart();
            UpdateHeading();
        };
        TagManager = new TagManagerViewModel(Library, Operations, () => RootPath, () => Photos);

        Library.FilesChanged += (_, changes) => _ = OnFilesChangedAsync(changes);
        Library.IsWriting = () => Operations.IsBusy || Details is PhotoDetailsViewModel { IsSaving: true };
        Library.FoldersToPoll = () => ShownFolder is { } folder ? [folder] : [];
    }

    public ObservableCollection<FolderNodeViewModel> RootFolders { get; } = [];
    public BulkOperations Operations { get; }
    public LibraryViewModel Library { get; }
    public TagManagerViewModel TagManager { get; }
    public UpdatesViewModel Updates { get; }

    /// <summary>For the settings window.</summary>
    public SettingsViewModel Settings { get; }

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

    /// <summary>PhotoTag's log file, for "Show log" in settings; null when there isn't one (tests, tools).</summary>
    public string? LogPath
    {
        get => Settings.LogPath;
        init => Settings.LogPath = value;
    }

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

    /// <summary>How long a folder can take to answer before the status bar says PhotoTag is waiting for it.</summary>
    private static readonly TimeSpan SlowFolder = TimeSpan.FromSeconds(1);

    private int _openVersion;
    private readonly Stopwatch _sinceHeading = Stopwatch.StartNew();

    /// <summary>
    /// Opens a folder tree. Checking it's there happens off the UI thread: a NAS that's asleep can take
    /// many seconds to answer, and the window shouldn't freeze meanwhile.
    /// </summary>
    public async Task OpenRootAsync(string path)
    {
        path = Path.GetFullPath(path);
        var version = ++_openVersion;
        var started = Stopwatch.StartNew();
        var check = Task.Run(() => (Exists: Directory.Exists(path), OnNetwork: PhotoFiles.IsOnNetworkDrive(path)));
        if (await Task.WhenAny(check, Task.Delay(SlowFolder)) != check) StatusText = $"Waiting for {path}…";
        var (exists, onNetwork) = await check;
        Log.Info($"Opening {path}" + (onNetwork ? " (network drive)" : "") +
                 (started.Elapsed >= SlowFolder ? $", which took {started.Elapsed.TotalSeconds:0.0} s to answer" : ""));
        if (version != _openVersion) return; // another folder was opened meanwhile
        if (!exists)
        {
            Log.Warn($"Can't reach {path}");
            StatusText = onNetwork
                ? $"Can't reach {path}. If it's on a NAS or another computer, check it's switched on and connected."
                : $"Folder not found: {path}";
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

    /// <summary>
    /// A folder shows the photos in its subfolders too, so a year folder shows the whole year. Saved in
    /// settings.json. A folder with no photos of its own shows its subfolders' photos either way.
    /// </summary>
    public bool IncludeSubfolders
    {
        get => _settings.IncludeSubfolders;
        set
        {
            if (value == _settings.IncludeSubfolders) return;
            _settings.IncludeSubfolders = value;
            _settings.Save();
            OnPropertyChanged();
            if (!IsSearching && SelectedFolder is { IsPlaceholder: false } folder) ShowFolder(folder);
        }
    }

    /// <summary>Whether the grid has the photos of the folder's subfolders as well as its own.</summary>
    [ObservableProperty] public partial bool ShowsSubfolders { get; private set; }

    /// <summary>Orders the loaded photos by the current sort, and adds day headings if grouped.</summary>
    private void Arrange()
    {
        IReadOnlyList<PhotoItemViewModel> ordered = Sort == PhotoSort.FileName
            ? _loaded
            : [.. _loaded.OrderBy(p => p.DateTaken is null).ThenByDescending(p => p.DateTaken)]; // newest first; stable: ties keep file order

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
        Subheading = Photos.Count == 0
            ? ShowsSubfolders ? "No photos in this folder or its subfolders" : "No photos in this folder"
            : $"{tagged:N0} of {Photos.Count:N0} tagged · {favouriteText}" + FromFolders(folder.Path);

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

    /// <summary>" · from 12 folders" when the grid has photos from the folder's subfolders, else nothing.</summary>
    private string FromFolders(string shown)
    {
        if (!ShowsSubfolders) return "";
        var folders = Photos.Select(p => Path.GetDirectoryName(p.Path) ?? "").Distinct(PathComparer).ToList();
        return folders switch
        {
            [var only] when PathComparer.Equals(only, shown) => "",
            [_] => " · from 1 subfolder",
            _ => $" · from {folders.Count:N0} folders",
        };
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
    }

    private static bool IsSameOrUnder(string path, string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        path = Path.TrimEndingDirectorySeparator(path);
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
        IReadOnlyDictionary<string, PhotoSummary> summaries;
        while (true)
        {
            // PhotoTag's own edits show on the tiles straight away (a ♥), before the index has them. An answer from
            // the index while one is being written or recorded could put back what was there before, so wait for
            // them, and ask again if one started meanwhile. (One that finished meanwhile started a newer refresh.)
            while (Library.IsWriting() || Library.IsRecording) await Task.Delay(100);
            if (version != _summariesVersion || root != RootPath) return;
            summaries = await Library.Index.GetSummariesAsync(root);
            if (version != _summariesVersion || root != RootPath) return;
            if (!Library.IsWriting() && !Library.IsRecording) break;
        }

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
                return (await PhotoFiles.FindAsync(paths), false);
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
        ShowFolder(value);
    }

    private void ShowFolder(FolderNodeViewModel folder)
    {
        PhotosLoading = ShowPhotosAsync(
            () => LoadFolderAsync(folder.Path),
            count => (count == 1 ? "1 photo" : $"{count:N0} photos") + $" in {folder.Name}" + (ShowsSubfolders ? " and its subfolders" : ""));
    }

    /// <summary>A folder's photos, and its subfolders' too if <see cref="IncludeSubfolders"/> is on or it has none of its own.</summary>
    private async Task<(IReadOnlyList<PhotoFile> Files, bool WithSubfolders)> LoadFolderAsync(string folder)
    {
        if (!IncludeSubfolders)
        {
            var own = await Task.Run(() => PhotoFiles.EnumeratePhotos(folder));
            if (own.Count > 0) return (own, false);
        }
        return (await PhotoFiles.EnumeratePhotosUnderAsync(folder), true);
    }

    /// <summary>Replaces the grid's photos with a folder's contents or search results.</summary>
    private async Task ShowPhotosAsync(Func<Task<(IReadOnlyList<PhotoFile> Files, bool WithSubfolders)>> load, Func<int, string> describe)
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
            var (files, withSubfolders) = await load();
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
            ShowsSubfolders = withSubfolders;
            Arrange();
            StatusText = describe(files.Count);
            await RefreshSummariesAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!cts.IsCancellationRequested) StatusText = $"Couldn't load photos: {e.Message}";
        }
    }

    // --- Changes made outside PhotoTag ----------------------------------------------------

    /// <summary>The folder whose photos the grid shows, or null while searching.</summary>
    private string? ShownFolder => !IsSearching && SelectedFolder is { IsPlaceholder: false } folder ? folder.Path : null;

    /// <summary>
    /// The window was activated: look for changes in the folder on screen, in case another app changed
    /// it and a notification went missing (they can, on network shares). Cheap: one folder listing.
    /// </summary>
    public void CheckShownFolder()
    {
        if (ShownFolder is { } folder) Library.RefreshFolders([folder]);
    }

    /// <summary>Completes when the grid and panels have caught up with the last outside change. For tests.</summary>
    public Task FilesChangedHandling { get; private set; } = Task.CompletedTask;

    /// <summary>One batch at a time, in order.</summary>
    private Task OnFilesChangedAsync(LibraryChanges changes)
    {
        var previous = FilesChangedHandling;
        return FilesChangedHandling = HandleAfterAsync();

        async Task HandleAfterAsync()
        {
            await previous;
            await HandleFilesChangedAsync(changes);
        }
    }

    private async Task HandleFilesChangedAsync(LibraryChanges changes)
    {
        // A folder still loading would be merged into as if empty, then replaced: let it finish.
        await PhotosLoading;
        if (RootPath is not { } root) return;
        var shown = ShownFolder;
        // Off the UI thread: these ask the disk, which on a sleeping share can take a while.
        var (rootExists, nearest) = await Task.Run(() =>
        {
            var folder = shown;
            while (folder is not null && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);
            return (Directory.Exists(root), folder);
        });
        if (RootPath != root) return;
        if (!rootExists)
        {
            StatusText = $"Can't reach {root} any more. If it's on a NAS or another computer, check it's switched on and connected.";
            return;
        }

        // The folder on screen was deleted or renamed: show the nearest one that's still there. Before
        // the tree drops its node, which would leave nothing selected.
        if (shown is not null && nearest != shown)
        {
            SelectedFolder = (nearest is null ? null : FindFolder(nearest)) ?? RootFolders.FirstOrDefault();
            shown = null; // selecting it loads its photos
        }

        foreach (var node in RootFolders.ToList()) await node.RefreshChildrenAsync(changes.Everything ? null : changes.Folders);

        if (shown is not null)
        {
            // With subfolders on screen, a change in any of them; otherwise one in the folder itself.
            bool Affects(string folder) => ShowsSubfolders ? IsSameOrUnder(folder, shown) : PathComparer.Equals(folder, shown);
            if (changes.Everything || changes.Folders.Any(Affects)
                || changes.Added.Concat(changes.Removed).Any(p => Path.GetDirectoryName(p) is { } folder && Affects(folder)))
            {
                var (files, withSubfolders) = await LoadFolderAsync(shown);
                if (ShownFolder != shown) return;
                ShowsSubfolders = withSubfolders; // e.g. a folder that had none of its own now has a photo
                await ReconcileAsync(files);
            }
        }
        else if (IsSearching && _loaded.Any(p => changes.Removed.Contains(p.Path)))
        {
            await ReconcileAsync([.. _loaded.Where(p => !changes.Removed.Contains(p.Path)).Select(p => p.File)]);
        }

        // Only photos changed since they were indexed: one indexed for the first time just now hasn't changed
        // since PhotoTag read it for its tile or panel.
        var changed = _loaded.Where(p => changes.Changed.Contains(p.Path)).ToList();
        foreach (var photo in changed) photo.FileChanged();
        switch (Details)
        {
            case PhotoDetailsViewModel details when changed.Contains(details.Photo):
                await details.FileChangedAsync();
                break;
            case BulkDetailsViewModel bulk when changed.Any(bulk.Photos.Contains):
                await bulk.LoadAsync(); // re-reads the ones that changed
                break;
        }
    }

    /// <summary>
    /// Updates the grid to <paramref name="files"/> without starting again: photos still there keep their
    /// tiles, thumbnails and selection; new ones are added and missing ones dropped.
    /// </summary>
    private async Task ReconcileAsync(IReadOnlyList<PhotoFile> files)
    {
        var existing = _loaded.ToDictionary(p => p.Path, PathComparer);
        var kept = new HashSet<PhotoItemViewModel>();
        var photos = new List<PhotoItemViewModel>(files.Count);
        var added = new List<PhotoItemViewModel>();
        foreach (var file in files)
        {
            if (existing.TryGetValue(file.Path, out var photo) && photo.File.Companions.SequenceEqual(file.Companions, PathComparer))
            {
                kept.Add(photo);
                photos.Add(photo);
            }
            else
            {
                var fresh = new PhotoItemViewModel(file, photos.Count, _thumbnails);
                added.Add(fresh);
                photos.Add(fresh);
            }
        }
        var gone = _loaded.Where(p => !kept.Contains(p)).ToList();
        if (gone.Count == 0 && added.Count == 0) return;

        if (added.Count > 0 && RootPath is { } root)
        {
            var summaries = await Library.Index.GetSummariesAsync(root);
            foreach (var photo in added) photo.Apply(summaries.GetValueOrDefault(photo.Path));
        }
        foreach (var photo in added) photo.PropertyChanged += OnPhotoChanged;

        var selectionChanged = false;
        foreach (var photo in gone)
        {
            photo.PropertyChanged -= OnPhotoChanged;
            photo.Unload();
            if (photo.IsSelected)
            {
                SetSelected(photo, false);
                selectionChanged = true;
            }
            if (CurrentPhoto == photo) CurrentPhoto = null;
        }
        if (Viewer is { } viewer && gone.Any(viewer.Photos.Contains)) CloseViewer();

        _loaded = photos;
        Arrange();
        if (selectionChanged) OnSelectionChanged();
        UpdateHeading();
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public void Dispose()
    {
        _photosLoad?.Cancel();
        _photosLoad?.Dispose();
        Library.Dispose();
        Viewer?.Dispose();
        (Details as IDisposable)?.Dispose();
        foreach (var photo in _loaded) photo.Unload();
    }
}
