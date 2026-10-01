using System.Security.Cryptography;
using System.Text;

namespace PhotoTag.Core;

/// <summary>
/// Generates thumbnails on demand and keeps them in an on-disk cache, so a folder only
/// has to be decoded once. Cache entries are keyed on path + size + last-write time, so
/// editing a photo invalidates its thumbnail automatically.
///
/// At most <c>maxConcurrency</c> thumbnails are generated at once; waiting requests can
/// be cancelled (e.g. when the tile scrolls out of view) before they cost anything.
///
/// Nothing is deleted as it goes stale (an edited photo's old thumbnail, a folder since deleted):
/// <see cref="CleanUpAsync"/> removes the least recently used thumbnails once the cache is too big.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly int _size;
    private readonly PhotoRenderer _renderer;
    private readonly SemaphoreSlim _gate;

    public ThumbnailCache(string cacheDirectory, PhotoRenderer? renderer = null, int size = 320, int? maxConcurrency = null)
    {
        _cacheDirectory = cacheDirectory;
        _size = size;
        _renderer = renderer ?? PhotoRenderer.ImagesOnly;
        _gate = new SemaphoreSlim(maxConcurrency ?? Math.Max(2, Environment.ProcessorCount - 1));
        Directory.CreateDirectory(cacheDirectory);
    }

    public static string DefaultDirectory => Path.Combine(AppData.Folder, "thumbnails");

    /// <summary>About 100,000 thumbnails at 320 px (they average 15–25 KB).</summary>
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// A thumbnail's modified time says when it was last used (access times are often switched off). Refreshing
    /// it at most this often means scrolling through a folder doesn't write to every thumbnail it shows.
    /// </summary>
    internal static readonly TimeSpan UsedResolution = TimeSpan.FromDays(1);

    /// <summary>Returns the path of a cached thumbnail file for <paramref name="photoPath"/>, creating it if needed.</summary>
    public async Task<string> GetAsync(string photoPath, CancellationToken cancellationToken = default)
    {
        // Off the caller's (UI) thread: the key needs the photo's size and time, a round trip on a network share.
        var (cachePath, cached) = await Task.Run(() =>
        {
            var path = GetCachePath(photoPath);
            return (path, MarkUsed(path));
        }, cancellationToken).ConfigureAwait(false);
        if (cached) return cachePath;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request may have produced it while we waited.
            if (File.Exists(cachePath)) return cachePath;

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await _renderer.RenderAsync(photoPath, _size, cancellationToken).ConfigureAwait(false);

            // Write-then-rename so a crash never leaves a half-written thumbnail behind.
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, CancellationToken.None).ConfigureAwait(false);
            File.Move(tempPath, cachePath, overwrite: true);
            return cachePath;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether the thumbnail exists; if so, notes that it was used.</summary>
    private static bool MarkUsed(string cachePath)
    {
        var file = new FileInfo(cachePath);
        if (!file.Exists) return false;
        var now = DateTime.UtcNow;
        if (now - file.LastWriteTimeUtc > UsedResolution)
        {
            try
            {
                file.LastWriteTimeUtc = now;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Being read or cleaned up at this moment; it's still there, or will be made again.
            }
        }
        return true;
    }

    /// <summary>
    /// If the cache is bigger than <paramref name="maxBytes"/>, deletes the least recently used thumbnails
    /// until it's back to three quarters of that, so it isn't trimmed again at every start. Thumbnails used
    /// in the last day are kept whatever the size, as a tile may be about to show one. Also removes
    /// half-written files left by a crash.
    /// </summary>
    public Task<CacheCleanUp> CleanUpAsync(long maxBytes = DefaultMaxBytes, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var cutoff = DateTime.UtcNow - UsedResolution;
            // Sizes and times as listed: a FileInfo forgets them once its file is deleted.
            var thumbnails = new List<(FileInfo File, long Size, DateTime LastUsed)>();
            long total = 0, freed = 0;
            var deleted = 0;
            foreach (var file in new DirectoryInfo(_cacheDirectory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (size, lastUsed) = (file.Length, file.LastWriteTimeUtc);
                if (file.Extension == ".thumb")
                {
                    thumbnails.Add((file, size, lastUsed));
                    total += size;
                }
                else if (file.Extension == ".tmp" && lastUsed < cutoff && TryDelete(file))
                {
                    deleted++;
                    freed += size;
                }
            }

            var target = maxBytes / 4 * 3;
            if (total > maxBytes)
            {
                foreach (var (file, size, lastUsed) in thumbnails.OrderBy(t => t.LastUsed))
                {
                    if (total <= target || lastUsed >= cutoff) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryDelete(file)) continue;
                    deleted++;
                    freed += size;
                    total -= size;
                }
            }
            return new CacheCleanUp(deleted, freed, total);
        }, cancellationToken);

    /// <summary>How much space the thumbnails take.</summary>
    public Task<long> MeasureAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => new DirectoryInfo(_cacheDirectory).EnumerateFiles("*.thumb", SearchOption.AllDirectories).Sum(f => f.Length),
            cancellationToken);

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // open for a tile right now, say
        }
    }

    internal string GetCachePath(string photoPath)
    {
        var file = new FileInfo(photoPath);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{_size}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        // Fan out into subfolders so no single directory holds hundreds of thousands of files.
        return Path.Combine(_cacheDirectory, hash[..2], hash + ".thumb");
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>What <see cref="ThumbnailCache.CleanUpAsync"/> did: files deleted, bytes freed, and the thumbnails' size now.</summary>
public sealed record CacheCleanUp(int Deleted, long Freed, long Size);
