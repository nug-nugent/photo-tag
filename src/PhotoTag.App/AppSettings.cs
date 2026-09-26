using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoTag.App;

/// <summary>How the grid orders photos.</summary>
public enum PhotoSort
{
    FileName,
    DateTaken,

    /// <summary>By date taken, with a heading for each day.</summary>
    Days,
}

/// <summary>Per-user settings, stored as JSON next to the thumbnail cache.</summary>
public sealed class AppSettings
{
    public static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTag", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

    public static AppSettings Load(string? filePath = null)
    {
        filePath ??= DefaultFilePath;
        var settings = new AppSettings();
        try
        {
            if (File.Exists(filePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath)) ?? settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file shouldn't stop the app starting.
        }
        settings.FilePath = filePath;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing "last folder" isn't worth interrupting the user over.
        }
    }
}
