using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace PhotoTag.Core;

/// <summary>
/// Decodes TIFFs, which Skia can't, with LibTiff.NET. Reads a strip (or a row of tiles) at a time and
/// shrinks as it goes, so a 100 MB scan is never held in memory whole just to make a thumbnail. Handles
/// whatever libtiff's RGBA interface does: 8- and 16-bit RGB, greyscale, palette, CMYK, any compression.
/// </summary>
internal static class TiffDecoder
{
    static TiffDecoder() => Tiff.SetErrorHandler(new SilentErrorHandler()); // it writes to the console otherwise

    public sealed record Decoded(SKBitmap Bitmap, SKEncodedOrigin Origin, bool HasAlpha);

    /// <summary>
    /// The first image in the file, in stored orientation (not yet rotated), shrunk by a whole factor so its
    /// longest edge is still at least <paramref name="maxSize"/>.
    /// </summary>
    public static Decoded Decode(Stream stream, int maxSize, string description)
    {
        try
        {
            return DecodeCore(stream, maxSize, description);
        }
        catch (Exception e) when (e is not (InvalidDataException or PreviewUnavailableException or IOException))
        {
            // LibTiff.NET can trip over damaged files with all sorts of exceptions.
            throw new InvalidDataException($"Could not decode image: {description}", e);
        }
    }

    private static Decoded DecodeCore(Stream stream, int maxSize, string description)
    {
        using var tif = Tiff.ClientOpen(description, "r", stream, new TiffStream())
            ?? throw new InvalidDataException($"Unsupported or corrupt image: {description}");

        if (!tif.RGBAImageOK(out _))
            throw new PreviewUnavailableException("PhotoTag can't show this kind of TIFF.");
        var image = TiffRgbaImage.Create(tif, false, out _)
            ?? throw new PreviewUnavailableException("PhotoTag can't show this kind of TIFF.");

        int width = image.Width, height = image.Height;
        var orientation = (int)image.Orientation is >= 1 and <= 8 ? (int)image.Orientation : 1;
        var hasAlpha = image.Alpha != ExtraSample.UNSPECIFIED;
        var factor = Math.Max(1, Math.Max(width, height) / maxSize);

        using var shrinker = new Shrinker(width, height, factor, hasAlpha);
        var rows = new BandReader(tif, width, height, orientation);
        var band = new int[rows.BandPixels];
        for (var top = 0; top < height; top += rows.BandHeight)
        {
            var count = Math.Min(rows.BandHeight, height - top);
            SilentErrorHandler.Failed = false;
            // libtiff reports a damaged strip to the error handler, then carries on with it blank.
            if (!rows.Read(top, count, band) || SilentErrorHandler.Failed)
            {
                if (top == 0) throw new InvalidDataException($"Could not decode image: {description}");
                break; // damaged part way through: show what we have, as for a truncated JPEG
            }
            shrinker.Add(band, count);
        }
        return new Decoded(shrinker.Finish(), (SKEncodedOrigin)orientation, hasAlpha);
    }

    /// <summary>
    /// Reads full-width bands of rows, top to bottom, in the order they're stored. libtiff's RGBA reads
    /// return each strip or tile upside down and flip some orientations for us; this undoes both, so
    /// <see cref="ImageRenderer.ApplyOrientation"/> can handle all eight orientations the same way.
    /// </summary>
    private sealed class BandReader
    {
        private readonly Tiff _tif;
        private readonly int _width;
        private readonly int _tileWidth;
        private readonly int[]? _tile;
        private readonly bool _flippedVertically;
        private readonly bool _flippedHorizontally;

        public BandReader(Tiff tif, int width, int height, int orientation)
        {
            _tif = tif;
            _width = width;
            // With its default lower-left origin, libtiff flips these orientations (setorientation in tif_getimage.c).
            _flippedVertically = orientation is 1 or 2 or 5 or 6;
            _flippedHorizontally = orientation is 2 or 3 or 6 or 7;

            if (tif.IsTiled())
            {
                _tileWidth = tif.GetField(TiffTag.TILEWIDTH)[0].ToInt();
                BandHeight = tif.GetField(TiffTag.TILELENGTH)[0].ToInt();
                _tile = new int[_tileWidth * BandHeight];
            }
            else
            {
                var rowsPerStrip = tif.GetFieldDefaulted(TiffTag.ROWSPERSTRIP)[0].ToInt();
                BandHeight = rowsPerStrip <= 0 ? height : Math.Min(rowsPerStrip, height); // unset reads as uint.MaxValue
            }
        }

