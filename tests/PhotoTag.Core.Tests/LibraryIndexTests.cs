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
    public async Task Search_Terms_MatchPlaces_AndPlaceValuesAreListed()
    {
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        Photo("c.jpg");
        await _index.ScanAsync(_library, cancellationToken: Ct);
        await _index.UpdateAsync([
            (a, new PhotoMetadata { Location = "Porthcurno beach", City = "St Levan", Country = "UK" }),
            (b, new PhotoMetadata { City = "St Ives", State = "Cornwall", Country = "UK" }),
        ]);

        Assert.Equal([a], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["porthcurno"] }));
        Assert.Equal([a, b], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["st "] }));
        Assert.Equal([b], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["cornwall", "uk"] }));
        Assert.Equal(["St Ives", "St Levan"], (await _index.GetPlaceValuesAsync(TextField.City)).Order());
        Assert.Equal(["UK"], await _index.GetPlaceValuesAsync(TextField.Country));
    }

    [Fact]
    public async Task AnIndexFromVersion1_GainsPlaces_AndItsPhotosAreReadAgain()
    {
        var path = Path.Combine(_dir.Path, "index", "old.db");
        var photo = Photo("a.jpg", "Beach");
        using (var current = new LibraryIndex(path)) await current.ScanAsync(_library, cancellationToken: Ct);

        // Turn it back into a version 1 index, with the photo already in it.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE photos DROP COLUMN location; ALTER TABLE photos DROP COLUMN city;
                ALTER TABLE photos DROP COLUMN state; ALTER TABLE photos DROP COLUMN country;
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }
        using var upgraded = new LibraryIndex(path);

        Assert.Equal(new FolderCounts(1, 1), await upgraded.GetFolderCountsAsync(_library)); // usable straight away
        Assert.Equal([photo], await upgraded.SearchAsync(_library, new PhotoQuery { Keywords = ["beach"] }));
        Assert.Equal(1, (await upgraded.ScanAsync(_library, cancellationToken: Ct)).Updated); // and read again on the next scan
        Assert.Empty(await upgraded.GetPlaceValuesAsync(TextField.City));
    }

    [Fact]
    public async Task People_AreSearchedByAnyPartOfTheName_AndCounted()
    {
        var a = Photo("a.jpg", "Beach");
        var b = Photo("b.jpg");
        Photo("c.jpg");
        await _index.ScanAsync(_library, cancellationToken: Ct);
        await _index.UpdateAsync([
            (a, new PhotoMetadata { Keywords = ["Beach"], People = ["Mary Smith", "Dad"] }),
            (b, new PhotoMetadata { People = ["mary smith"] }),
        ]);

        Assert.Equal([a, b], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["smith"] }));
        Assert.Equal([a], await _index.SearchAsync(_library, new PhotoQuery { Terms = ["smith", "beach"] }));
        Assert.Equal([a], await _index.SearchAsync(_library, new PhotoQuery { People = ["dad"] }));
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { People = ["smith"] })); // whole names only
        Assert.Empty(await _index.SearchAsync(_library, new PhotoQuery { Keywords = ["Dad"] })); // people aren't tags

        var people = await _index.GetValuesAsync(ListField.People);
        Assert.Equal(("Mary Smith", 2), (people[0].Keyword, people[0].Count));
        Assert.Equal(["mary smith"], people[0].OtherSpellings);
        Assert.Equal(("Dad", 1), (people[1].Keyword, people[1].Count));
        Assert.Equal(["Beach"], (await _index.GetKeywordsAsync()).Select(k => k.Keyword));
    }

    [Fact]
    public async Task AnIndexFromVersion2_GainsPeople_AndItsPhotosAreReadAgain()
    {
        var path = Path.Combine(_dir.Path, "index", "v2.db");
        var photo = Photo("a.jpg", "Beach");
        using (var current = new LibraryIndex(path)) await current.ScanAsync(_library, cancellationToken: Ct);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE photo_people; PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }
        using var upgraded = new LibraryIndex(path);

        Assert.Equal([photo], await upgraded.SearchAsync(_library, new PhotoQuery { Keywords = ["beach"] }));
        Assert.Equal(1, (await upgraded.ScanAsync(_library, cancellationToken: Ct)).Updated);
        Assert.Empty(await upgraded.GetValuesAsync(ListField.People));
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
    public async Task Summaries_HaveWhatTheGridShows_ForPhotosUnderTheFolder()
    {
        var beach = Photo("beach.jpg", "Beach", "Dog");
        var plain = Photo(Path.Combine("sub", "plain.jpg"));
        await _index.ScanAsync(_library, cancellationToken: Ct);
        await _index.UpdateAsync([(plain, new PhotoMetadata
        {
            Rating = PhotoMetadataWriter.FavouriteRating,
            DateTaken = new DateTime(2019, 8, 12, 14, 30, 5),
            Title = "First swim",
            City = "St Ives",
        })]);

        var all = await _index.GetSummariesAsync(_library);
        Assert.Equal(2, all.Count);
        Assert.Equal(["Beach", "Dog"], all[beach].Keywords.Order());
        Assert.False(all[beach].IsFavourite);
        Assert.Null(all[beach].DateTaken);

        var summary = all[plain];
        Assert.True(summary.IsFavourite);
        Assert.Empty(summary.Keywords);
        Assert.Equal(new DateTime(2019, 8, 12, 14, 30, 5), summary.DateTaken);
        Assert.Equal("First swim", summary.Title);
        Assert.Equal("St Ives", summary.City);

        Assert.Equal([plain], (await _index.GetSummariesAsync(Path.Combine(_library, "sub"))).Keys);
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
