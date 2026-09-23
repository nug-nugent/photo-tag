using System.Text.Json;
using SkiaSharp;

namespace PhotoTag.Core;

/// <summary>Why a photo can't be shown, in words fit for the user.</summary>
public sealed class PreviewUnavailableException(string message) : Exception(message);

/// <summary>
/// Renders any supported photo at a given size. Ordinary images are decoded directly; camera RAW
/// files are shown via the full-colour JPEG preview the camera embeds in them (as Lightroom's
/// "embedded preview" does), extracted by ExifTool. Without ExifTool, RAW files can't be shown.
/// </summary>
public sealed class PhotoRenderer(RawPreviewExtractor? rawPreviews)
{
    /// <summary>A renderer for ordinary images only.</summary>
    public static PhotoRenderer ImagesOnly { get; } = new(null);

    public bool CanRenderRaw => rawPreviews is not null;

    /// <summary>Encoded bytes (JPEG, or PNG with alpha) whose longest edge is at most <paramref name="maxSize"/>.</summary>
    public async Task<byte[]> RenderAsync(string path, int maxSize, CancellationToken cancellationToken = default)
    {
        if (!PhotoFiles.IsRaw(path))
            return await Task.Run(() => ImageRenderer.Render(path, maxSize), cancellationToken).ConfigureAwait(false);

        if (rawPreviews is null)
            throw new PreviewUnavailableException("Showing RAW files needs ExifTool.");

        var preview = await rawPreviews.ExtractAsync(path, maxSize, cancellationToken).ConfigureAwait(false)
                      ?? throw new PreviewUnavailableException("This RAW file has no preview PhotoTag can show.");

        return await Task.Run(() => ImageRenderer.Render(preview.Jpeg, maxSize, preview.Orientation), cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed record RawPreview(byte[] Jpeg, int Width, int Height, SKEncodedOrigin Orientation);

/// <summary>
/// Pulls the JPEG previews a camera embeds in its RAW files. Cameras store one to three, of
/// very different sizes (a 640 px preview plus a full-size copy is common); this picks the
/// smallest that is at least as big as needed, so thumbnails stay quick.
/// </summary>
public sealed class RawPreviewExtractor(ExifTool exifTool)
{
    // ExifTool's names for embedded JPEGs across formats. ThumbnailImage (160 px) is too small to use.
    private static readonly string[] PreviewTags = ["PreviewImage", "OtherImage", "JpgFromRaw"];

    public async Task<RawPreview?> ExtractAsync(string path, int minSize, CancellationToken cancellationToken = default)
    {
        List<string> args = ["-charset", "filename=utf8", "-json", "-b", "-Orientation#"];
        args.AddRange(PreviewTags.Select(t => "-" + t));
        args.Add(path);

        var json = await exifTool.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement[0];

        var orientation = result.TryGetProperty("Orientation", out var o) && o.TryGetInt32(out var value) && value is >= 1 and <= 8
            ? (SKEncodedOrigin)value
            : SKEncodedOrigin.TopLeft;

        var previews = new List<RawPreview>();
        foreach (var tag in PreviewTags)
        {
            if (!result.TryGetProperty(tag, out var property) || property.GetString() is not { } text) continue;
            if (!text.StartsWith("base64:", StringComparison.Ordinal)) continue;

            var bytes = Convert.FromBase64String(text["base64:".Length..]);
            using var codec = SKCodec.Create(new MemoryStream(bytes)); // reads the header only
            if (codec is null) continue;
            previews.Add(new RawPreview(bytes, codec.Info.Width, codec.Info.Height, orientation));
        }

        static int Longest(RawPreview p) => Math.Max(p.Width, p.Height);
        return previews.Where(p => Longest(p) >= minSize).MinBy(Longest)
               ?? previews.MaxBy(Longest);
    }
}