        /// <summary>Rows per band: the strip or tile height.</summary>
        public int BandHeight { get; }

        public int BandPixels => _width * BandHeight;

        /// <summary>Fills <paramref name="band"/> with rows top..top+count, row by row, left to right.</summary>
        public bool Read(int top, int count, int[] band)
        {
            if (_tile is null)
            {
                if (!_tif.ReadRGBAStrip(top, band)) return false;
                Restore(band, 0, _width, _width, count, band, 0, _width);
                return true;
            }

            for (var left = 0; left < _width; left += _tileWidth)
            {
                if (!_tif.ReadRGBATile(left, top, _tile)) return false;
                // A partial tile at the bottom edge is moved to the bottom of the buffer, like a whole one.
                var columns = Math.Min(_tileWidth, _width - left);
                Restore(_tile, (BandHeight - count) * _tileWidth, _tileWidth, columns, count, band, left, _width);
            }
            return true;
        }

        private void Restore(int[] source, int sourceStart, int sourceStride, int columns, int rows,
            int[] target, int targetLeft, int targetStride)
        {
            // Copy the rows out first: for strips, source and target are the same array.
            var copies = new int[rows * columns];
            for (var r = 0; r < rows; r++)
                source.AsSpan(sourceStart + r * sourceStride, columns).CopyTo(copies.AsSpan(r * columns));

            for (var r = 0; r < rows; r++)
            {
                // Without a vertical flip libtiff wrote rows top down; with one, bottom up.
                var from = copies.AsSpan((_flippedVertically ? rows - 1 - r : r) * columns, columns);
                var to = target.AsSpan(r * targetStride + targetLeft, columns);
                from.CopyTo(to);
                if (_flippedHorizontally) to.Reverse();
            }
        }
    }

    /// <summary>Averages each factor × factor block of stored rows into one pixel, as rows arrive.</summary>
    private sealed class Shrinker : IDisposable
    {
        private readonly int _width;
        private readonly int _factor;
        private readonly SKBitmap _bitmap;
        private readonly long[] _sums; // r, g, b, a per output column
        private readonly int[] _counts;
        private int _sourceRow;
        private int _outputRow;
        private bool _finished;

        public Shrinker(int width, int height, int factor, bool hasAlpha)
        {
            _width = width;
            _factor = factor;
            var outWidth = (width + factor - 1) / factor;
            var outHeight = (height + factor - 1) / factor;
            _bitmap = new SKBitmap(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888,
                hasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
            _bitmap.Erase(SKColors.Black); // what's left of a damaged file
            _sums = new long[outWidth * 4];
            _counts = new int[outWidth];
        }

        public void Add(int[] band, int rows)
        {
            for (var r = 0; r < rows; r++)
            {
                var row = band.AsSpan(r * _width, _width);
                for (var x = 0; x < _width; x++)
                {
                    // libtiff packs pixels as A B G R, high byte to low, premultiplied.
                    var pixel = (uint)row[x];
                    var o = x / _factor;
                    _sums[o * 4] += pixel & 0xFF;
                    _sums[o * 4 + 1] += (pixel >> 8) & 0xFF;
                    _sums[o * 4 + 2] += (pixel >> 16) & 0xFF;
                    _sums[o * 4 + 3] += pixel >> 24;
                    _counts[o]++;
                }
                if (++_sourceRow % _factor == 0) Flush();
            }
        }

        /// <summary>The shrunk image, which the caller then owns.</summary>
        public SKBitmap Finish()
        {
            if (_sourceRow % _factor != 0) Flush();
            _finished = true;
            return _bitmap;
        }

        private void Flush()
        {
            if (_outputRow >= _bitmap.Height) return;
            var pixels = _bitmap.GetPixelSpan();
            var start = _outputRow * _bitmap.RowBytes;
            for (var o = 0; o < _counts.Length; o++)
            {
                var n = Math.Max(1, _counts[o]);
                for (var c = 0; c < 4; c++)
                    pixels[start + o * 4 + c] = (byte)((_sums[o * 4 + c] + n / 2) / n);
            }
            Array.Clear(_sums);
            Array.Clear(_counts);
            _outputRow++;
        }

        public void Dispose()
        {
            if (!_finished) _bitmap.Dispose();
        }
    }

    /// <summary>Keeps libtiff quiet, noting errors for the thread that's decoding (the handler is global).</summary>
    private sealed class SilentErrorHandler : TiffErrorHandler
    {
        [ThreadStatic] public static bool Failed;

        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) => Failed = true;
        public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) => Failed = true;
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }
}
