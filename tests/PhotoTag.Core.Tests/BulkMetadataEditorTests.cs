namespace PhotoTag.Core.Tests;

public sealed class BulkMetadataEditorTests(ExifToolFixture fixture) : IClassFixture<ExifToolFixture>, IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BulkMetadataEditor RequireEditor() => new(fixture.RequireWriter());

    private string Photo(string name, params string[] keywords) =>
        TestImages.Write(_dir.Path, name, TestImages.Jpeg(64, 64, xmpKeywords: keywords.Length > 0 ? keywords : null));

    [Fact]
    public async Task AddKeywords_KeepsExistingTags_AndSkipsPhotosThatAlreadyHaveThem()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Family");
        var b = Photo("b.jpg", "beach"); // already has it, different case
        var c = Photo("c.jpg");

        var result = await editor.AddKeywordsAsync([a, b, c], ["Beach"], cancellationToken: Ct);

        Assert.Equal(2, result.Changed);
        Assert.Equal(1, result.Unchanged);
        Assert.Empty(result.Failures);
        Assert.Equal(["Family", "Beach"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal(["beach"], PhotoMetadata.Read(b).Keywords);
        Assert.Equal(["Beach"], PhotoMetadata.Read(c).Keywords);
        Assert.Equal(["Family", "Beach"], result.After[a].Keywords);
    }

    [Fact]
    public async Task RemoveKeywords_IsCaseInsensitive_AndLeavesOtherTags()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Beach", "Dog");
        var b = Photo("b.jpg", "BEACH");
        var c = Photo("c.jpg", "Dog");

        var result = await editor.RemoveKeywordsAsync([a, b, c], ["beach"], cancellationToken: Ct);

        Assert.Equal((2, 1), (result.Changed, result.Unchanged));
        Assert.Equal(["Dog"], PhotoMetadata.Read(a).Keywords);
        Assert.Empty(PhotoMetadata.Read(b).Keywords);
        Assert.Equal(["Dog"], PhotoMetadata.Read(c).Keywords);
    }

    [Fact]
    public async Task RenameKeyword_ReplacesEverySpelling_KeepingPosition()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Dog", "seaside", "Sunset");
        var b = Photo("b.jpg", "SEASIDE");
        var c = Photo("c.jpg", "Dog");

        var result = await editor.RenameKeywordAsync([a, b, c], "Seaside", "Coast", cancellationToken: Ct);

        Assert.Equal((2, 1), (result.Changed, result.Unchanged));
        Assert.Equal(["Dog", "Coast", "Sunset"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal(["Coast"], PhotoMetadata.Read(b).Keywords);
        Assert.Equal(["Dog"], PhotoMetadata.Read(c).Keywords);
        Assert.Equal(["Dog", "Coast", "Sunset"], result.After[a].Keywords);
    }

    [Fact]
    public async Task RenameKeyword_ToAnExistingTag_MergesThem()
    {
        var editor = RequireEditor();
        var both = Photo("both.jpg", "beach", "Seaside"); // the other spelling first: "Beach" still wins
        var old = Photo("old.jpg", "Seaside");
        var done = Photo("done.jpg", "Beach");

        var result = await editor.RenameKeywordAsync([both, old, done], "Seaside", "Beach", cancellationToken: Ct);

        Assert.Equal((2, 1), (result.Changed, result.Unchanged));
        Assert.Equal(["Beach"], PhotoMetadata.Read(both).Keywords);
        Assert.Equal(["Beach"], PhotoMetadata.Read(old).Keywords);
        Assert.Equal(["Beach"], PhotoMetadata.Read(done).Keywords);
    }

    [Fact]
    public async Task RenameKeyword_ToItself_TidiesCaseVariants()
    {
        var editor = RequireEditor();
        var lower = Photo("lower.jpg", "beach", "Dog");
        var right = Photo("right.jpg", "Beach");

        var result = await editor.RenameKeywordAsync([lower, right], "Beach", "Beach", cancellationToken: Ct);

        Assert.Equal((1, 1), (result.Changed, result.Unchanged));
        Assert.Equal(["Beach", "Dog"], PhotoMetadata.Read(lower).Keywords);
    }

    [Fact]
    public async Task Undo_PutsBackTheTags_OnlyOnPhotosThatChanged()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Family");
        var b = Photo("b.jpg", "beach"); // already had it: not written, so nothing to undo
        var c = Photo("c.jpg");
        var added = await editor.AddKeywordsAsync([a, b, c], ["Beach"], cancellationToken: Ct);
        Assert.Equal([a, c], added.Written.Select(w => w.Path));

        var undone = await editor.UndoAsync(added.Written, cancellationToken: Ct);

        Assert.Equal((2, 0, 0), (undone.Changed, undone.Unchanged, undone.ChangedSince));
        Assert.Equal(["Family"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal(["beach"], PhotoMetadata.Read(b).Keywords);
        Assert.Empty(PhotoMetadata.Read(c).Keywords);
        Assert.Equal(["Family"], undone.After[a].Keywords);
    }

    [Fact]
    public async Task Undo_PutsBackRenamesAndDeletes()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Dog", "seaside", "Sunset");
        var b = Photo("b.jpg", "Beach", "Seaside");

        var renamed = await editor.RenameKeywordAsync([a, b], "Seaside", "Beach", cancellationToken: Ct);
        await editor.UndoAsync(renamed.Written, cancellationToken: Ct);
        Assert.Equal(["Dog", "seaside", "Sunset"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal(["Beach", "Seaside"], PhotoMetadata.Read(b).Keywords);

        var deleted = await editor.RemoveKeywordsAsync([a, b], ["Beach"], cancellationToken: Ct);
        await editor.UndoAsync(deleted.Written, cancellationToken: Ct);
        Assert.Equal(["Beach", "Seaside"], PhotoMetadata.Read(b).Keywords);
    }

    [Fact]
    public async Task Undo_LeavesPhotosEditedAgainSinceAlone()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Family");
        var b = Photo("b.jpg", "Family");
        var added = await editor.AddKeywordsAsync([a, b], ["Beach"], cancellationToken: Ct);
        await editor.AddKeywordsAsync([a], ["Later"], cancellationToken: Ct); // someone kept working on a

        var undone = await editor.UndoAsync(added.Written, cancellationToken: Ct);

        Assert.Equal((1, 1, 1), (undone.Changed, undone.Unchanged, undone.ChangedSince));
        Assert.Equal(["Family", "Beach", "Later"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal(["Family"], PhotoMetadata.Read(b).Keywords);
    }

    [Fact]
    public async Task Undo_PutsBackFavourites_AndRatingsFromOtherApps()
    {
        var editor = RequireEditor();
        var rated = Photo("rated.jpg", "Rated"); // test images with XMP are rated 4 stars
        var plain = Photo("plain.jpg");
        var favourite = Photo("favourite.jpg");
        await editor.SetFavouriteAsync([favourite], true, cancellationToken: Ct);

        var set = await editor.SetFavouriteAsync([rated, plain], true, cancellationToken: Ct);
        await editor.UndoAsync(set.Written, cancellationToken: Ct);
        Assert.Equal(4, PhotoMetadata.Read(rated).Rating);
        Assert.Null(PhotoMetadata.Read(plain).Rating);

        var cleared = await editor.SetFavouriteAsync([favourite], false, cancellationToken: Ct);
        await editor.UndoAsync(cleared.Written, cancellationToken: Ct);
        Assert.True(PhotoMetadata.Read(favourite).IsFavourite);
    }

    [Fact]
    public async Task SetText_ReplacesOrClears_OnlyTheFieldsGiven()
    {
        var writer = fixture.RequireWriter();
        var editor = new BulkMetadataEditor(writer);
        var a = Photo("a.jpg", "Beach");
        var b = Photo("b.jpg");
        var c = Photo("c.jpg");
        await writer.WriteAsync(a, new MetadataChanges { Title = "Old", Description = "OLYMPUS DIGITAL CAMERA" }, Ct);
        await writer.WriteAsync(c, new MetadataChanges { Title = "Harbour" }, Ct);

        var titled = await editor.SetTextAsync([a, b, c], "  Harbour ", null, cancellationToken: Ct);
        Assert.Equal((2, 1), (titled.Changed, titled.Unchanged)); // c already had it
        Assert.All([a, b, c], p => Assert.Equal("Harbour", PhotoMetadata.Read(p).Title));
        Assert.Equal("OLYMPUS DIGITAL CAMERA", PhotoMetadata.Read(a).Description);
        Assert.Equal(["Beach"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal("Harbour", titled.After[b].Title);

        var cleared = await editor.SetTextAsync([a, b, c], null, "", cancellationToken: Ct);
        Assert.Equal((1, 2), (cleared.Changed, cleared.Unchanged));
        Assert.Null(PhotoMetadata.Read(a).Description);
        Assert.Null(cleared.After[a].Description);

        await editor.SetTextAsync([a, b], null, "Line one\r\nLine two", cancellationToken: Ct);
        Assert.Equal("Line one\nLine two", PhotoMetadata.Read(b).Description);
    }

    [Fact]
    public async Task Undo_PutsBackTitlesAndDescriptions_UnlessEditedSince()
    {
        var writer = fixture.RequireWriter();
        var editor = new BulkMetadataEditor(writer);
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var c = Photo("c.jpg");
        await writer.WriteAsync(a, new MetadataChanges { Title = "Old", Description = "Kept?" }, Ct);

        var set = await editor.SetTextAsync([a, b, c], "New", "", cancellationToken: Ct);
        await writer.WriteAsync(c, new MetadataChanges { Description = "Written since" }, Ct);
        var undone = await editor.UndoAsync(set.Written, cancellationToken: Ct);

        Assert.Equal((2, 1), (undone.Changed, undone.ChangedSince));
        Assert.Equal(("Old", "Kept?"), (PhotoMetadata.Read(a).Title, PhotoMetadata.Read(a).Description));
        Assert.Null(PhotoMetadata.Read(b).Title);
        Assert.Equal(("New", "Written since"), (PhotoMetadata.Read(c).Title, PhotoMetadata.Read(c).Description));
    }

    [Fact]
    public async Task SetFavourite_SetsAndClears_LeavingOtherRatingsAlone()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Rated"); // test images with XMP have rating 4
        var b = Photo("b.jpg");
        var c = Photo("c.jpg", "Rated");

        var set = await editor.SetFavouriteAsync([a, b], true, cancellationToken: Ct);
        Assert.Equal((2, 0), (set.Changed, set.Unchanged));
        Assert.True(PhotoMetadata.Read(a).IsFavourite);
        Assert.True(PhotoMetadata.Read(b).IsFavourite);
        Assert.True(set.After[a].IsFavourite);

        var again = await editor.SetFavouriteAsync([a, b], true, cancellationToken: Ct);
        Assert.Equal((0, 2), (again.Changed, again.Unchanged));

        // c isn't a favourite, so unfavouriting leaves its 4-star rating from another app alone.
        var cleared = await editor.SetFavouriteAsync([a, b, c], false, cancellationToken: Ct);
        Assert.Equal((2, 1), (cleared.Changed, cleared.Unchanged));
        Assert.Null(PhotoMetadata.Read(a).Rating);
        Assert.Null(PhotoMetadata.Read(b).Rating);
        Assert.Equal(4, PhotoMetadata.Read(c).Rating);
        Assert.Equal(["Rated"], PhotoMetadata.Read(a).Keywords);
    }

    [Fact]
    public async Task ReadsEachFileJustBeforeWriting_SoOtherEditsSurvive()
    {
        var writer = fixture.RequireWriter();
        var editor = new BulkMetadataEditor(writer);
        var a = Photo("a.jpg", "Original");

        // Someone else tags the file after the selection was loaded.
        await writer.WriteAsync(a, new MetadataChanges { Keywords = ["Original", "AddedElsewhere"] }, Ct);
        await editor.AddKeywordsAsync([a], ["Bulk"], cancellationToken: Ct);

        Assert.Equal(["Original", "AddedElsewhere", "Bulk"], PhotoMetadata.Read(a).Keywords);
    }

    [Fact]
    public async Task FailuresAreReported_AndTheRestStillSucceed()
    {
        var editor = RequireEditor();
        var good1 = Photo("1.jpg");
        var corrupt = TestImages.Write(_dir.Path, "2.jpg", [0xFF, 0xD8, 1, 2, 3]);
        var missing = Path.Combine(_dir.Path, "gone.jpg");
        var good2 = Photo("3.jpg");

        var result = await editor.AddKeywordsAsync([good1, corrupt, missing, good2], ["X"], cancellationToken: Ct);

        Assert.Equal(2, result.Changed);
        Assert.Equal([corrupt, missing], result.Failures.Select(f => f.Path));
        Assert.Equal(["X"], PhotoMetadata.Read(good2).Keywords);
    }

    [Fact]
    public async Task Cancelling_StopsBetweenFiles_AndReportsProgress()
    {
        var editor = RequireEditor();
        var paths = Enumerable.Range(0, 6).Select(i => Photo($"{i}.jpg")).ToList();
        using var cts = new CancellationTokenSource();
        var reports = new List<BulkProgress>();
        var progress = new SynchronousProgress(p =>
        {
            reports.Add(p);
            if (p.Done == 2) cts.Cancel();
        });

        var result = await editor.AddKeywordsAsync(paths, ["Partial"], progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(2, result.Changed);
        Assert.Equal([new BulkProgress(1, 6), new BulkProgress(2, 6)], reports);
        Assert.Equal(["Partial"], PhotoMetadata.Read(paths[1]).Keywords);
        Assert.Empty(PhotoMetadata.Read(paths[2]).Keywords);
    }

    /// <summary>Unlike Progress&lt;T&gt;, reports inline so the test can cancel at an exact point.</summary>
    private sealed class SynchronousProgress(Action<BulkProgress> report) : IProgress<BulkProgress>
    {
        public void Report(BulkProgress value) => report(value);
    }

    public void Dispose() => _dir.Dispose();
}
