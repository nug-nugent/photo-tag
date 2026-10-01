using SkiaSharp;

namespace PhotoTag.Core.Tests;

public sealed class ThumbnailTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Theory]
    [InlineData(1200, 800, 300, 300, 200)] // exact 1/4 JPEG scale
    [InlineData(800, 600, 128, 128, 96)]   // nearest JPEG scale (1/8 = 100px) would undershoot
    [InlineData(600, 900, 320, 213, 320)]  // portrait
    public void Render_FitsWithinMaxSize_KeepingAspectRatio(int width, int height, int max, int expectedW, int expectedH)
    {
        var path = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(width, height));

        using var thumb = SKBitmap.Decode(ImageRenderer.Render(path, max));

        Assert.Equal((expectedW, expectedH), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void Render_DoesNotUpscaleSmallImages()
    {
        var path = TestImages.Write(_dir.Path, "small.jpg", TestImages.Jpeg(100, 50));

        using var thumb = SKBitmap.Decode(ImageRenderer.Render(path, 300));

        Assert.Equal((100, 50), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void Render_AppliesExifOrientation()
    {
        // Orientation 6 = stored landscape, displayed rotated 90° clockwise (portrait).
        var bytes = TestImages.Jpeg(400, 200, new TestImages.Exif { Orientation = 6 });
        var path = TestImages.Write(_dir.Path, "rotated.jpg", bytes);

        using var thumb = SKBitmap.Decode(ImageRenderer.Render(path, 400));

        Assert.Equal((200, 400), (thumb.Width, thumb.Height));
        // The red top-left corner of the stored image ends up top-right after a 90° CW turn.
        Assert.True(IsRed(thumb.GetPixel(thumb.Width - 10, 10)), "expected red at top-right");
        Assert.False(IsRed(thumb.GetPixel(10, 10)), "expected blue at top-left");
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, 0, 0)]
    [InlineData(SKEncodedOrigin.TopRight, 1, 0)]
    [InlineData(SKEncodedOrigin.BottomRight, 1, 1)]
    [InlineData(SKEncodedOrigin.BottomLeft, 0, 1)]
    [InlineData(SKEncodedOrigin.LeftTop, 0, 0)]
    [InlineData(SKEncodedOrigin.RightTop, 1, 0)]
    [InlineData(SKEncodedOrigin.RightBottom, 1, 1)]
    [InlineData(SKEncodedOrigin.LeftBottom, 0, 1)]
    public void ApplyOrientation_MovesTopLeftCornerToExpectedCorner(SKEncodedOrigin origin, int right, int bottom)
    {
        using var source = new SKBitmap(40, 20);
        source.Erase(SKColors.Blue);
        for (var x = 0; x < 5; x++)
            for (var y = 0; y < 5; y++)
                source.SetPixel(x, y, SKColors.Red);

        using var result = ImageRenderer.ApplyOrientation(source, origin);

        var x0 = right == 1 ? result.Width - 2 : 1;
        var y0 = bottom == 1 ? result.Height - 2 : 1;
        Assert.True(IsRed(result.GetPixel(x0, y0)), $"expected red corner at ({x0},{y0})");
    }

    [Fact]
    public async Task Cache_ReusesThumbnail_UntilSourceChanges()
    {
        var photo = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(800, 600));
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"), size: 128);

        var first = await cache.GetAsync(photo, TestContext.Current.CancellationToken);
        var created = File.GetLastWriteTimeUtc(first);
        var second = await cache.GetAsync(photo, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(created, File.GetLastWriteTimeUtc(second));

        File.SetLastWriteTimeUtc(photo, DateTime.UtcNow.AddMinutes(5));
        var third = await cache.GetAsync(photo, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, third);
        using var thumb = SKBitmap.Decode(third);
        Assert.Equal(128, thumb.Width);
    }

    [Fact]
    public async Task Cache_HonoursCancellationWhileQueued()
    {
        var photo = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 200));
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetAsync(photo, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Cache_RendersInTheOrderAsked_SkippingCancelledRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var photos = Enumerable.Range(0, 8).Select(i => TestImages.Write(_dir.Path, $"photo{i}.jpg", TestImages.Jpeg(50, 50))).ToList();
        var thumbnail = File.ReadAllBytes(photos[0]);
        var rendered = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var busy = new SemaphoreSlim(0);
        using var carryOn = new ManualResetEventSlim();
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"), (path, _, _) =>
        {
            rendered.Enqueue(path);
            if (path == photos[0])
            {
                busy.Release();
                carryOn.Wait(ct);
            }
            return thumbnail;
        }, maxConcurrency: 1);

        var first = cache.GetAsync(photos[0], ct);
        await busy.WaitAsync(ct); // the only render thread is busy with the first photo
        using var scrolledAway = new CancellationTokenSource();
        var rest = photos.Skip(1).Select(p => cache.GetAsync(p, p == photos[3] ? scrolledAway.Token : ct)).ToList();
        while (cache.RendersWaiting < rest.Count) await Task.Delay(10, ct); // all looked up, none cached
        await scrolledAway.CancelAsync();
        carryOn.Set();

        await first;
        foreach (var request in rest.Where((_, i) => i != 2)) await request;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rest[2]);
        Assert.Equal(photos.Where(p => p != photos[3]), rendered);
    }

    [Fact]
    public async Task Cache_FindsThumbnail_FromTheFolderListing_WithoutTouchingThePhoto()
    {
        var ct = TestContext.Current.CancellationToken;
        var photo = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 200));
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"));
        var thumbnail = await cache.GetAsync(photo, ct);
        var listed = Assert.Single(PhotoFiles.EnumeratePhotos(_dir.Path)).Listed;
        Assert.NotNull(listed);

        File.Delete(photo); // as good as unreachable

        Assert.Equal(thumbnail, await cache.GetAsync(photo, listed, ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => cache.GetAsync(photo, ct));
    }

    [Fact]
    public async Task Cache_ThrowsForCorruptFile()
    {
        var photo = TestImages.Write(_dir.Path, "broken.jpg", [1, 2, 3, 4]);
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => cache.GetAsync(photo, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_NotesWhenAThumbnailIsUsed_AtMostDaily()
    {
        var photo = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 200));
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"));
        var thumbnail = await cache.GetAsync(photo, TestContext.Current.CancellationToken);

        var hourAgo = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(thumbnail, hourAgo);
        await cache.GetAsync(photo, TestContext.Current.CancellationToken);
        Assert.Equal(hourAgo, File.GetLastWriteTimeUtc(thumbnail)); // not written again so soon

        File.SetLastWriteTimeUtc(thumbnail, DateTime.UtcNow.AddDays(-3));
        await cache.GetAsync(photo, TestContext.Current.CancellationToken);
        Assert.InRange(File.GetLastWriteTimeUtc(thumbnail), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task CleanUp_DeletesLeastRecentlyUsed_DownToThreeQuartersOfTheLimit()
    {
        var dir = Path.Combine(_dir.Path, "cache");
        using var cache = new ThumbnailCache(dir);
        var files = Enumerable.Range(0, 10).Select(i => FakeThumbnail(dir, $"{i}.thumb", 1000, DateTime.UtcNow.AddDays(-2 - i))).ToList();

        var result = await cache.CleanUpAsync(maxBytes: 5000, TestContext.Current.CancellationToken);

        Assert.Equal(new CacheCleanUp(Deleted: 7, Freed: 7000, Size: 3000), result);
        Assert.Equal(files[..3], files.Where(File.Exists)); // the three used most recently
    }

    [Fact]
    public async Task CleanUp_LeavesACacheUnderTheLimitAlone()
    {
        var dir = Path.Combine(_dir.Path, "cache");
        using var cache = new ThumbnailCache(dir);
        for (var i = 0; i < 5; i++) FakeThumbnail(dir, $"{i}.thumb", 1000, DateTime.UtcNow.AddDays(-100));

        var result = await cache.CleanUpAsync(maxBytes: 5000, TestContext.Current.CancellationToken);

        Assert.Equal(new CacheCleanUp(0, 0, 5000), result);
    }

    [Fact]
    public async Task CleanUp_KeepsThumbnailsUsedToday_EvenOverTheLimit()
    {
        var dir = Path.Combine(_dir.Path, "cache");
        using var cache = new ThumbnailCache(dir);
        var old = FakeThumbnail(dir, "old.thumb", 1000, DateTime.UtcNow.AddDays(-5));
        for (var i = 0; i < 5; i++) FakeThumbnail(dir, $"{i}.thumb", 1000, DateTime.UtcNow.AddHours(-i));

        var result = await cache.CleanUpAsync(maxBytes: 2000, TestContext.Current.CancellationToken);

        Assert.Equal(new CacheCleanUp(1, 1000, 5000), result);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task CleanUp_RemovesHalfWrittenFilesFromACrash()
    {
        var dir = Path.Combine(_dir.Path, "cache");
        using var cache = new ThumbnailCache(dir);
        var leftOver = FakeThumbnail(dir, "a.thumb.123.tmp", 500, DateTime.UtcNow.AddDays(-2));
        var beingWritten = FakeThumbnail(dir, "b.thumb.456.tmp", 500, DateTime.UtcNow);

        var result = await cache.CleanUpAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new CacheCleanUp(1, 500, 0), result);
        Assert.False(File.Exists(leftOver));
        Assert.True(File.Exists(beingWritten));
    }

    [Fact]
    public async Task CleanUp_KeepsThumbnailsUsedSince_OverOnesMadeLongAgo()
    {
        var photos = Enumerable.Range(0, 3)
            .Select(i => TestImages.Write(_dir.Path, $"photo{i}.jpg", TestImages.Jpeg(200, 200))).ToList();
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"));
        var thumbnails = new List<string>();
        foreach (var photo in photos) thumbnails.Add(await cache.GetAsync(photo, TestContext.Current.CancellationToken));
        foreach (var thumbnail in thumbnails) File.SetLastWriteTimeUtc(thumbnail, DateTime.UtcNow.AddDays(-10));

        await cache.GetAsync(photos[1], TestContext.Current.CancellationToken); // looked at again
        await cache.CleanUpAsync(maxBytes: 1, TestContext.Current.CancellationToken);

        Assert.Equal([thumbnails[1]], thumbnails.Where(File.Exists));
    }

    private static string FakeThumbnail(string cacheDirectory, string name, int size, DateTime lastUsed)
    {
        var path = Path.Combine(cacheDirectory, name[..1], name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, lastUsed);
        return path;
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Blue < 80;

    public void Dispose() => _dir.Dispose();
}
