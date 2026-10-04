using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// The large viewer: one photo fills the window, ← → step through the grid's photos (or just the
/// selected ones), F favourites and T jumps to the tags. The photo shown is also the selected one,
/// so the tags beside it are the details panel's. Full screen, the photo can be zoomed into and dragged around.
/// </summary>
public partial class ViewerViewModel : ViewModelBase, IDisposable
{
    /// <summary>Big enough for a full-screen photo on most displays.</summary>
    private const int PreviewSize = 2560;

    /// <summary>Zoomed in, the photo at (up to) full size, so it stays sharp.</summary>
    private const int FullSize = 8192;

    /// <summary>How far in the photo can go: 8x its full-screen size.</summary>
    public const double MaxZoom = 8;

    private int _previewSize;

    private readonly MainWindowViewModel _owner;
    private readonly PhotoRenderer _renderer;
    private CancellationTokenSource? _previewLoad;
    private int _index;

    public ViewerViewModel(MainWindowViewModel owner, IReadOnlyList<PhotoItemViewModel> photos, PhotoItemViewModel start,
        PhotoRenderer renderer, string title)
    {
        _owner = owner;
        _renderer = renderer;
        Photos = photos;
        Title = title;
        DateRange = FormatRange(photos.Where(p => p.DateTaken is not null).Select(p => p.DateTaken!.Value).ToList());
        foreach (var photo in photos) photo.PropertyChanged += OnPhotoChanged;
        CountPicked();
        _index = IndexOf(start);
        Current = start;
        start.Realize(); // the thumbnail stands in until the preview is ready
        _owner.Select(start);
        _ = LoadPreviewAsync(start, PreviewSize);
    }

    public IReadOnlyList<PhotoItemViewModel> Photos { get; }

    /// <summary>The folder or search being viewed.</summary>
    public string Title { get; }

