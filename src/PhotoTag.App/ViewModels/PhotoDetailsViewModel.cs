using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MetadataExtractor;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>The side panel for the selected photo: a larger preview plus its metadata.</summary>
public partial class PhotoDetailsViewModel(PhotoItemViewModel photo) : ViewModelBase, IDisposable
{
    private const int PreviewSize = 1200;
    private readonly CancellationTokenSource _cts = new();

    public PhotoItemViewModel Photo { get; } = photo;
    public string FileName => Photo.FileName;
    public string? Folder => System.IO.Path.GetDirectoryName(Photo.Path);

    [ObservableProperty] public partial Bitmap? Preview { get; private set; }
    [ObservableProperty] public partial string? Error { get; private set; }
    [ObservableProperty] public partial string? DateTaken { get; private set; }
    [ObservableProperty] public partial string? Camera { get; private set; }
    [ObservableProperty] public partial string? Lens { get; private set; }
    [ObservableProperty] public partial string? Exposure { get; private set; }
    [ObservableProperty] public partial string? Dimensions { get; private set; }
    [ObservableProperty] public partial string? Title { get; private set; }
    [ObservableProperty] public partial string? Description { get; private set; }
    [ObservableProperty] public partial string? Rating { get; private set; }
    [ObservableProperty] public partial string? Location { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<string> Keywords { get; private set; } = [];

    public async Task LoadAsync()
    {
        var token = _cts.Token;
        var path = Photo.Path;

        // Start both at once; metadata is usually ready well before the preview.
        var metadataTask = Task.Run(() => (Metadata: PhotoMetadata.Read(path), Size: new FileInfo(path).Length), token);
        var previewTask = Task.Run(() => new Bitmap(new MemoryStream(ImageRenderer.Render(path, PreviewSize))), token);

        try
        {
            var (metadata, size) = await metadataTask;
            if (!token.IsCancellationRequested) Apply(metadata, size);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is ImageProcessingException or IOException or InvalidDataException)
        {
            Error = "This file looks damaged, or isn't an image PhotoTag can read.";
        }
        catch (Exception e)
        {
            Error = $"Couldn't read metadata: {e.Message}";
        }

        try
        {
            var preview = await previewTask;
            if (token.IsCancellationRequested) preview.Dispose();
            else Preview = preview;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Error ??= $"Couldn't open image: {e.Message}";
        }
    }

    private void Apply(PhotoMetadata m, long fileSize)
    {
        var culture = CultureInfo.CurrentCulture;

        DateTaken = m.DateTaken?.ToString("dddd d MMMM yyyy, HH:mm", culture);
        Camera = m.Camera;
        Lens = m.LensModel;
        Exposure = JoinNonEmpty(" · ", m.ExposureTime, m.FNumber, m.Iso is { } iso ? $"ISO {iso}" : null, m.FocalLength);
        Dimensions = JoinNonEmpty(" · ",
            m.Width is { } w && m.Height is { } h ? $"{w:N0} × {h:N0}" : null,
            FormatBytes(fileSize));
        Title = m.Title;
        Description = m.Description;
        Rating = m.Rating is > 0 and <= 5 ? new string('★', m.Rating.Value) + new string('☆', 5 - m.Rating.Value) : null;
        Location = m.Latitude is { } lat && m.Longitude is { } lon
            ? string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lon:F5}")
            : null;
        Keywords = m.Keywords;
    }

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var joined = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return joined.Length == 0 ? null : joined;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} bytes",
    };

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        Preview?.Dispose();
        Preview = null;
    }
}
