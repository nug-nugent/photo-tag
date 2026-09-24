using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// One tile in the grid. The thumbnail is only loaded while the tile is on screen
/// (<see cref="Realize"/>) and released when it scrolls away (<see cref="Release"/>),
/// so memory stays flat no matter how many photos are in the folder.
/// </summary>
public partial class PhotoItemViewModel(PhotoFile file, int index, ThumbnailCache thumbnails) : ViewModelBase
{
    private CancellationTokenSource? _loading;

    /// <summary>The photo's files: one, or a RAW+JPEG pair (shown and indexed as the JPEG).</summary>
    public PhotoFile File { get; } = file;
    public string Path => File.Path;
    public string FileName { get; } = System.IO.Path.GetFileName(file.Path);

    /// <summary>"RAW", "RAW+JPEG", or null for an ordinary image.</summary>
    public string? Badge { get; } = file.Companions.Count > 0 ? "RAW+JPEG" : PhotoFiles.IsRaw(file.Path) ? "RAW" : null;

    /// <summary>Position in the folder's photo list, for range selection and keyboard moves.</summary>
    public int Index { get; } = index;

    /// <summary>
    /// Metadata last read from (or written to) the file this session, so re-selecting photos
    /// doesn't re-read them. Null when unknown or invalidated by a write.
    /// </summary>
    public PhotoMetadata? Metadata { get; set; }

    /// <summary>Shows a ♥ on the tile. Set from the library index, and straight away by PhotoTag's own edits.</summary>
    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; private set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    [ObservableProperty]
    public partial string? LoadFailedText { get; private set; }

    /// <summary>Called on the UI thread when the tile becomes visible.</summary>
    public async void Realize()
    {
        if (Thumbnail is not null || _loading is not null || LoadFailed) return;

        var cts = _loading = new CancellationTokenSource();
        try
        {
            var file = await thumbnails.GetAsync(Path, cts.Token);
            var bitmap = await Task.Run(() => new Bitmap(file), cts.Token);

            if (cts.IsCancellationRequested) bitmap.Dispose();
            else Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (PreviewUnavailableException e)
        {
            LoadFailedText = e.Message;
            LoadFailed = true;
        }
        catch (Exception)
        {
            LoadFailedText = "Can't read this file";
            LoadFailed = true;
        }
        finally
        {
            if (_loading == cts) _loading = null;
            cts.Dispose();
        }
    }

    /// <summary>Called on the UI thread when the tile is recycled or the folder changes.</summary>
    public void Release()
    {
        _loading?.Cancel();
        _loading = null;

        var bitmap = Thumbnail;
        Thumbnail = null;
        bitmap?.Dispose();
    }
}
