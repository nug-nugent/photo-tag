using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>A folder in the tree. Subfolders are discovered lazily, off the UI thread, on first expand.</summary>
public partial class FolderNodeViewModel : ViewModelBase
{
    private bool _childrenLoaded;

    private FolderNodeViewModel(string path, string name, bool hasChildren, bool isPlaceholder)
    {
        Path = path;
        Name = name;
        IsPlaceholder = isPlaceholder;
        if (hasChildren) Children.Add(CreatePlaceholder());
        else _childrenLoaded = true;
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public static FolderNodeViewModel CreateRoot(string path)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return new FolderNodeViewModel(path, string.IsNullOrEmpty(name) ? path : name, hasChildren: true, isPlaceholder: false);
    }

    private static FolderNodeViewModel CreatePlaceholder() => new("", "Loading…", false, isPlaceholder: true);

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded) _ = LoadChildrenAsync();
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
            Children.Add(new FolderNodeViewModel(path, System.IO.Path.GetFileName(path), hasChildren, isPlaceholder: false));
    }
}
