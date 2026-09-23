using System.Collections.Frozen;

namespace PhotoTag.Core;

/// <summary>
/// File-system discovery of folders and photos. Everything here is synchronous and
/// cheap-ish; callers should still run it off the UI thread for large or network folders.
/// </summary>
public static class PhotoFiles
{
    public static readonly FrozenSet<string> Extensions =
        new[] { ".jpg", ".jpeg", ".png", ".webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Photos directly inside <paramref name="folder"/>, sorted by file name.</summary>
    public static IReadOnlyList<string> EnumeratePhotos(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        var photos = Directory.EnumerateFiles(folder, "*", Options)
            .Where(IsSupported)
            .ToList();
        photos.Sort(CompareFileNames);
        return photos;
    }

    /// <summary>Photos anywhere under <paramref name="root"/>, skipping hidden and inaccessible folders.</summary>
    public static IEnumerable<string> EnumeratePhotosRecursive(string root)
    {
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = true,
        };
        return Directory.EnumerateFiles(root, "*", options).Where(IsSupported);
    }

    /// <summary>Immediate subfolders of <paramref name="folder"/>, sorted by name.</summary>
    public static IReadOnlyList<string> EnumerateSubfolders(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        var folders = Directory.EnumerateDirectories(folder, "*", Options).ToList();
        folders.Sort(CompareFileNames);
        return folders;
    }

    public static bool HasSubfolders(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder, "*", Options).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int CompareFileNames(string a, string b) =>
        StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b));
}
