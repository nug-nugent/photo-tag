using System.Buffers.Binary;
using SkiaSharp;

namespace PhotoTag.Core;

/// <summary>
/// The small thumbnail (usually 160 px) that cameras and phones put in a JPEG's EXIF block, near the start of the
/// file. Reading it takes one short read, where rendering a thumbnail reads the whole photo (several MB, which is
/// slow over a share), so the grid shows it, scaled up, until the real thumbnail is ready.
/// </summary>
public static class EmbeddedThumbnail
{
    /// <summary>The EXIF block is one APP1 segment of at most 64 KB, and comes first (after JFIF's few bytes, if any).</summary>
    private const int HeadSize = 64 * 1024;

    /// <summary>
    /// The thumbnail, upright and in the photo's proportions, encoded as JPEG; null if the photo isn't a JPEG or
    /// has no thumbnail fit to show.
    /// </summary>
    public static byte[]? Read(string path)
    {
        if (!PhotoFiles.IsJpeg(path)) return null;
        using var stream = PhotoFiles.OpenRead(path);
        return FromHead(ReadHead(stream));
    }

    private static byte[] ReadHead(Stream stream)
    {
        var head = new byte[HeadSize];
        var length = stream.ReadAtLeast(head, HeadSize, throwOnEndOfStream: false);

        // An EXIF block that starts in the first 64 KB may end just past it.
        var layout = Walk(head.AsSpan(0, length));
        var exifEnd = layout.ExifStart + layout.ExifLength;
        if (layout.ExifStart >= 0 && exifEnd > length && length == HeadSize)
        {
            Array.Resize(ref head, exifEnd);
            length += stream.ReadAtLeast(head.AsSpan(length), exifEnd - length, throwOnEndOfStream: false);
        }
        return length == head.Length ? head : head[..length];
    }

    /// <summary>The thumbnail in <paramref name="head"/>, the start of a JPEG file, as <see cref="Read"/> returns it.</summary>
    internal static byte[]? FromHead(byte[] head)
    {
        var layout = Walk(head);
        if (layout.ExifStart < 0 || layout.ExifStart + layout.ExifLength > head.Length) return null;
        var tiff = head.AsSpan(layout.ExifStart, layout.ExifLength);

        if (tiff.Length < 8) return null;
        bool le;
        if (tiff[..2].SequenceEqual("II"u8)) le = true;
        else if (tiff[..2].SequenceEqual("MM"u8)) le = false;
        else return null;
        if (U16(tiff, 2, le) != 42) return null;

        var ifd0 = ReadIfd(tiff, le, U32(tiff, 4, le), out var next);
        if (ifd0 is null || next == 0) return null;
        var ifd1 = ReadIfd(tiff, le, next, out _);
        if (ifd1 is null || !ifd1.TryGetValue(JpegOffset, out var start) || !ifd1.TryGetValue(JpegLength, out var length)) return null;
        if (length == 0 || start + (long)length > tiff.Length) return null;

        var origin = ifd0.TryGetValue(Orientation, out var o) && o is >= 1 and <= 8 ? (SKEncodedOrigin)o : SKEncodedOrigin.TopLeft;

        // The photo's size, to put the thumbnail in its proportions: from the frame header if the head reached it,
        // or else from EXIF (which an editor may not have kept up to date).
        var (width, height) = (layout.Width, layout.Height);
        if (width == 0 && ifd0.TryGetValue(ExifIfd, out var exifOffset) && ReadIfd(tiff, le, exifOffset, out _) is { } exif
            && exif.TryGetValue(PixelWidth, out var w) && exif.TryGetValue(PixelHeight, out var h))
            (width, height) = ((int)Math.Min(w, int.MaxValue), (int)Math.Min(h, int.MaxValue));

        using var data = SKData.CreateCopy(tiff.Slice((int)start, (int)length));
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        using var thumbnail = SKBitmap.Decode(codec);
        if (thumbnail is null) return null;
        using var proportioned = Proportion(thumbnail, width, height);
        if (proportioned is null) return null;
        using var upright = ImageRenderer.ApplyOrientation(proportioned, origin);
        using var encoded = upright.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded?.ToArray();
    }

    private const ushort Orientation = 0x0112, ExifIfd = 0x8769, PixelWidth = 0xA002, PixelHeight = 0xA003,
        JpegOffset = 0x0201, JpegLength = 0x0202;

    /// <summary>Where the EXIF block's TIFF data is in the file (-1 if not found), and the photo's size if the frame header was reached.</summary>
    private readonly record struct Layout(int ExifStart, int ExifLength, int Width, int Height);

