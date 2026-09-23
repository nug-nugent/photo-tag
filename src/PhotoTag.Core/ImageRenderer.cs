using SkiaSharp;

namespace PhotoTag.Core;

/// <summary>
/// Decodes an image at (roughly) the size it will be displayed, applies its EXIF
/// orientation, and re-encodes it. Uses the codec's native downscaling (JPEG DCT
/// scaling), so a 24MP photo is never fully decoded just to show a thumbnail.
/// </summary>
public static class ImageRenderer
{
    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// Renders <paramref name="path"/> so its longest edge is at most <paramref name="maxSize"/>
    /// pixels, correctly oriented. Returns encoded bytes (JPEG, or PNG when the source has alpha).
    /// </summary>
    public static byte[] Render(string path, int maxSize, int jpegQuality = 85)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 1);

        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException($"Unsupported or corrupt image: {path}");

        using var decoded = Decode(codec, maxSize);
        using var resized = FitWithin(decoded, maxSize);
        using var oriented = ApplyOrientation(resized, codec.EncodedOrigin);

        var hasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque;
        using var data = oriented.Encode(
            hasAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
            jpegQuality);
        return data.ToArray();
    }

    private static SKBitmap Decode(SKCodec codec, int maxSize)
    {
        var info = codec.Info;
        var scaled = ChooseDecodeSize(codec, Math.Min(maxSize, Math.Max(info.Width, info.Height)));
        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888,
            info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);

        var bitmap = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, bitmap.GetPixels());

        // IncompleteInput = truncated file; show what we have rather than nothing.
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Could not decode image ({result}).");
        }

        return bitmap;
    }

    /// <summary>
    /// JPEG can decode cheaply at 1/8, 2/8 ... 8/8 scale, and the codec rounds to the
    /// *nearest* of those, which can undershoot. Pick the smallest one that is still at
    /// least <paramref name="target"/> pixels on the longest edge; we fine-tune afterwards.
    /// </summary>
    private static SKSizeI ChooseDecodeSize(SKCodec codec, int target)
    {
        for (var eighths = 1; eighths < 8; eighths++)
        {
            var size = codec.GetScaledDimensions(eighths / 8f);
            if (Math.Max(size.Width, size.Height) >= target) return size;
        }
        return codec.Info.Size;
    }

    private static SKBitmap FitWithin(SKBitmap source, int maxSize)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxSize) return source.Copy();

        var ratio = (double)maxSize / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        return source.Resize(source.Info.WithSize(width, height), Sampling)
            ?? throw new InvalidOperationException("Resize failed.");
    }

    /// <summary>Rotates/flips so the pixels are upright (EXIF orientation 1).</summary>
    internal static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return source.Copy();

        int w = source.Width, h = source.Height;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var result = new SKBitmap(source.Info.WithSize(swapsAxes ? h : w, swapsAxes ? w : h));
        using var canvas = new SKCanvas(result);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // mirror horizontal
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight: // rotate 180
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft: // mirror vertical
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop: // transpose
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop: // rotate 90 clockwise
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // transverse
                canvas.Translate(h, w);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom: // rotate 90 anti-clockwise
                canvas.Translate(0, w);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
