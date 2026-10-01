namespace PhotoTag.Core.Tests;

public sealed class PhotoFilesTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void APhotoBeingRead_CanStillBeRenamedAndDeleted()
    {
        // On Windows, PhotoTag reading a photo mustn't stop Explorer or another app moving or deleting it.
        var photo = TestImages.Write(_dir.Path, "a.jpg", TestImages.Jpeg(32, 32));
        var renamed = System.IO.Path.Combine(_dir.Path, "b.jpg");

        using (var reading = PhotoFiles.OpenRead(photo))
        {
            File.Move(photo, renamed);
            File.Delete(renamed);
            Assert.Equal(0xFF, reading.ReadByte()); // and the read carries on
        }
        Assert.False(File.Exists(photo));
        Assert.False(File.Exists(renamed));
    }

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
    public async Task FindAsync_GivesSearchResultsTheirRawsAndListing_DroppingMissingOnes()
    {
        var sub = Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "sub")).FullName;
        foreach (var name in new[] { "a.jpg", "a.RAF", "b.jpg", "c.nef", "c.jpg" })
            File.WriteAllText(System.IO.Path.Combine(_dir.Path, name), "");
        File.WriteAllText(System.IO.Path.Combine(sub, "d.png"), "four");
        string P(string name) => System.IO.Path.Combine(_dir.Path, name);

        var found = await PhotoFiles.FindAsync(
            [
                System.IO.Path.Combine(sub, "d.png"),
                P("b.jpg"),
                P("gone.jpg"),                                   // deleted since it was indexed
                System.IO.Path.Combine(_dir.Path, "nowhere", "e.jpg"), // and its folder too
                P("c.nef"),                                      // indexed on its own; its JPEG came later
                P("A.JPG"),                                      // as the index spelt it (case-insensitive systems)
                P("a.RAF"),                                      // the same photo again
            ],
            TestContext.Current.CancellationToken);

        var expected = OperatingSystem.IsLinux()
            ? new[] { ("d.png", ""), ("b.jpg", ""), ("c.jpg", "c.nef") }
            : new[] { ("d.png", ""), ("b.jpg", ""), ("c.jpg", "c.nef"), ("a.jpg", "a.RAF") };
        Assert.Equal(expected, found.Select(p => (System.IO.Path.GetFileName(p.Path), string.Join(",", p.Companions.Select(System.IO.Path.GetFileName)))));
        Assert.Equal(4, found[0].Listed?.Size); // from the folder listing, for finding thumbnails
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

    // NAS recycle bins, snapshots and thumbnail caches, which aren't hidden over SMB.
    public static TheoryData<string> HousekeepingFolders =>
        ["#recycle", "#snapshot", "@eaDir", "@tmp", "@Recycle", ".@__thumb", "$RECYCLE.BIN", ".Trashes", "#RECYCLE"];

    [Theory]
    [MemberData(nameof(HousekeepingFolders))]
    public async Task HousekeepingFolders_AreNotListed(string name)
    {
        TestImages.Write(_dir.Path, "a.jpg", TestImages.Jpeg(16, 16));
        var sub = Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "sub")).FullName;
        TestImages.Write(sub, "b.jpg", TestImages.Jpeg(16, 16));
        foreach (var folder in new[] { _dir.Path, sub, System.IO.Path.Combine(_dir.Path, "only") })
        {
            var skipped = Directory.CreateDirectory(System.IO.Path.Combine(folder, name, "nested")).Parent!.FullName;
            TestImages.Write(skipped, "deleted.jpg", TestImages.Jpeg(16, 16));
            TestImages.Write(System.IO.Path.Combine(skipped, "nested"), "deeper.jpg", TestImages.Jpeg(16, 16));
        }

        string[] expected = ["a.jpg", "b.jpg"];
        Assert.Equal(expected, PhotoFiles.EnumeratePhotosRecursive(_dir.Path).Select(p => System.IO.Path.GetFileName(p.Path)).Order());
        Assert.Equal(expected, (await PhotoFiles.EnumeratePhotosUnderAsync(_dir.Path, TestContext.Current.CancellationToken))
            .Select(p => System.IO.Path.GetFileName(p.Path)));
        Assert.Equal(["only", "sub"], PhotoFiles.EnumerateSubfolders(_dir.Path).Select(System.IO.Path.GetFileName));
        Assert.False(PhotoFiles.HasSubfolders(sub));

        Assert.True(PhotoFiles.IsInSkippedFolder(_dir.Path, System.IO.Path.Combine(sub, name, "deleted.jpg")));
        Assert.False(PhotoFiles.IsInSkippedFolder(_dir.Path, System.IO.Path.Combine(sub, "b.jpg")));
    }

    [Fact]
    public async Task OpeningAHousekeepingFolderItself_StillShowsItsPhotos()
    {
        // Only folders below the one asked for are skipped: someone looking inside #recycle on purpose sees what's there.
        var recycle = Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "#recycle")).FullName;
        TestImages.Write(recycle, "deleted.jpg", TestImages.Jpeg(16, 16));

        Assert.Single(PhotoFiles.EnumeratePhotosRecursive(recycle));
        Assert.Single(await PhotoFiles.EnumeratePhotosUnderAsync(recycle, TestContext.Current.CancellationToken));
        Assert.False(PhotoFiles.IsInSkippedFolder(recycle, System.IO.Path.Combine(recycle, "deleted.jpg")));
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
