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
    public async Task SetRating_SetsAndClears()
    {
        var editor = RequireEditor();
        var a = Photo("a.jpg", "Rated"); // test images with XMP have rating 4
        var b = Photo("b.jpg");

        var set = await editor.SetRatingAsync([a, b], 4, cancellationToken: Ct);
        Assert.Equal((1, 1), (set.Changed, set.Unchanged));
        Assert.Equal(4, PhotoMetadata.Read(b).Rating);

        await editor.SetRatingAsync([a, b], 0, cancellationToken: Ct);
        Assert.Null(PhotoMetadata.Read(a).Rating);
        Assert.Null(PhotoMetadata.Read(b).Rating);
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
