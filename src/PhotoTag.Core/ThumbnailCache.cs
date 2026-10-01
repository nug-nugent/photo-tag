using System.Security.Cryptography;
using System.Text;

namespace PhotoTag.Core;

/// <summary>
/// Generates thumbnails on demand and keeps them in an on-disk cache, so a folder only
/// has to be decoded once. Cache entries are keyed on path + size + last-write time, so
/// editing a photo invalidates its thumbnail automatically.
///
/// Requests are served in the order they're made, which is the grid's order: top to bottom. Each is first
/// looked up in the cache, then if need be rendered, at most <c>maxConcurrency</c> at once. Both stages have
/// their own threads, so a lookup never waits behind a render, and neither waits for the thread pool (which
/// the index scan keeps busy reading photos). Waiting requests can be cancelled (e.g. when the tile scrolls
/// out of view) before they cost anything.
///
/// Nothing is deleted as it goes stale (an edited photo's old thumbnail, a folder since deleted):
/// <see cref="CleanUpAsync"/> removes the least recently used thumbnails once the cache is too big.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly int _size;
    private readonly Func<string, int, CancellationToken, byte[]> _render;
    private readonly Stage _lookups;
    private readonly Stage _renders;
    private long _requests;

    public ThumbnailCache(string cacheDirectory, PhotoRenderer? renderer = null, int size = 320, int? maxConcurrency = null)
        : this(cacheDirectory, (renderer ?? PhotoRenderer.ImagesOnly).Render, size, maxConcurrency)
    {
    }

    /// <summary>For tests: <paramref name="render"/> stands in for the <see cref="PhotoRenderer"/>.</summary>
    internal ThumbnailCache(string cacheDirectory, Func<string, int, CancellationToken, byte[]> render, int size = 320,
        int? maxConcurrency = null)
    {
        _cacheDirectory = cacheDirectory;
        _size = size;
        _render = render;
        _lookups = new Stage("Thumbnail lookup", 2, LookUp);
        _renders = new Stage("Thumbnail render", maxConcurrency ?? Math.Max(2, Environment.ProcessorCount - 1), Render);
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
    public Task<string> GetAsync(string photoPath, CancellationToken cancellationToken = default) =>
        GetAsync(photoPath, null, cancellationToken);

    /// <summary>
    /// Like <see cref="GetAsync(string, CancellationToken)"/>. Given the photo's size and time from its folder's
    /// listing (<see cref="PhotoFile.Listed"/>), a cached thumbnail is found without touching the photo at all.
    /// </summary>
    public async Task<string> GetAsync(string photoPath, FileStamp? listed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new Request(photoPath, listed, Interlocked.Increment(ref _requests), cancellationToken);
        await using (cancellationToken.Register(() => request.Done.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
        {
            _lookups.Add(request);
            return await request.Done.Task.ConfigureAwait(false);
        }
    }

    /// <summary>For tests: how many requests are waiting to be rendered (some perhaps cancelled).</summary>
    internal int RendersWaiting => _renders.Waiting;

    /// <summary>On a lookup thread: answers from the cache, or passes the request on to be rendered.</summary>
    private void LookUp(Request request)
    {
        try
        {
            request.CachePath = GetCachePath(request.PhotoPath, request.Listed);
            if (MarkUsed(request.CachePath)) request.Done.TrySetResult(request.CachePath);
            else _renders.Add(request);
        }
        catch (Exception e)
        {
            request.Done.TrySetException(e);
        }
    }

    /// <summary>On a render thread: makes the thumbnail.</summary>
    private void Render(Request request)
    {
        var cachePath = request.CachePath!;
        try
        {
            // Another request may have produced it while this one waited.
            if (!File.Exists(cachePath))
            {
                var bytes = _render(request.PhotoPath, _size, request.CancellationToken);

                // Write-then-rename so a crash never leaves a half-written thumbnail behind.
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            request.Done.TrySetResult(cachePath);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            request.Done.TrySetCanceled(request.CancellationToken);
        }
        catch (Exception e)
        {
            request.Done.TrySetException(e);
        }
    }

    private sealed class Request(string photoPath, FileStamp? listed, long order, CancellationToken cancellationToken)
    {
        public string PhotoPath { get; } = photoPath;
        public FileStamp? Listed { get; } = listed;
        public long Order { get; } = order;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CachePath { get; set; }
    }

    /// <summary>
    /// Requests waiting for one kind of work, taken oldest first by up to <c>threads</c> threads of its own.
    /// Threads start as they're needed; cancelled requests are skipped when their turn comes.
    /// </summary>
    private sealed class Stage(string name, int threads, Action<Request> work) : IDisposable
    {
        private readonly PriorityQueue<Request, long> _waiting = new();
        private int _started, _idle;
        private bool _disposed;

        public int Waiting
        {
            get
            {
                lock (_waiting) return _waiting.Count;
            }
        }

        public void Add(Request request)
        {
            lock (_waiting)
            {
                if (_disposed)
                {
                    request.Done.TrySetCanceled();
                    return;
                }
                _waiting.Enqueue(request, request.Order);
                if (_idle > 0)
                {
                    // Counted off here, not when it wakes: a second request before then must wake another.
                    _idle--;
                    Monitor.Pulse(_waiting);
                }
                else if (_started < threads)
                {
                    _started++;
                    new Thread(Run) { Name = name, IsBackground = true }.Start();
                }
            }
        }

        private void Run()
        {
            while (Take() is { } request) work(request);
        }

        private Request? Take()
        {
            lock (_waiting)
            {
                while (!_disposed)
                {
                    while (_waiting.TryDequeue(out var request, out _))
                        if (!request.Done.Task.IsCompleted) return request;
                    _idle++;
                    Monitor.Wait(_waiting);
                }
                return null;
            }
        }

        public void Dispose()
        {
            lock (_waiting)
            {
                _disposed = true;
                while (_waiting.TryDequeue(out var request, out _)) request.Done.TrySetCanceled();
                Monitor.PulseAll(_waiting);
            }
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

    internal string GetCachePath(string photoPath, FileStamp? listed = null)
    {
        var stamp = listed ?? FileStamp.Of(new FileInfo(photoPath));
        var key = $"{Path.GetFullPath(photoPath)}|{stamp.Size}|{stamp.Ticks}|{_size}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        // Fan out into subfolders so no single directory holds hundreds of thousands of files.
        return Path.Combine(_cacheDirectory, hash[..2], hash + ".thumb");
    }

    public void Dispose()
    {
        _lookups.Dispose();
        _renders.Dispose();
    }
}

/// <summary>What <see cref="ThumbnailCache.CleanUpAsync"/> did: files deleted, bytes freed, and the thumbnails' size now.</summary>
public sealed record CacheCleanUp(int Deleted, long Freed, long Size);
