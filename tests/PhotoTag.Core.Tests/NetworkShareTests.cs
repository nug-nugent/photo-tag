namespace PhotoTag.Core.Tests;

/// <summary>
/// The same operations over SMB, through Windows' administrative share for the temp folder's drive
/// (<c>\\localhost\C$\…</c>). A real network share, minus the network; skipped where there isn't one
/// (macOS, Linux, or a Windows account that isn't an administrator).
/// </summary>
public sealed class NetworkShareTests(ExifToolFixture fixture) : IClassFixture<ExifToolFixture>, IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The temp folder as a UNC path, or skips.</summary>
    private string Share()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows' administrative shares only.");
        var full = Path.GetFullPath(_dir.Path);
        var unc = $@"\\localhost\{full[0]}${full[2..]}";
        if (!Directory.Exists(unc)) Assert.Skip($"{unc} isn't reachable (the account may not be an administrator).");
        return unc;
    }

    private static string Photo(string folder, string name, params string[] keywords)
    {
        Directory.CreateDirectory(folder);
        return TestImages.Write(folder, name, TestImages.Jpeg(64, 48, xmpKeywords: keywords.Length > 0 ? keywords : null));
    }

    [Fact]
    public async Task Indexing_WorksOverTheShare()
    {
        var share = Share();
        Assert.True(PhotoFiles.IsOnNetworkDrive(share));
        Photo(share, "a.jpg", "Beach");
        Photo(Path.Combine(share, "2020"), "b.jpg");
        using var index = new LibraryIndex(Path.Combine(_dir.Path, "index", "library.db"));

        Assert.Equal(2, (await index.ScanAsync(share, cancellationToken: Ct)).Updated);
        Assert.Equal(2, (await index.ScanAsync(share, cancellationToken: Ct)).Unchanged);
        Assert.Equal(new FolderCounts(2, 1), await index.GetFolderCountsAsync(share));

        var added = Photo(share, "c.jpg", "New");
        Assert.Equal([added], (await index.RefreshAsync(share, includeSubfolders: false, Ct)).Added);
    }

    [Fact]
    public async Task ChangesOnTheShare_AreNoticed()
    {
        var share = Share();
        using var watcher = new FolderWatcher(share, quietPeriod: TimeSpan.FromMilliseconds(300));
        var reported = new System.Collections.Concurrent.ConcurrentBag<string>();
        watcher.Changed += (_, changes) => { foreach (var path in changes.Paths) reported.Add(path); };
        Assert.True(watcher.IsWatching);

        var photo = Photo(share, "new.jpg");

        var until = DateTime.UtcNow.AddSeconds(15);
        while (!reported.Contains(photo, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < until) await Task.Delay(50, Ct);
        Assert.Contains(photo, reported, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TagsAreWritten_AndThumbnailsMade_OverTheShare()
    {
        var share = Share();
        var photo = Photo(share, "a.jpg", "Before");
        var writer = fixture.RequireWriter();

        await writer.WriteAsync(photo, new MetadataChanges { Keywords = ["After", "Café"] }, Ct);

        Assert.Equal(["After", "Café"], PhotoMetadata.Read(photo).Keywords);
        using var thumbnails = new ThumbnailCache(Path.Combine(_dir.Path, "thumbnails"));
        Assert.True(new FileInfo(await thumbnails.GetAsync(photo, Ct)).Length > 0);
    }

    public void Dispose() => _dir.Dispose();
}
