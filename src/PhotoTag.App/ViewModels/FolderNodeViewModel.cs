using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// A folder in the tree. Subfolders are discovered lazily, off the UI thread, on first expand.
/// Photo and tag counts (including subfolders) come from the library index.
/// </summary>
public partial class FolderNodeViewModel : ViewModelBase
{
    private readonly LibraryIndex? _index;
    private bool _childrenLoaded;

    private FolderNodeViewModel(string path, string name, bool hasChildren, bool isPlaceholder, LibraryIndex? index)
    {
        Path = path;
        Name = name;
        IsPlaceholder = isPlaceholder;
        _index = index;
        if (hasChildren) Children.Add(CreatePlaceholder());
        else _childrenLoaded = true;
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>e.g. "1,500 · 320 tagged", or null before the folder is indexed.</summary>
    [ObservableProperty] public partial string? CountText { get; private set; }

    [ObservableProperty] public partial FolderCounts Counts { get; private set; }

    /// <summary>Completes once subfolders have been loaded. For tests.</summary>
    public Task ChildrenLoading { get; private set; } = Task.CompletedTask;

    public static FolderNodeViewModel CreateRoot(string path, LibraryIndex? index = null)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return new FolderNodeViewModel(path, string.IsNullOrEmpty(name) ? path : name, hasChildren: true, isPlaceholder: false, index);
    }

    private static FolderNodeViewModel CreatePlaceholder() => new("", "Loading…", false, isPlaceholder: true, index: null);

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded) ChildrenLoading = LoadChildrenAsync();
    }

    /// <summary>Refreshes this folder's counts and those of every loaded subfolder.</summary>
    public async Task RefreshCountsAsync()
    {
        if (_index is null || IsPlaceholder) return;

        Counts = await _index.GetFolderCountsAsync(Path);
        CountText = Counts switch
        {
            { Photos: 0 } => null,
            { Tagged: 0 } c => $"{c.Photos:N0}",
            var c => $"{c.Photos:N0} · {c.Tagged:N0} tagged",
        };

        foreach (var child in Children.ToList()) await child.RefreshCountsAsync();
    }

    private async Task LoadChildrenAsync()
    {
        _childrenLoaded = true;

        List<(string Path, bool HasChildren)> folders;
        try
        {
            folders = await Task.Run(() => PhotoFiles.EnumerateSubfolders(Path)
                .Select(p => (p, PhotoFiles.HasSubfolders(p)))
                .ToList());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            folders = [];
        }

        Children.Clear();
        foreach (var (path, hasChildren) in folders)
            Children.Add(new FolderNodeViewModel(path, System.IO.Path.GetFileName(path), hasChildren, isPlaceholder: false, _index));

        foreach (var child in Children.ToList()) await child.RefreshCountsAsync();
    }
}
