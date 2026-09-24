namespace PhotoTag.Core.Tests;

public sealed class LibraryIndexTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _library;
    private readonly LibraryIndex _index;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LibraryIndexTests()
    {
        _library = Directory.CreateDirectory(Path.Combine(_dir.Path, "Photos")).FullName;
        _index = new LibraryIndex(Path.Combine(_dir.Path, "index", "library.db"));
    }

    private string Photo(string relativePath, params string[] keywords)
    {
        var path = Path.Combine(_library, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, TestImages.Jpeg(32, 32, xmpKeywords: keywords.Length > 0 ? keywords : null));
        return path;
    }

    [Fact]
    public async Task Scan_IndexesTheWholeTree_WithCounts()
    {
        Photo("a.jpg", "Beach");
        Photo(Path.Combine("2020", "b.jpg"), "Beach", "Dog");
        Photo(Path.Combine("2020", "c.jpg"));
        Photo(Path.Combine("2020", "Summer", "d.jpg"), "Sunset");
        Photo(Path.Combine("2021", "e.jpg"));
        File.WriteAllText(Path.Combine(_library, "notes.txt"), "not a photo");

        var result = await _index.ScanAsync(_library, cancellationToken: Ct);

        Assert.Equal(new IndexScanResult(Total: 5, Updated: 5, Unchanged: 0, Removed: 0, Failed: 0), result);
        Assert.Equal(new FolderCounts(5, 3), await _index.GetFolderCountsAsync(_library));
        Assert.Equal(new FolderCounts(3, 2), await _index.GetFolderCountsAsync(Path.Combine(_library, "2020")));
        Assert.Equal(new FolderCounts(1, 1), await _index.GetFolderCountsAsync(Path.Combine(_library, "2020", "Summer")));
        Assert.Equal(new FolderCounts(1, 0), await _index.GetFolderCountsAsync(Path.Combine(_library, "2021") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task FolderCounts_DoNotIncludeSiblingsWithTheSamePrefix()
    {
        Photo(Path.Combine("2020", "a.jpg"), "X");
        Photo(Path.Combine("2020 extra", "b.jpg"), "X");
        Photo(Path.Combine("2020_more", "c.jpg"));
        Photo(Path.Combine("20201", "d.jpg"));
        await _index.ScanAsync(_library, cancellationToken: Ct);

        Assert.Equal(new FolderCounts(1, 1), await _index.GetFolderCountsAsync(Path.Combine(_library, "2020")));
    }

    [Fact]
    public async Task Rescan_SkipsUnchanged_ReadsChanged_AddsNew_RemovesDeleted()
    {
        var keep = Photo("keep.jpg", "Old");
        var change = Photo("change.jpg", "Before");
        var delete = Photo("delete.jpg");
        await _index.ScanAsync(_library, cancellationToken: Ct);

        File.WriteAllBytes(change, TestImages.Jpeg(40, 40, xmpKeywords: ["After"]));
        File.SetLastWriteTimeUtc(change, DateTime.UtcNow.AddMinutes(1));
        File.Delete(delete);
        Photo("new.jpg", "Fresh");

        var result = await _index.ScanAsync(_library, cancellationToken: Ct);

        Assert.Equal(new IndexScanResult(Total: 3, Updated: 2, Unchanged: 1, Removed: 1, Failed: 0), result);
        Assert.Equal([change], await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["After"] }));
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["Before"] }));
        Assert.Equal(new FolderCounts(3, 3), await _index.GetFolderCountsAsync(_library));
        Assert.Contains(keep, await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["Old"] }));
    }

    [Fact]
    public async Task Rescan_OfASubfolder_DoesNotRemovePhotosElsewhere()
    {
        Photo(Path.Combine("A", "a.jpg"));
        Photo(Path.Combine("B", "b.jpg"));
        await _index.ScanAsync(_library, cancellationToken: Ct);

        await _index.ScanAsync(Path.Combine(_library, "A"), cancellationToken: Ct);

        Assert.Equal(new FolderCounts(2, 0), await _index.GetFolderCountsAsync(_library));
    }

    [Fact]
    public async Task Search_MatchesAllKeywords_IgnoringCase_InFolderOrder()
    {
        var a = Photo(Path.Combine("x", "a.jpg"), "Beach", "Dog");
        var b = Photo("b.jpg", "beach");
        var c = Photo(Path.Combine("x", "c.jpg"), "BEACH", "dog", "Sunset");
        Photo("d.jpg", "Dog");
        await _index.ScanAsync(_library, cancellationToken: Ct);

        Assert.Equal([b, a, c], await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["Beach"] }));
        Assert.Equal([a, c], await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["dog", "BEACH"] }));
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["Beach", "Cat"] }));
        Assert.Equal([a, c], await _index.SearchAsync(Path.Combine(_library, "x"), new PhotoQuery { Keywords = ["beach"] }));
    }

    [Fact]
    public async Task Search_Terms_MatchWholeTags_OrTextInTitleDescriptionAndFileName()
    {
        var tagged = Photo("a.jpg", "Beach", "Dog");
        var titled = Photo("b.jpg");
        var described = Photo(Path.Combine("sub", "c.jpg"));
        var named = Photo("beachcombing 2020.jpg");
        var other = Photo("e.jpg", "Beaches");
        await _index.ScanAsync(_library, cancellationToken: Ct);
        await _index.UpdateAsync([
            (titled, new PhotoMetadata { Title = "Sunset at the BEACH café" }),
            (described, new PhotoMetadata { Description = "The dog on Porthcurno beach", Keywords = ["Walk"] }),
        ]);

        Assert.Equal([tagged, titled, named, described], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["beach"] }));
        // Tags must match whole: "Beaches" doesn't match "Beach", but "beac" is part of the other photos' text.
        Assert.Equal([titled, named, described], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["beac"] }));
        Assert.Equal([other], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["beaches"] }));
        // Every term must match, each in any field.
        Assert.Equal([tagged, described], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["dog", "beach"] }));
        Assert.Equal([described], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["walk", "porthcurno"] }));
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { Terms = ["beach", "cat"] }));
        // Case is ignored beyond ASCII too.
        Assert.Equal([titled], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["CAFÉ"] }));
        Assert.Equal([named], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["2020"] }));
    }

    [Fact]
    public async Task Search_Untagged()
    {
        Photo("tagged.jpg", "Beach");
        var untagged1 = Photo("u1.jpg");
        var untagged2 = Photo(Path.Combine("sub", "u2.jpg"));
        await _index.ScanAsync(_library, cancellationToken: Ct);

        Assert.Equal([untagged1, untagged2], await _index.SearchAsync(_library, new PhotoQuery { UntaggedOnly = true }));
    }

    [Fact]
    public async Task Search_Favourites_AloneAndWithTags()
    {
        var beach = Photo("beach.jpg", "Beach"); // test images with XMP are rated 4 stars: not favourites
        var dog = Photo("dog.jpg", "Dog");
        var plain = Photo(Path.Combine("sub", "plain.jpg"));
        await _index.ScanAsync(_library, cancellationToken: Ct);
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { FavouritesOnly = true }));

        await _index.UpdateAsync([
            (beach, PhotoMetadata.Read(beach) with { Rating = PhotoMetadataWriter.FavouriteRating }),
            (plain, new PhotoMetadata { Rating = PhotoMetadataWriter.FavouriteRating }),
        ]);

        Assert.Equal([beach, plain], await _index.SearchAsync(_library, new PhotoQuery { FavouritesOnly = true }));
        Assert.Equal([beach], await _index.SearchAsync(_library, new PhotoQuery { FavouritesOnly = true, Keywords = ["beach"] }));
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { FavouritesOnly = true, Keywords = ["Dog"] }));
        Assert.Equal([plain], await _index.SearchAsync(_library, new PhotoQuery { FavouritesOnly = true, UntaggedOnly = true }));
        Assert.Equal([dog], await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["Dog"] }));

        Assert.Equal([beach, plain], (await _index.GetFavouritesAsync(_library)).Order());
        Assert.Equal([plain], await _index.GetFavouritesAsync(Path.Combine(_library, "sub")));
    }

    [Fact]
    public async Task Keywords_AreCountedAcrossTheLibrary()
    {
        Photo("a.jpg", "Beach", "Dog");
        Photo(Path.Combine("sub", "b.jpg"), "beach");
        Photo(Path.Combine("sub", "c.jpg"), "Cat");
        await _index.ScanAsync(_library, cancellationToken: Ct);

        var all = await _index.GetKeywordsAsync();
        // Most used first; case variants merged, keeping the most used spelling.
        Assert.Equal(("Beach", 2), (all[0].Keyword, all[0].Count));
        Assert.Equal(["beach"], all[0].OtherSpellings);
        Assert.Empty(all.Single(k => k.Keyword == "Cat").OtherSpellings);
        Assert.Equal(["Beach", "Cat", "Dog"], all.Select(k => k.Keyword).Order());

        var sub = await _index.GetKeywordsAsync(Path.Combine(_library, "sub"));
        Assert.Equal(2, sub.Count);
    }

    [Fact]
    public async Task Update_RecordsEditsWithoutARescan()
    {
        var path = Photo("a.jpg", "Old");
        await _index.ScanAsync(_library, cancellationToken: Ct);

        await _index.UpdateAsync([(path, new PhotoMetadata { Keywords = ["New", "Tags"], Rating = PhotoMetadataWriter.FavouriteRating })]);

        Assert.Equal([path], await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["new", "tags"] }));
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["Old"] }));
        Assert.Equal([path], await _index.SearchAsync(_library, new PhotoQuery { FavouritesOnly = true }));
    }

    [Fact]
    public async Task CorruptPhotos_AreIndexedAsUntagged()
    {
        File.WriteAllBytes(Path.Combine(_library, "broken.jpg"), [0xFF, 0xD8, 1, 2, 3]);
        Photo("ok.jpg", "Fine");

        var result = await _index.ScanAsync(_library, cancellationToken: Ct);

        Assert.Equal(0, result.Failed);
        Assert.Equal(new FolderCounts(2, 1), await _index.GetFolderCountsAsync(_library));
    }

    [Fact]
    public async Task Index_PersistsAcrossInstances()
    {
        var dbPath = Path.Combine(_dir.Path, "persist.db");
        Photo("a.jpg", "Kept");
        using (var first = new LibraryIndex(dbPath)) await first.ScanAsync(_library, cancellationToken: Ct);

        using var second = new LibraryIndex(dbPath);
        Assert.Equal(new FolderCounts(1, 1), await second.GetFolderCountsAsync(_library));
        Assert.Equal(1, (await second.ScanAsync(_library, cancellationToken: Ct)).Unchanged);
    }

    [Fact]
    public async Task Cancelling_AScan_Throws_AndLeavesTheIndexUsable()
    {
        for (var i = 0; i < 20; i++) Photo($"p{i}.jpg", "T");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _index.ScanAsync(_library, cancellationToken: new CancellationToken(canceled: true)));

        var result = await _index.ScanAsync(_library, cancellationToken: Ct);
        Assert.Equal(20, result.Total);
        Assert.Equal(new FolderCounts(20, 20), await _index.GetFolderCountsAsync(_library));
    }

    public void Dispose()
    {
        _index.Dispose();
        _dir.Dispose();
    }
}
