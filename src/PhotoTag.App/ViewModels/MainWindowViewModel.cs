using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

public partial class MainWindowViewModel(ThumbnailCache thumbnails, AppSettings settings, PhotoMetadataWriter? writer)
    : ViewModelBase, IDisposable
{
    private readonly KeywordSuggestions _keywordSuggestions = new();
    private CancellationTokenSource? _folderLoad;

    public ObservableCollection<FolderNodeViewModel> RootFolders { get; } = [];

    [ObservableProperty] public partial string? RootPath { get; private set; }
    [ObservableProperty] public partial FolderNodeViewModel? SelectedFolder { get; set; }
    [ObservableProperty] public partial PhotoItemViewModel? SelectedPhoto { get; set; }
    [ObservableProperty] public partial PhotoDetailsViewModel? Details { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "Open a folder to get started.";

    /// <summary>
    /// A plain list, replaced wholesale per folder, so the grid gets one reset
    /// notification instead of one per photo.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<PhotoItemViewModel> Photos { get; private set; } = [];

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

        settings.LastFolder = path;
        settings.Save();
    }

    [RelayCommand]
    private void SelectPhoto(PhotoItemViewModel photo) => SelectedPhoto = photo;

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value) => _ = LoadFolderAsync(value);

    partial void OnSelectedPhotoChanged(PhotoItemViewModel? oldValue, PhotoItemViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        newValue?.IsSelected = true;

        Details?.Dispose();
        Details = newValue is null ? null : new PhotoDetailsViewModel(newValue, writer, _keywordSuggestions);
        if (Details is not null) _ = Details.LoadAsync();
    }

    private async Task LoadFolderAsync(FolderNodeViewModel? folder)
    {
        _folderLoad?.Cancel();
        _folderLoad?.Dispose();
        var cts = _folderLoad = new CancellationTokenSource();

        SelectedPhoto = null;
        foreach (var photo in Photos) photo.Release();
        Photos = [];

        if (folder is null || folder.IsPlaceholder) return;

        StatusText = $"Reading {folder.Name}…";
        try
        {
            var files = await Task.Run(() => PhotoFiles.EnumeratePhotos(folder.Path), cts.Token);
            if (cts.IsCancellationRequested) return;

            Photos = files.Select(f => new PhotoItemViewModel(f, thumbnails)).ToList();
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
        Details?.Dispose();
        foreach (var photo in Photos) photo.Release();
    }
}
