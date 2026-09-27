using BitMiracle.LibTiff.Classic;
using SkiaSharp;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;

namespace PhotoTag.Core.Tests;

/// <summary>Builds TIFFs for tests, in the layouts and pixel formats real ones come in.</summary>
internal static class TestTiffs
{
    public sealed record Options
    {
        public int Orientation { get; init; } = 1;
        /// <summary>Rows per strip; 0 writes the whole image as one strip.</summary>
        public int RowsPerStrip { get; init; } = 8;
        /// <summary>Tile width and height; 0 writes strips instead.</summary>
        public int TileSize { get; init; }
        public Compression Compression { get; init; } = Compression.NONE;
        public bool SixteenBit { get; init; }
        public bool Grey { get; init; }
        public bool Alpha { get; init; }
    }

    /// <summary>
    /// A picture in four quadrants, as stored (before orientation): red top left, green top right, blue bottom
    /// left, white bottom right. Greyscale ones use four shades instead.
    /// </summary>
    public static SKBitmap Quadrants(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, (x < width / 2, y < height / 2) switch
                {
                    (true, true) => SKColors.Red,
                    (false, true) => SKColors.Lime,
                    (true, false) => SKColors.Blue,
                    _ => SKColors.White,
                });
        return bitmap;
    }

    /// <summary>The shade a greyscale TIFF stores for each quadrant: red 42, green 85, blue 127, white 255.</summary>
    public static byte Grey(SKColor c) => (byte)((c.Red + 2 * c.Green + 3 * c.Blue) / 6);

    /// <summary>Points the last strip past the end of the file, as when a copy was cut short.</summary>
    public static byte[] WithLastStripMissing(byte[] tiff)
    {
        const ushort StripOffsets = 273;
        var ifd = BitConverter.ToInt32(tiff, 4); // little-endian ("II"), as LibTiff.NET writes on every platform we test on
        var entries = BitConverter.ToUInt16(tiff, ifd);
        for (var i = 0; i < entries; i++)
        {
            var entry = ifd + 2 + i * 12;
            if (BitConverter.ToUInt16(tiff, entry) != StripOffsets) continue;
            var (type, count) = (BitConverter.ToUInt16(tiff, entry + 2), BitConverter.ToInt32(tiff, entry + 4));
            var size = type == 3 ? 2 : 4; // SHORT or LONG
            // Values that fit in 4 bytes are in the entry itself; longer lists are elsewhere.
            var values = count * size <= 4 ? entry + 8 : BitConverter.ToInt32(tiff, entry + 8);
            var last = values + (count - 1) * size;
            var patched = tiff.ToArray();
            BitConverter.GetBytes(size == 2 ? 0xFFF0 : 0x7FFFFFF0).AsSpan(0, size).CopyTo(patched.AsSpan(last));
            return patched;
        }
        throw new InvalidOperationException("No StripOffsets tag.");
    }

    public static byte[] Tiff(int width, int height, Options? options = null)
    {
        options ??= new Options();
        using var pixels = Quadrants(width, height);
        var channels = (options.Grey ? 1 : 3) + (options.Alpha ? 1 : 0);
        var bytesPerSample = options.SixteenBit ? 2 : 1;

        // One stored row in the file's format.
        byte[] Row(int y)
        {
            var row = new byte[width * channels * bytesPerSample];
            for (var x = 0; x < width; x++)
            {
                var c = pixels.GetPixel(x, y);
                byte[] samples = options.Grey ? [Grey(c)] : [c.Red, c.Green, c.Blue];
                if (options.Alpha) samples = [.. samples, 255];
                for (var s = 0; s < samples.Length; s++)
                {
                    var i = (x * channels + s) * bytesPerSample;
                    if (options.SixteenBit) (row[i], row[i + 1]) = (samples[s], samples[s]); // v * 257, either byte order
                    else row[i] = samples[s];
                }
            }
            return row;
        }

        var stream = new MemoryStream();
        using (var tif = LibTiff.ClientOpen("test.tif", "w", stream, new TiffStream()))
        {
            tif.SetField(TiffTag.IMAGEWIDTH, width);
            tif.SetField(TiffTag.IMAGELENGTH, height);
            tif.SetField(TiffTag.BITSPERSAMPLE, 8 * bytesPerSample);
            tif.SetField(TiffTag.SAMPLESPERPIXEL, channels);
            tif.SetField(TiffTag.PHOTOMETRIC, options.Grey ? Photometric.MINISBLACK : Photometric.RGB);
            tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tif.SetField(TiffTag.COMPRESSION, options.Compression);
            tif.SetField(TiffTag.ORIENTATION, (Orientation)options.Orientation);
            if (options.Alpha) tif.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });

            if (options.TileSize > 0)
            {
                var size = options.TileSize;
                tif.SetField(TiffTag.TILEWIDTH, size);
                tif.SetField(TiffTag.TILELENGTH, size);
                var rowBytes = size * channels * bytesPerSample;
                for (var top = 0; top < height; top += size)
                    for (var left = 0; left < width; left += size)
                    {
                        var tile = new byte[rowBytes * size]; // parts past the edge stay empty
                        for (var r = 0; r < size && top + r < height; r++)
                        {
                            var row = Row(top + r);
                            var start = left * channels * bytesPerSample;
                            row.AsSpan(start, Math.Min(rowBytes, row.Length - start)).CopyTo(tile.AsSpan(r * rowBytes));
                        }
                        tif.WriteTile(tile, left, top, 0, 0);
                    }
            }
            else
            {
                tif.SetField(TiffTag.ROWSPERSTRIP, options.RowsPerStrip > 0 ? options.RowsPerStrip : height);
                for (var y = 0; y < height; y++) tif.WriteScanline(Row(y), y);
            }
            tif.WriteDirectory();
        }
        return stream.ToArray();
    }
}
