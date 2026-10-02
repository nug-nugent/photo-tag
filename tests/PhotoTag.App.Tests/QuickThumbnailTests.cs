using Avalonia.Headless.XUnit;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;
using PhotoTag.Core.Tests;

namespace PhotoTag.App.Tests;

/// <summary>A tile shows the small thumbnail embedded in a JPEG while its real one is made.</summary>
public sealed class QuickThumbnailTests : UiTestBase
{
    [AvaloniaFact]
    public async Task Tile_ShowsTheEmbeddedThumbnail_UntilTheRealOneIsReady()
    {
        var path = PhotoWithThumbnail();
        var real = await Task.Run(() => ImageRenderer.Render(path, 320), TestContext.Current.CancellationToken);
        using var carryOn = new ManualResetEventSlim();
        using var cache = new ThumbnailCache(ThumbnailsPath(0), (_, _, _) =>
        {
            carryOn.Wait(TimeSpan.FromSeconds(15));
            return real;
        });
        var photo = new PhotoItemViewModel(PhotoFile.Single(path), 0, cache);

        photo.Realize();
        await WaitForAsync(() => photo.Thumbnail is not null);
        Assert.True(photo.IsQuickThumbnail);
        var quick = photo.Thumbnail;

        carryOn.Set();
        await WaitForAsync(() => !photo.IsQuickThumbnail);
        Assert.NotNull(photo.Thumbnail);
        Assert.NotSame(quick, photo.Thumbnail);

        photo.Release();
        Assert.Null(photo.Thumbnail);
    }

    [AvaloniaFact]
    public async Task Tile_ScrolledAway_DoesNotTakeTheEmbeddedThumbnail_AlreadyOnItsWay()
    {
        var path = PhotoWithThumbnail();
        using var lookedAt = new ManualResetEventSlim();
        using var carryOn = new ManualResetEventSlim();
        using var cache = new ThumbnailCache(ThumbnailsPath(0), (_, _, _) =>
        {
            carryOn.Wait(TimeSpan.FromSeconds(15));
            throw new OperationCanceledException();
        }, readEmbedded: p =>
        {
            lookedAt.Set();
            return EmbeddedThumbnail.Read(p);
        });
        var photo = new PhotoItemViewModel(PhotoFile.Single(path), 0, cache);

        // Holding up the UI thread, so the embedded thumbnail is passed on but waits to be shown...
        photo.Realize();
        Assert.True(lookedAt.Wait(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Thread.Sleep(300);

        // ...when the tile scrolls away.
        photo.Release();
        carryOn.Set();
        for (var i = 0; i < 10; i++)
        {
            Settle();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Null(photo.Thumbnail);
        Assert.False(photo.IsQuickThumbnail);
    }

    private string PhotoWithThumbnail() =>
        TestImages.Write(DirPath, "photo.jpg", TestImages.Jpeg(400, 300, new TestImages.Exif { Thumbnail = TestImages.Thumbnail(160, 120) }));
}