    /// <summary>When its photos were taken: "Mon 12 – Thu 15 Aug 2019".</summary>
    public string? DateRange { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(Progress))]
    public partial PhotoItemViewModel Current { get; private set; }

    /// <summary>The photo at screen size; until it's ready, the thumbnail shows.</summary>
    [ObservableProperty] public partial Bitmap? Preview { get; private set; }

    /// <summary>Where <see cref="Current"/> is in <see cref="Photos"/>.</summary>
    public int CurrentIndex => _index;

    public string PositionText => $"{_index + 1:N0} of {Photos.Count:N0}";

    /// <summary>How far through, 0–100, for the bar at the top.</summary>
    public double Progress => 100.0 * (_index + 1) / Photos.Count;

    /// <summary>"6 picked": favourites among the photos being viewed.</summary>
    [ObservableProperty] public partial string PickedText { get; private set; } = "";

    /// <summary>Just the photo, filling the screen: a click on the photo goes in and out, Esc comes out.</summary>
    [ObservableProperty] public partial bool IsFullScreen { get; set; }

    /// <summary>How many times its full-screen size the photo is shown: 1 fits the screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZoomed))]
    public partial double Zoom { get; private set; } = 1;

    public bool IsZoomed => Zoom > 1;

    /// <summary>How far the zoomed photo's centre is from the screen's, in screen pixels.</summary>
    [ObservableProperty] public partial Vector Pan { get; private set; }

    partial void OnIsFullScreenChanged(bool value)
    {
        if (!value) ResetZoom();
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/>, keeping the point <paramref name="fromCentre"/> (from the centre of the
    /// photo as it fits the screen) where it is, as a mouse wheel does around the pointer.
    /// </summary>
    /// <param name="fitted">The photo's size when it fits the screen.</param>
    /// <param name="screen">The space it's shown in.</param>
    public void ZoomBy(double factor, Vector fromCentre, Size fitted, Size screen)
    {
        if (!IsFullScreen) return;
        var zoom = Math.Clamp(Zoom * factor, 1, MaxZoom);
        if (zoom == Zoom) return;

        // The point of the photo under fromCentre stays under it.
        var pan = fromCentre - (fromCentre - Pan) * (zoom / Zoom);
        Zoom = zoom;
        Pan = Clamp(pan, fitted, screen);
        if (IsZoomed && _previewSize < FullSize) _ = LoadPreviewAsync(Current, FullSize, keepShown: true);
    }

    /// <summary>Drags the zoomed photo, but not so far that it leaves a gap at an edge.</summary>
    public void PanBy(Vector delta, Size fitted, Size screen)
    {
        if (IsZoomed) Pan = Clamp(Pan + delta, fitted, screen);
    }

    /// <summary>Back to the whole photo.</summary>
    public void ResetZoom()
    {
        Zoom = 1;
        Pan = default;
    }

    private Vector Clamp(Vector pan, Size fitted, Size screen)
    {
        // How far the zoomed photo overhangs the screen either side; one that doesn't stays in the middle.
        var x = Math.Max(0, (fitted.Width * Zoom - screen.Width) / 2);
        var y = Math.Max(0, (fitted.Height * Zoom - screen.Height) / 2);
        return new Vector(Math.Clamp(pan.X, -x, x), Math.Clamp(pan.Y, -y, y));
    }

    /// <summary>← and → (and the buttons either side of the photo).</summary>
    [RelayCommand]
    private void Previous() => Move(-1);

    [RelayCommand]
    private void Next() => Move(1);

    public void Move(int delta)
    {
        var index = Math.Clamp(_index + delta, 0, Photos.Count - 1);
        Show(Photos[index]);
    }

    /// <summary>A click in the filmstrip.</summary>
    [RelayCommand]
    public void Show(PhotoItemViewModel photo)
    {
        if (Current == photo) return;

        var previous = Current;
        ResetZoom();
        _index = IndexOf(photo);
        Current = photo;
        photo.Realize();
        previous.Release();
        _owner.Select(photo);
        _ = LoadPreviewAsync(photo, PreviewSize);
    }

    /// <param name="keepShown">Keep the preview on screen until the bigger one is ready.</param>
    private async Task LoadPreviewAsync(PhotoItemViewModel photo, int size, bool keepShown = false)
    {
        _previewLoad?.Cancel();
        _previewLoad?.Dispose();
        var cts = _previewLoad = new CancellationTokenSource();
        _previewSize = size;
        if (!keepShown)
        {
            var old = Preview;
            Preview = null;
            old?.Dispose();
        }

        try
        {
            var token = cts.Token;
            var bitmap = await Task.Run(async () =>
                new Bitmap(new MemoryStream(await _renderer.RenderAsync(photo.Path, size, token))), token);
            if (cts.IsCancellationRequested) bitmap.Dispose();
            else
            {
                var old = Preview;
                Preview = bitmap;
                old?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // The thumbnail (or its "can't read" message) stays.
            Log.Warn($"Couldn't show {photo.Path}", e);
        }
    }

    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoItemViewModel.IsFavourite)) CountPicked();
    }

    private void CountPicked() => PickedText = $"{Photos.Count(p => p.IsFavourite):N0} picked";

    private int IndexOf(PhotoItemViewModel photo)
    {
        // The viewer's list is the grid's when viewing everything, or just the selected photos.
        if (photo.Index < Photos.Count && Photos[photo.Index] == photo) return photo.Index;
        for (var i = 0; i < Photos.Count; i++)
            if (Photos[i] == photo) return i;
        return 0;
    }

    private static string? FormatRange(IReadOnlyList<DateTime> dates)
    {
        if (dates.Count == 0) return null;
        var culture = CultureInfo.CurrentCulture;
        var (first, last) = (dates.Min(), dates.Max());
        if (first.Date == last.Date) return first.ToString("ddd d MMM yyyy", culture);
        if (first.Year != last.Year) return $"{first.ToString("d MMM yyyy", culture)} – {last.ToString("d MMM yyyy", culture)}";
        return first.Month == last.Month
            ? $"{first.ToString("ddd d", culture)} – {last.ToString("ddd d MMM yyyy", culture)}"
            : $"{first.ToString("ddd d MMM", culture)} – {last.ToString("ddd d MMM yyyy", culture)}";
    }

    public void Dispose()
    {
        foreach (var photo in Photos) photo.PropertyChanged -= OnPhotoChanged;
        Current.Release();
        _previewLoad?.Cancel();
        _previewLoad?.Dispose();
        _previewLoad = null;
        Preview?.Dispose();
        Preview = null;
    }
}

