using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>The settings window: saving tags, the thumbnail cache, updates, and the log. Each change is saved straight away.</summary>
public partial class SettingsViewModel(AppSettings settings, PhotoMetadataWriter? writer, ThumbnailCache thumbnails,
    UpdatesViewModel updates) : ViewModelBase
{
    private const long MB = 1024 * 1024, GB = 1024 * MB;

    /// <summary>The sizes the thumbnail cache can be limited to, with <see cref="ThumbnailLimitLabels"/> to match.</summary>
    public static IReadOnlyList<long> ThumbnailLimits { get; } = [500 * MB, 1 * GB, 2 * GB, 5 * GB, 10 * GB, 20 * GB, 50 * GB];

    public static IReadOnlyList<string> ThumbnailLimitLabels { get; } = [.. ThumbnailLimits.Select(FormatSize)];

    private readonly SemaphoreSlim _cleaning = new(1, 1);

    public UpdatesViewModel Updates { get; } = updates;

    // --- Saving tags ---------------------------------------------------------------------

    public bool CanEditTags => writer is not null;

    /// <summary>Whether saving tags keeps each photo's "date modified". See PhotoMetadataWriter.PreserveModifiedTime.</summary>
    public bool PreserveModifiedTime
    {
        get => settings.PreserveModifiedTime;
        set
        {
            if (value == settings.PreserveModifiedTime) return;
            settings.PreserveModifiedTime = value;
            if (writer is not null) writer.PreserveModifiedTime = value;
            settings.Save();
            OnPropertyChanged();
        }
    }

    // --- Thumbnails ----------------------------------------------------------------------

    /// <summary>
    /// Which of <see cref="ThumbnailLimits"/> is chosen (the nearest, if settings.json says something else).
    /// Lowering it deletes thumbnails straight away.
    /// </summary>
    public int ThumbnailLimitIndex
    {
        get
        {
            var limit = settings.ThumbnailCacheLimit;
            return ThumbnailLimits.Select((l, i) => (Distance: Math.Abs(l - limit), Index: i)).MinBy(x => x.Distance).Index;
        }
        set
        {
            if (value < 0 || value >= ThumbnailLimits.Count || ThumbnailLimits[value] == settings.ThumbnailCacheLimit) return;
            settings.ThumbnailCacheLimit = ThumbnailLimits[value];
            settings.Save();
            OnPropertyChanged();
            Log.Info($"Thumbnail cache limit set to {FormatSize(ThumbnailLimits[value])}");
            _ = CleanUpThumbnailsAsync();
        }
    }

    /// <summary>"Thumbnails take 340 MB at the moment." Null until measured.</summary>
    [ObservableProperty] public partial string? ThumbnailSizeText { get; private set; }

    /// <summary>When the window opens.</summary>
    public async Task MeasureThumbnailsAsync()
    {
        try
        {
            ThumbnailSizeText = SizeText(await thumbnails.MeasureAsync());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Couldn't measure the thumbnail cache", e);
        }
    }

    /// <summary>At startup, and when the limit changes: deletes the least recently used thumbnails if there are too many.</summary>
    public async Task CleanUpThumbnailsAsync()
    {
        await _cleaning.WaitAsync();
        try
        {
            var result = await thumbnails.CleanUpAsync(settings.ThumbnailCacheLimit);
            if (result.Deleted > 0)
                Log.Info($"Cleaned up the thumbnail cache: deleted {result.Deleted:N0} files ({FormatSize(result.Freed)}), " +
                         $"{FormatSize(result.Size)} left");
            ThumbnailSizeText = SizeText(result.Size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Couldn't clean up the thumbnail cache", e);
        }
        finally
        {
            _cleaning.Release();
        }
    }

    private static string SizeText(long bytes) => $"Thumbnails take {FormatSize(bytes)} at the moment.";

    /// <summary>"less than 1 MB", "340 MB", "1 GB", "2.5 GB".</summary>
    internal static string FormatSize(long bytes) => bytes switch
    {
        < MB => "less than 1 MB",
        < GB => $"{Math.Round((double)bytes / MB):N0} MB",
        _ => ((double)bytes / GB).ToString("0.#", CultureInfo.CurrentCulture) + " GB",
    };

    // --- Problems ------------------------------------------------------------------------

    /// <summary>PhotoTag's log file, for "Show log"; null when there isn't one (tests, tools).</summary>
    public string? LogPath { get; set; }

    public Uri? LogUri => LogPath is null ? null : new Uri(LogPath);
}
