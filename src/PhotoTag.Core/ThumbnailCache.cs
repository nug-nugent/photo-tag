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

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoTag", "thumbnails");

    /// <summary>Returns the path of a cached thumbnail file for <paramref name="photoPath"/>, creating it if needed.</summary>
    public async Task<string> GetAsync(string photoPath, CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(photoPath);
        if (File.Exists(cachePath)) return cachePath;

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