    /// <summary>Walks the JPEG's segments, as far as <paramref name="head"/> goes.</summary>
    private static Layout Walk(ReadOnlySpan<byte> head)
    {
        var layout = new Layout(-1, 0, 0, 0);
        if (head.Length < 4 || head[0] != 0xFF || head[1] != 0xD8) return layout;

        var at = 2;
        while (at + 4 <= head.Length && head[at] == 0xFF)
        {
            var marker = head[at + 1];
            if (marker == 0xFF)
            {
                at++; // fill byte
                continue;
            }
            if (marker is 0xD9 or 0xDA) break; // end of image, or the image data itself

            var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(head[(at + 2)..]) - 2;
            if (bodyLength < 0) break;
            var body = at + 4;

            if (marker == 0xE1 && layout.ExifStart < 0 && bodyLength >= 6 && body + 6 <= head.Length
                && head.Slice(body, 6).SequenceEqual("Exif\0\0"u8))
            {
                layout = layout with { ExifStart = body + 6, ExifLength = bodyLength - 6 };
            }
            else if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) // a start of frame
            {
                if (body + 5 <= head.Length)
                    layout = layout with
                    {
                        Height = BinaryPrimitives.ReadUInt16BigEndian(head[(body + 1)..]),
                        Width = BinaryPrimitives.ReadUInt16BigEndian(head[(body + 3)..]),
                    };
                break;
            }
            at = body + bodyLength;
        }
        return layout;
    }

    /// <summary>The single SHORT and LONG values in the IFD at <paramref name="offset"/>, by tag; null if it's damaged.</summary>
    private static Dictionary<ushort, uint>? ReadIfd(ReadOnlySpan<byte> tiff, bool le, uint offset, out uint next)
    {
        next = 0;
        if (offset < 8 || offset > tiff.Length - 2) return null;
        var count = U16(tiff, (int)offset, le);
        var end = (int)offset + 2 + count * 12;
        if (end > tiff.Length - 4) return null;

        var values = new Dictionary<ushort, uint>();
        for (var at = (int)offset + 2; at < end; at += 12)
        {
            if (U32(tiff, at + 4, le) != 1) continue;
            var type = U16(tiff, at + 2, le);
            if (type == 3) values[U16(tiff, at, le)] = U16(tiff, at + 8, le);
            else if (type == 4) values[U16(tiff, at, le)] = U32(tiff, at + 8, le);
        }
        next = U32(tiff, end, le);
        return values;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);

    /// <summary>
    /// The thumbnail in the proportions of the photo (<paramref name="width"/> × <paramref name="height"/> as stored,
    /// before orientation). Most cameras make it 160 × 120 whatever the photo's shape and fill the rest with black
    /// bars, which are cut off; one stretched to fit is put back in shape, unless it's so far off it can't be
    /// this photo's (an editor changed the photo but not the thumbnail), when this returns null.
    /// </summary>
    private static SKBitmap? Proportion(SKBitmap thumbnail, int width, int height)
    {
        if (width <= 0 || height <= 0) return thumbnail.Copy();
        double photo = (double)width / height, thumb = (double)thumbnail.Width / thumbnail.Height;
        var mismatch = thumb / photo;
        if (mismatch is > 1 / 1.03 and < 1.03) return thumbnail.Copy();

        // The part the photo fills, centred.
        int w = thumbnail.Width, h = thumbnail.Height;
        var area = thumb > photo
            ? SKRectI.Create((w - (int)Math.Round(h * photo)) / 2, 0, (int)Math.Round(h * photo), h)
            : SKRectI.Create(0, (h - (int)Math.Round(w / photo)) / 2, w, (int)Math.Round(w / photo));

        var sideBars = thumb > photo;
        var bars = sideBars
            ? (SKRectI.Create(0, 0, area.Left, h), new SKRectI(area.Right, 0, w, h))
            : (SKRectI.Create(0, 0, w, area.Top), new SKRectI(0, area.Bottom, w, h));
        if (IsBlack(thumbnail, bars.Item1) && IsBlack(thumbnail, bars.Item2))
        {
            using var picture = new SKBitmap();
            if (!thumbnail.ExtractSubset(picture, TakeInBars(thumbnail, area, sideBars))) return null;
            return picture.Copy();
        }
        if (mismatch is > 1.5 or < 1 / 1.5) return null;
        return thumbnail.Resize(new SKImageInfo(area.Width, area.Height, thumbnail.ColorType, thumbnail.AlphaType),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    /// <summary>
    /// <paramref name="area"/> without any of the bars still at its edges: cameras round the bars' size their own
    /// way (a Fujifilm X-S10 leaves 104 rows of picture where 107 would be the photo's proportions).
    /// </summary>
    private static SKRectI TakeInBars(SKBitmap bitmap, SKRectI area, bool sideBars)
    {
        const int most = 4;
        var (start, end) = sideBars ? (area.Left, area.Right) : (area.Top, area.Bottom);
        var (first, last) = (start, end);
        while (first < start + most && first < last - 1 && IsBlack(bitmap, Line(first))) first++;
        while (last > end - most && last - 1 > first && IsBlack(bitmap, Line(last - 1))) last--;
        return sideBars ? new SKRectI(first, area.Top, last, area.Bottom) : new SKRectI(area.Left, first, area.Right, last);

        SKRectI Line(int at) => sideBars ? new SKRectI(at, area.Top, at + 1, area.Bottom) : new SKRectI(area.Left, at, area.Right, at + 1);
    }

    /// <summary>
    /// Whether nearly all of <paramref name="rect"/> is black. JPEG leaves it not quite black, and colour (stored at
    /// half size) bleeds into the row or column beside the picture, so this goes by brightness.
    /// </summary>
    private static bool IsBlack(SKBitmap bitmap, SKRectI rect)
    {
        var dark = 0;
        for (var y = rect.Top; y < rect.Bottom; y++)
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red * 299 + c.Green * 587 + c.Blue * 114 < 40_000) dark++;
            }
        return dark >= rect.Width * rect.Height * 0.95;
    }
}
