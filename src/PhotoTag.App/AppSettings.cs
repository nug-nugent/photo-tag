using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoTag.Core;

namespace PhotoTag.App;

/// <summary>How the grid orders photos.</summary>
public enum PhotoSort
{
    FileName,

    /// <summary>By date taken, the most recent first; photos with no date go last.</summary>
    DateTaken,

    /// <summary>By date taken, the most recent first, with a heading for each day.</summary>
    Days,
}

/// <summary>Per-user settings, stored as JSON next to the thumbnail cache.</summary>
public sealed class AppSettings
{
    public static readonly string DefaultFilePath = Path.Combine(AppData.Folder, "settings.json");

    /// <summary>Where <see cref="Save"/> writes. Tests point this at a temp file.</summary>
    [JsonIgnore]
    public string FilePath { get; private set; } = DefaultFilePath;

    public string? LastFolder { get; set; }

    /// <summary>Keep photos' "date modified" when saving tags. See PhotoMetadataWriter.PreserveModifiedTime.</summary>
    public bool PreserveModifiedTime { get; set; }

    /// <summary>Check GitHub Releases for a new version at startup (installed copies only).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>How the grid is ordered: by file name, date taken, or grouped by day.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PhotoSort>))]
    public PhotoSort Sort { get; set; } = PhotoSort.FileName;

    /// <summary>When grouped by day, show favourites at double size.</summary>
    public bool HighlightFavourites { get; set; } = true;

    /// <summary>A folder shows the photos in its subfolders too.</summary>
    public bool IncludeSubfolders { get; set; }

    /// <summary>How big the thumbnail cache may grow before the least recently used thumbnails are deleted.</summary>
    public long ThumbnailCacheLimit { get; set; } = ThumbnailCache.DefaultMaxBytes;

    /// <summary>The people last added to a photo, most recent first. See ViewModels.RecentNames.</summary>
    public List<string> RecentPeople { get; set; } = [];

    /// <summary>The tags last added to a photo, most recent first. See ViewModels.RecentNames.</summary>
    public List<string> RecentTags { get; set; } = [];

    public static AppSettings Load(string? filePath = null)
    {
        filePath ??= DefaultFilePath;
        var settings = new AppSettings();
        try
        {
            if (File.Exists(filePath))
                settings = JsonSerializer.Deserialize(File.ReadAllText(filePath), AppJsonContext.Default.AppSettings) ?? settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file shouldn't stop the app starting.
            Log.Warn($"Couldn't read settings from {filePath}; using the defaults", e);
        }
        settings.FilePath = filePath;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing "last folder" isn't worth interrupting the user over.
            Log.Warn($"Couldn't save settings to {FilePath}", e);
        }
    }
}

/// <summary>
/// The JSON PhotoTag reads and writes, generated at compile time: release builds are trimmed, which
/// can remove the members reflection-based serialization would look for.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
