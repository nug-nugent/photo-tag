using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// One tile in the grid (and in the viewer's filmstrip). The thumbnail is only loaded while a tile
/// showing it is on screen (<see cref="Realize"/>) and released when the last one scrolls away
/// (<see cref="Release"/>), so memory stays flat no matter how many photos are in the folder.
/// </summary>
public partial class PhotoItemViewModel(PhotoFile file, int index, ThumbnailCache thumbnails) : ViewModelBase
{
    private CancellationTokenSource? _loading;
    private int _uses;

    /// <summary>The photo's files: one, or a RAW+JPEG pair (shown and indexed as the JPEG).</summary>
    public PhotoFile File { get; } = file;
    public string Path => File.Path;
    public string FileName { get; } = System.IO.Path.GetFileName(file.Path);

    /// <summary>"RAW", "RAW+JPEG", or null for an ordinary image.</summary>
    public string? Badge { get; } = file.Companions.Count > 0 ? "RAW+JPEG" : PhotoFiles.IsRaw(file.Path) ? "RAW" : null;

    /// <summary>Position in the grid's photo list (after sorting), for range selection and keyboard moves.</summary>
    public int Index { get; set; } = index;

    /// <summary>Position among the grid's items, which include day headings when grouped by day.</summary>
    public int GridIndex { get; set; } = index;

    /// <summary>
    /// Metadata last read from (or written to) the file this session, so re-selecting photos
    /// doesn't re-read them. Null when unknown or invalidated by a write.
    /// </summary>
    public PhotoMetadata? Metadata { get; set; }

    /// <summary>The tile's ♥. Set from the library index, and straight away by PhotoTag's own edits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouriteToolTip))]
    public partial bool IsFavourite { get; set; }

    public string FavouriteToolTip => IsFavourite ? "Remove from favourites (F)" : "Add to favourites (F)";

    /// <summary>Shown at double size: a favourite, when the grid is grouped by day with favourites highlighted.</summary>
    [ObservableProperty] public partial bool IsFeatured { get; set; }

    /// <summary>Tags from the index, for the tile's coloured dots. Empty until the photo is indexed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUntagged), nameof(TagDots))]
    public partial IReadOnlyList<string> Keywords { get; set; } = [];

    /// <summary>Whether the index has this photo yet: until it does, "no tags" only means "not known".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUntagged))]
    public partial bool IsIndexed { get; set; }

    /// <summary>Marks the tile with a red square, so what still needs tagging stands out.</summary>
    public bool IsUntagged => IsIndexed && Keywords.Count == 0;

    /// <summary>Up to four tags, one dot each.</summary>
    public IReadOnlyList<string> TagDots => Keywords.Count <= 4 ? Keywords : [.. Keywords.Take(4)];

    [ObservableProperty] public partial DateTime? DateTaken { get; set; }

    /// <summary>The photo's title, shown over featured tiles.</summary>
    [ObservableProperty] public partial string? Title { get; set; }

    public string? City { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; private set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    [ObservableProperty]
    public partial string? LoadFailedText { get; private set; }

    /// <summary>Takes what the index knows about the photo.</summary>
    public void Apply(PhotoSummary? summary)
    {
        IsIndexed = summary is not null;
        if (summary is null) return;
        IsFavourite = summary.IsFavourite;
        if (!Keywords.SequenceEqual(summary.Keywords)) Keywords = summary.Keywords;
        DateTaken = summary.DateTaken;
        Title = summary.Title;
        City = summary.City;
    }

    /// <summary>Called on the UI thread when a tile showing this photo becomes visible.</summary>
    public async void Realize()
    {
        _uses++;
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

    /// <summary>Called on the UI thread when a tile showing this photo is recycled.</summary>
    public void Release()
    {
        _uses = Math.Max(0, _uses - 1);
        if (_uses == 0) Unload();
    }

    /// <summary>Frees the thumbnail whatever still shows it: the folder changed.</summary>
    public void Unload()
    {
        _uses = 0;
        _loading?.Cancel();
        _loading = null;

        var bitmap = Thumbnail;
        Thumbnail = null;
        bitmap?.Dispose();
    }
}
