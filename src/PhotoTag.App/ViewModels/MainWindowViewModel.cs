using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private CancellationTokenSource? _folderLoad;
    private int _anchorIndex = -1;

    public MainWindowViewModel(ThumbnailCache thumbnails, AppSettings settings, PhotoMetadataWriter? writer)
    {
        _thumbnails = thumbnails;
        _settings = settings;
        _writer = writer;
        Operations = new BulkOperations(writer, _keywordSuggestions);
        Operations.Summary += (_, summary) => StatusText = summary;
    }

    public ObservableCollection<FolderNodeViewModel> RootFolders { get; } = [];
    public BulkOperations Operations { get; }

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
    /// A plain list, replaced wholesale per folder, so the grid gets one reset
    /// notification instead of one per photo.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<PhotoItemViewModel> Photos { get; private set; } = [];

    /// <summary>Selected photos in folder order.</summary>
    public IReadOnlyList<PhotoItemViewModel> SelectedPhotos => [.. _selection.OrderBy(p => p.Index)];

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
        var root = FolderNodeViewModel.CreateRoot(path);
        RootFolders.Add(root);
        root.IsExpanded = true;
        SelectedFolder = root;

        _settings.LastFolder = path;
        _settings.Save();
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
                var details = new PhotoDetailsViewModel(_selection.First(), _writer, _keywordSuggestions, Operations);
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

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value) => _ = LoadFolderAsync(value);

    private async Task LoadFolderAsync(FolderNodeViewModel? folder)
    {
        _folderLoad?.Cancel();
        _folderLoad?.Dispose();
        var cts = _folderLoad = new CancellationTokenSource();

        ClearSelection();
        CurrentPhoto = null;
        _anchorIndex = -1;
        foreach (var photo in Photos) photo.Release();
        Photos = [];

        if (folder is null || folder.IsPlaceholder) return;

        StatusText = $"Reading {folder.Name}…";
        try
        {
            var files = await Task.Run(() => PhotoFiles.EnumeratePhotos(folder.Path), cts.Token);
            if (cts.IsCancellationRequested) return;

            Photos = files.Select((f, i) => new PhotoItemViewModel(f, i, _thumbnails)).ToList();
            StatusText = files.Count switch
            {
                0 => $"No photos in {folder.Name}",
                1 => $"1 photo in {folder.Name}",
                var n => $"{n:N0} photos in {folder.Name}",
            };
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't read {folder.Name}: {e.Message}";
        }
    }

    public void Dispose()
    {
        _folderLoad?.Cancel();
        _folderLoad?.Dispose();
        (Details as IDisposable)?.Dispose();
        foreach (var photo in Photos) photo.Release();
    }
}
