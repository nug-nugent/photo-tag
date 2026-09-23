using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoTag.Core.Tests;

/// <summary>
/// Real camera RAW files for tests: public-domain (CC0) samples from raw.pixls.us, listed in
/// tests/samples/raw-samples.json. They're downloaded on first use into tests/.samples (not
/// committed) and checked against their SHA-256. If they can't be downloaded the tests skip,
/// unless PHOTOTAG_REQUIRE_SAMPLES=1 (set in CI), when they fail.
/// </summary>
internal static class RawSamples
{
    public const string CanonCr3 = "Canon-EOS-R6.cr3";
    public const string CanonCr2 = "Canon-EOS-40D.cr2";
    public const string NikonNef = "Nikon-Z5-2.nef";
    public const string SonyArw = "Sony-ILCE-7S.arw";
    public const string FujiRaf = "Fujifilm-X-S10.raf";
    public const string RicohDng = "Ricoh-GR.dng";
    public const string PanasonicRw2 = "Panasonic-DMC-LX7.rw2";

    public static readonly string[] All = [CanonCr3, CanonCr2, NikonNef, SonyArw, FujiRaf, RicohDng, PanasonicRw2];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Downloads = new();

    private sealed record Sample(string File, string Url, string Sha256);

    /// <summary>
    /// Copies the sample into <paramref name="directory"/> (tests may modify it) and returns the
    /// copy's path. <paramref name="name"/> renames it, keeping or replacing the extension as given.
    /// </summary>
    public static async Task<string> CopyAsync(string sample, string directory, string? name = null)
    {
        var cached = await GetAsync(sample);
        var destination = Path.Combine(directory, name ?? sample);
        File.Copy(cached, destination, overwrite: true);
        return destination;
    }

    private static Task<string> GetAsync(string sample) =>
        Downloads.GetOrAdd(sample, s => new Lazy<Task<string>>(() => DownloadAsync(s))).Value;

    private static async Task<string> DownloadAsync(string file)
    {
        var sample = Manifest.Value.Single(s => s.File == file);
        var cacheDir = Environment.GetEnvironmentVariable("PHOTOTAG_SAMPLES_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(RepoRoot(), "tests", ".samples");
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, file);

        if (File.Exists(path) && await Sha256Async(path) == sample.Sha256) return path;

        try
        {
            var bytes = await Http.GetByteArrayAsync(new Uri(sample.Url));
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sample.Sha256)
                throw new InvalidDataException($"{file} downloaded, but its checksum doesn't match the manifest.");
            var temp = path + ".download";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (Environment.GetEnvironmentVariable("PHOTOTAG_REQUIRE_SAMPLES") == "1") throw;
            Assert.Skip($"Couldn't download the RAW sample {file}: {e.Message}");
            throw; // unreachable
        }
    }

    private static readonly Lazy<IReadOnlyList<Sample>> Manifest = new(() =>
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "raw-samples.json"));
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("samples").EnumerateArray().Select(s => new Sample(
            s.GetProperty("file").GetString()!, s.GetProperty("url").GetString()!, s.GetProperty("sha256").GetString()!))];
    });

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PhotoTag.slnx"))) return dir.FullName;
        return Path.GetTempPath(); // not in a checkout: cache in temp
    }
}
