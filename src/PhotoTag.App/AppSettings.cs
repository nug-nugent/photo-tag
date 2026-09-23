using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoTag.App;

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
