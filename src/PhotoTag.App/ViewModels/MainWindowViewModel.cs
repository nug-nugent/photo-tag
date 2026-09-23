using System.Collections.ObjectModel;
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

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ThumbnailCache _thumbnails;
    private readonly AppSettings _settings;
    private readonly PhotoMetadataWriter? _writer;
    private readonly KeywordSuggestions _keywordSuggestions = new();
    private readonly HashSet<PhotoItemViewModel> _selection = [];
    private CancellationTokenSource? _photosLoad;
    private int _anchorIndex = -1;

    public MainWindowViewModel(ThumbnailCache thumbnails, AppSettings settings, PhotoMetadataWriter? writer, LibraryIndex index)
    {
        _thumbnails = thumbnails;
        _settings = settings;
        _writer = writer;
        if (writer is not null) writer.PreserveModifiedTime = settings.PreserveModifiedTime;
        Library = new LibraryViewModel(index, _keywordSuggestions);
        Library.CountsChanged += (_, _) => _ = RefreshFolderCountsAsync();
        Operations = new BulkOperations(writer, _keywordSuggestions);
        Operations.Summary += (_, summary) => StatusText = summary;
        Operations.Completed += (_, result) => _ = Library.PhotosChangedAsync(result.After);
    }

    public ObservableCollection<FolderNodeViewModel> RootFolders { get; } = [];
    public BulkOperations Operations { get; }
    public LibraryViewModel Library { get; }
    public ObservableCollection<string> KeywordSuggestions => _keywordSuggestions.Items;

    [ObservableProperty] public partial string? RootPath { get; private set; }
    [ObservableProperty] public partial FolderNodeViewModel? SelectedFolder { get; set; }

    /// <summary>The photo last clicked or moved to with the keyboard.</summary>
    [ObservableProperty] public partial PhotoItemViewModel? CurrentPhoto { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    public partial int SelectedCount { get; private set; }

    public string? SelectionText => SelectedCount > 1 ? $"{SelectedCount:N0} selected" : null;

    /// <summary>A <see cref="PhotoDetailsViewModel"/> for one photo, a <see cref="BulkDetailsViewModel"/> for several.</summary>
    [ObservableProperty] public partial ViewModelBase? Details { get; private set; }

    [ObservableProperty] public partial string StatusText { get; private set; } = "Open a folder to get started.";

    /// <summary>
    /// A plain list, replaced wholesale per folder or search, so the grid gets one reset
    /// notification instead of one per photo.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<PhotoItemViewModel> Photos { get; private set; } = [];

    /// <summary>Selected photos in folder order.</summary>
    public IReadOnlyList<PhotoItemViewModel> SelectedPhotos => [.. _selection.OrderBy(p => p.Index)];

    /// <summary>Completes when the current folder or search results have loaded. For tests.</summary>
    public Task PhotosLoading { get; private set; } = Task.CompletedTask;

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

    private async Task RefreshFolderCountsAsync()
    {
        foreach (var root in RootFolders.ToList()) await root.RefreshCountsAsync();
    }

    // --- Search --------------------------------------------------------------------------

    /// <summary>Tags to search for, comma-separated; photos must have all of them.</summary>
    [ObservableProperty] public partial string? SearchText { get; set; }

    /// <summary>Show photos with no tags at all, to find what still needs tagging.</summary>
    [ObservableProperty] public partial bool ShowUntagged { get; set; }

    [ObservableProperty] public partial bool IsSearching { get; private set; }

    // Tag search and "Untagged" are alternatives: switching one on switches the other off.
    private bool _changingFilters;

    /// <summary>Enter in the search box.</summary>
    [RelayCommand]
    private void Search()
    {
        _changingFilters = true;
        ShowUntagged = false;
        _changingFilters = false;
        RunSearch();
    }

    partial void OnShowUntaggedChanged(bool value)
    {
        if (_changingFilters) return;
        if (value)
        {
            SearchText = null;
            RunSearch();
        }
        else if (IsSearching)
        {
            ClearSearch();
        }
    }

    private void RunSearch()
    {
        var keywords = PhotoMetadataWriter.NormalizeKeywords((SearchText ?? "").Split(','));
        if (RootPath is null || (keywords.Count == 0 && !ShowUntagged))
        {
            ClearSearch();
            return;
        }

        IsSearching = true;
        SelectedFolder = null; // the grid now shows results from the whole library, not one folder
        var root = RootPath;
        var query = new PhotoQuery { Keywords = keywords, UntaggedOnly = ShowUntagged };
        var description = ShowUntagged ? "untagged photos" : $"photos tagged {string.Join(" + ", keywords)}";

        PhotosLoading = ShowPhotosAsync(async () =>
            {
                var paths = await Library.Index.SearchAsync(root, query);
                return await Task.Run(() => paths.Where(File.Exists).ToList()); // the index may lag deletions
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
                var details = new PhotoDetailsViewModel(photo, _writer, _keywordSuggestions, Operations);
                details.Saved += (_, _) => _ = Library.PhotoChangedAsync(photo.Path);
                Details = details;
                _ = details.LoadAsync();
                break;
            default:
                var bulk = new BulkDetailsViewModel(SelectedPhotos, Operations, _keywordSuggestions);
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
    private async Task ShowPhotosAsync(Func<Task<IReadOnlyList<string>>> load, Func<int, string> describe)
    {
        _photosLoad?.Cancel();
        _photosLoad?.Dispose();
        var cts = _photosLoad = new CancellationTokenSource();

        ClearSelection();
        CurrentPhoto = null;
        _anchorIndex = -1;
        foreach (var photo in Photos) photo.Release();
        Photos = [];
        StatusText = "Loading…";

        try
        {
            var files = await load();
            if (cts.IsCancellationRequested) return;

            Photos = files.Select((f, i) => new PhotoItemViewModel(f, i, _thumbnails)).ToList();
            StatusText = describe(files.Count);
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
        (Details as IDisposable)?.Dispose();
        foreach (var photo in Photos) photo.Release();
    }
}
