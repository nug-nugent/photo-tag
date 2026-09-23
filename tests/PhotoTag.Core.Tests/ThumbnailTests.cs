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
    public async Task Cache_ThrowsForCorruptFile()
    {
        var photo = TestImages.Write(_dir.Path, "broken.jpg", [1, 2, 3, 4]);
        using var cache = new ThumbnailCache(Path.Combine(_dir.Path, "cache"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => cache.GetAsync(photo, TestContext.Current.CancellationToken));
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Blue < 80;

    public void Dispose() => _dir.Dispose();
}
