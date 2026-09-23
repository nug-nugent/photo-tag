using System.Collections.Frozen;

namespace PhotoTag.Core;

/// <summary>
/// A photo as the user sees it: one file, or a RAW+JPEG pair shot together. <see cref="Path"/> is
/// the file that's shown (the JPEG, for a pair); <see cref="Companions"/> are the other files of
/// the same shot, which get the same tags.
/// </summary>
public sealed record PhotoFile(string Path, IReadOnlyList<string> Companions)
{
    public static PhotoFile Single(string path) => new(path, []);

    public IEnumerable<string> AllPaths => Companions.Prepend(Path);
}

/// <summary>
/// File-system discovery of folders and photos. Everything here is synchronous and
/// cheap-ish; callers should still run it off the UI thread for large or network folders.
/// </summary>
public static class PhotoFiles
{
    public static readonly FrozenSet<string> RasterExtensions =
        new[] { ".jpg", ".jpeg", ".png", ".webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Camera RAW formats. Previews come from the JPEG the camera embeds; tags go in an .xmp sidecar.</summary>
    public static readonly FrozenSet<string> RawExtensions =
        new[] { ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2", ".pef", ".srw", ".dng" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> Extensions =
        RasterExtensions.Concat(RawExtensions).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> JpegExtensions =
        new[] { ".jpg", ".jpeg" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Photos directly inside <paramref name="folder"/>, sorted by file name, with RAW+JPEG
    /// pairs merged into one <see cref="PhotoFile"/>.
    /// </summary>
    public static IReadOnlyList<PhotoFile> EnumeratePhotos(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var photos = Group(Directory.EnumerateFiles(folder, "*", Options).Where(IsSupported)).ToList();
        photos.Sort((a, b) => CompareFileNames(a.Path, b.Path));
        return photos;
    }

    /// <summary>Photos anywhere under <paramref name="root"/>, pairs merged, skipping hidden and inaccessible folders.</summary>
    public static IEnumerable<PhotoFile> EnumeratePhotosRecursive(string root)
    {
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = true,
        };
        return Directory.EnumerateFiles(root, "*", options)
            .Where(IsSupported)
            .GroupBy(Path.GetDirectoryName)
            .SelectMany(Group);
    }

    /// <summary>
    /// Merges files in one folder that share a name, where one is a JPEG and the others are RAW
    /// (IMG_0001.JPG + IMG_0001.CR2). Anything else stays on its own.
    /// </summary>
    internal static IEnumerable<PhotoFile> Group(IEnumerable<string> files)
    {
        foreach (var shot in files.GroupBy(f => Path.Combine(Path.GetDirectoryName(f) ?? "", Path.GetFileNameWithoutExtension(f)),
                     StringComparer.OrdinalIgnoreCase))
        {
            var members = shot.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var jpeg = members.FirstOrDefault(f => JpegExtensions.Contains(Path.GetExtension(f)));
            var raws = members.Where(IsRaw).ToList();

            if (jpeg is not null && raws.Count > 0)
            {
                yield return new PhotoFile(jpeg, raws);
                foreach (var other in members.Where(f => f != jpeg && !IsRaw(f))) yield return PhotoFile.Single(other);
            }
            else
            {
                foreach (var file in members) yield return PhotoFile.Single(file);
            }
        }
    }

    /// <summary>For a photo found some other way (e.g. a search result), finds its RAW companions.</summary>
    public static PhotoFile WithCompanions(string path)
    {
        if (!JpegExtensions.Contains(Path.GetExtension(path))) return PhotoFile.Single(path);
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return PhotoFile.Single(path);

        var pattern = Path.GetFileNameWithoutExtension(path) + ".*";
        var raws = Directory.EnumerateFiles(folder, pattern, Options).Where(IsRaw)
            .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PhotoFile(path, raws);
    }

    // --- Sidecars ------------------------------------------------------------------------------

    /// <summary>
    /// The .xmp sidecar that holds a RAW file's tags, if one exists. Reads both conventions:
    /// IMG_0001.xmp (Lightroom, Capture One) and IMG_0001.CR2.xmp (darktable, digiKam).
    /// </summary>
    public static string? FindSidecar(string rawPath) =>
        SidecarCandidates(rawPath).FirstOrDefault(File.Exists);

    /// <summary>Where PhotoTag writes a new sidecar: IMG_0001.CR2 → IMG_0001.xmp, as Lightroom does.</summary>
    public static string NewSidecarPath(string rawPath) => Path.ChangeExtension(rawPath, ".xmp");

    private static IEnumerable<string> SidecarCandidates(string rawPath)
    {
        yield return Path.ChangeExtension(rawPath, ".xmp");
        yield return Path.ChangeExtension(rawPath, ".XMP");
        yield return rawPath + ".xmp";
        yield return rawPath + ".XMP";
    }

    /// <summary>
    /// Size and modified time identifying this version of a photo's metadata. For a RAW file
    /// this includes its sidecar, so edits to the sidecar alone count as a change.
    /// </summary>
    public static (long Size, long Ticks) GetStamp(string path)
    {
        var info = new FileInfo(path);
        var (size, ticks) = (info.Length, info.LastWriteTimeUtc.Ticks);
        if (IsRaw(path) && FindSidecar(path) is { } sidecar)
        {
            var xmp = new FileInfo(sidecar);
            size += xmp.Length;
            ticks = Math.Max(ticks, xmp.LastWriteTimeUtc.Ticks);
        }
        return (size, ticks);
    }

    // --- Folders -------------------------------------------------------------------------------

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
