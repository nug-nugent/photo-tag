namespace PhotoTag.Core.Tests;

public sealed class PhotoFilesTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void EnumeratePhotos_ReturnsOnlySupportedFiles_SortedByName()
    {
        foreach (var name in new[] { "b.JPG", "a.jpeg", "c.png", "notes.txt", "raw.cr2", "d.webp" })
            File.WriteAllText(System.IO.Path.Combine(_dir.Path, name), "");
        Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "sub.jpg")); // folder, not a photo

        var names = PhotoFiles.EnumeratePhotos(_dir.Path).Select(System.IO.Path.GetFileName);

        Assert.Equal(["a.jpeg", "b.JPG", "c.png", "d.webp"], names);
    }

    [Fact]
    public void EnumerateSubfolders_IsSortedAndNonRecursive()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "Zebra", "Nested"));
        Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "apple"));

        var names = PhotoFiles.EnumerateSubfolders(_dir.Path).Select(System.IO.Path.GetFileName);

        Assert.Equal(["apple", "Zebra"], names);
        Assert.True(PhotoFiles.HasSubfolders(_dir.Path));
        Assert.False(PhotoFiles.HasSubfolders(System.IO.Path.Combine(_dir.Path, "apple")));
    }

    [Fact]
    public void MissingFolder_ReturnsEmpty()
    {
        var missing = System.IO.Path.Combine(_dir.Path, "nope");
        Assert.Empty(PhotoFiles.EnumeratePhotos(missing));
        Assert.Empty(PhotoFiles.EnumerateSubfolders(missing));
        Assert.False(PhotoFiles.HasSubfolders(missing));
    }

    public void Dispose() => _dir.Dispose();
}
