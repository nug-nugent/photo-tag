namespace PhotoTag.Core.Tests;

public sealed class PhotoFilesTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void EnumeratePhotos_ReturnsOnlySupportedFiles_SortedByName()
    {
        foreach (var name in new[] { "b.JPG", "a.jpeg", "c.png", "notes.txt", "e.cr2", "clip.mov", "d.webp", "e.xmp" })
            File.WriteAllText(System.IO.Path.Combine(_dir.Path, name), "");
        Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "sub.jpg")); // folder, not a photo

        var names = PhotoFiles.EnumeratePhotos(_dir.Path).Select(p => System.IO.Path.GetFileName(p.Path));

        Assert.Equal(["a.jpeg", "b.JPG", "c.png", "d.webp", "e.cr2"], names);
    }

    [Fact]
    public void RawAndJpegOfTheSameShot_AreOnePhoto()
    {
        foreach (var name in new[] { "a.jpg", "a.CR2", "b.nef", "c.JPG", "c.cr3", "c.dng", "d.png", "d.arw", "e.jpg", "e.jpeg.txt" })
            File.WriteAllText(System.IO.Path.Combine(_dir.Path, name), "");

        var photos = PhotoFiles.EnumeratePhotos(_dir.Path)
            .Select(p => (System.IO.Path.GetFileName(p.Path), string.Join(",", p.Companions.Select(System.IO.Path.GetFileName))));

        Assert.Equal(
            [
                ("a.jpg", "a.CR2"),   // RAW+JPEG pair: shown as the JPEG
                ("b.nef", ""),        // RAW on its own
                ("c.JPG", "c.cr3,c.dng"),
                ("d.arw", ""),        // only JPEGs pair with RAWs, not PNGs
                ("d.png", ""),
                ("e.jpg", ""),
            ],
            photos);
    }

    [Fact]
    public void WithCompanions_FindsTheRawOfASearchResult()
    {
        foreach (var name in new[] { "a.jpg", "a.RAF", "b.jpg" })
            File.WriteAllText(System.IO.Path.Combine(_dir.Path, name), "");

        Assert.Equal(["a.RAF"], PhotoFiles.WithCompanions(System.IO.Path.Combine(_dir.Path, "a.jpg")).Companions.Select(System.IO.Path.GetFileName));
        Assert.Empty(PhotoFiles.WithCompanions(System.IO.Path.Combine(_dir.Path, "b.jpg")).Companions);
    }

    [Fact]
    public void Sidecars_AreFoundInEitherNamingStyle()
    {
        var raw = System.IO.Path.Combine(_dir.Path, "IMG_0001.CR2");
        File.WriteAllText(raw, "");
        Assert.Null(PhotoFiles.FindSidecar(raw));
        Assert.Equal(System.IO.Path.Combine(_dir.Path, "IMG_0001.xmp"), PhotoFiles.NewSidecarPath(raw));

        File.WriteAllText(raw + ".xmp", ""); // darktable / digiKam style
        Assert.Equal(raw + ".xmp", PhotoFiles.FindSidecar(raw));

        File.WriteAllText(System.IO.Path.Combine(_dir.Path, "IMG_0001.xmp"), ""); // Lightroom style wins
        Assert.Equal("IMG_0001.xmp", System.IO.Path.GetFileName(PhotoFiles.FindSidecar(raw)), ignoreCase: true);
    }

    [Fact]
    public void Stamp_OfARaw_ChangesWhenOnlyItsSidecarChanges()
    {
        var raw = System.IO.Path.Combine(_dir.Path, "IMG.nef");
        File.WriteAllText(raw, "raw");
        var before = PhotoFiles.GetStamp(raw);

        File.WriteAllText(System.IO.Path.Combine(_dir.Path, "IMG.xmp"), "<x/>");

        Assert.NotEqual(before, PhotoFiles.GetStamp(raw));
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
