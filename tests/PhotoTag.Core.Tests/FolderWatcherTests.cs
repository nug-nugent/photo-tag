using System.Collections.Concurrent;

namespace PhotoTag.Core.Tests;

public sealed class FolderWatcherTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ConcurrentQueue<FolderChanges> _batches = new();
    private readonly FolderWatcher _watcher;

    public FolderWatcherTests()
    {
        _watcher = new FolderWatcher(_dir.Path, quietPeriod: TimeSpan.FromMilliseconds(300));
        _watcher.Changed += (_, changes) => _batches.Enqueue(changes);
    }

    private IEnumerable<string> Reported => _batches.SelectMany(b => b.Paths);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ReportsNewChangedAndDeletedFiles()
    {
        Assert.True(_watcher.IsWatching);
        var photo = Path.Combine(_dir.Path, "a.jpg");

        File.WriteAllBytes(photo, TestImages.Jpeg(16, 16));
        await WaitForAsync(() => Reported.Contains(photo));

        _batches.Clear();
        File.Delete(photo);
        await WaitForAsync(() => Reported.Contains(photo));
    }

    [Fact]
    public async Task ReportsBothNames_WhenAFolderIsRenamed_AndChangesInSubfolders()
    {
        var before = Directory.CreateDirectory(Path.Combine(_dir.Path, "Trip")).FullName;
        var after = Path.Combine(_dir.Path, "Cornwall");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        _batches.Clear();

        Directory.Move(before, after);
        await WaitForAsync(() => Reported.Contains(before) && Reported.Contains(after));

        var inside = Path.Combine(after, "b.jpg");
        File.WriteAllBytes(inside, TestImages.Jpeg(16, 16));
        await WaitForAsync(() => Reported.Contains(inside));
    }

    [Fact]
    public async Task ChangesInANasRecycleBin_AreNotReported()
    {
        var recycle = Directory.CreateDirectory(Path.Combine(_dir.Path, "#recycle", "2020")).FullName;
        await Task.Delay(500, TestContext.Current.CancellationToken);
        _batches.Clear();

        File.WriteAllBytes(Path.Combine(recycle, "deleted.jpg"), TestImages.Jpeg(16, 16));
        var photo = Path.Combine(_dir.Path, "a.jpg");
        File.WriteAllBytes(photo, TestImages.Jpeg(16, 16)); // and something that is reported, to know when to look
        await WaitForAsync(() => Reported.Contains(photo));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Reported, p => p.Contains("#recycle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABurstOfChanges_ArrivesTogether()
    {
        for (var i = 0; i < 30; i++) File.WriteAllBytes(Path.Combine(_dir.Path, $"p{i}.jpg"), TestImages.Jpeg(16, 16));
        await WaitForAsync(() => Reported.Distinct().Count() >= 30);

        Assert.InRange(_batches.Count, 1, 3);
    }

    [Fact]
    public async Task AfterDispose_NothingIsReported()
    {
        _watcher.Dispose();
        File.WriteAllBytes(Path.Combine(_dir.Path, "late.jpg"), TestImages.Jpeg(16, 16));
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        Assert.Empty(_batches);
    }

    [Fact]
    public void LocalFolders_AreNotNetworkDrives()
    {
        Assert.False(PhotoFiles.IsOnNetworkDrive(_dir.Path));
        if (OperatingSystem.IsWindows()) Assert.True(PhotoFiles.IsOnNetworkDrive(@"\\nas\photos\2024"));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _dir.Dispose();
    }
}
