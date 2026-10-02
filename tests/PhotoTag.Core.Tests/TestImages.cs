using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace PhotoTag.Core.Tests;

/// <summary>Builds small real JPEGs with hand-made EXIF/XMP segments for tests.</summary>
internal static class TestImages
{
    public static byte[] Jpeg(int width, int height, Exif? exif = null, IEnumerable<string>? xmpKeywords = null)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            // Mark the top-left corner red so orientation handling can be checked.
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 4f, height / 4f, paint);
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = data.ToArray();

        var segments = new List<byte[]>();
        if (exif is not null) segments.Add(App1(Encoding.ASCII.GetBytes("Exif\0\0"), exif.BuildTiff()));
        if (xmpKeywords is not null) segments.Add(App1(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), Xmp(xmpKeywords)));

        // Insert the APP1 segments straight after SOI (FFD8).
        return [.. jpeg[..2], .. segments.SelectMany(s => s), .. jpeg[2..]];
    }

    public static string Write(string directory, string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] App1(byte[] header, byte[] payload)
    {
        var segment = new byte[4 + header.Length + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(2 + header.Length + payload.Length));
        header.CopyTo(segment, 4);
        payload.CopyTo(segment, 4 + header.Length);
        return segment;
    }

    private static byte[] Xmp(IEnumerable<string> keywords)
    {
        var items = string.Concat(keywords.Select(k => $"<rdf:li>{k}</rdf:li>"));
        var xml = $"""
            <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
             <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmp:Rating="4">
               <dc:subject><rdf:Bag>{items}</rdf:Bag></dc:subject>
              </rdf:Description>
             </rdf:RDF>
            </x:xmpmeta>
            <?xpacket end="w"?>
            """;
        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>
    /// A small picture to embed as an EXIF thumbnail: <paramref name="picture"/> (blue, with a red top-left corner)
    /// in a black <paramref name="width"/> × <paramref name="height"/> frame, as cameras do: centred, or with its
    /// top-left corner <paramref name="at"/>.
    /// </summary>
    public static byte[] Thumbnail(int width, int height, SKSizeI? picture = null, SKPointI? at = null)
    {
        var size = picture ?? new SKSizeI(width, height);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            float left = at?.X ?? (width - size.Width) / 2, top = at?.Y ?? (height - size.Height) / 2;
            using var blue = new SKPaint { Color = SKColors.SteelBlue };
            canvas.DrawRect(left, top, size.Width, size.Height, blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(left, top, size.Width / 4f, size.Height / 4f, red);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>A minimal TIFF/EXIF block: IFD0, an EXIF sub-IFD, and IFD1 with a thumbnail.</summary>
    internal sealed class Exif
    {
        public string? Make { get; init; }
        public string? Model { get; init; }
        public string? Description { get; init; }
        public ushort? Orientation { get; init; }
        public string? DateTimeOriginal { get; init; } // "yyyy:MM:dd HH:mm:ss"
        public ushort? Iso { get; init; }
        public uint? PixelWidth { get; init; }
        public uint? PixelHeight { get; init; }

        /// <summary>A JPEG for IFD1, as cameras embed (see <see cref="TestImages.Thumbnail"/>).</summary>
        public byte[]? Thumbnail { get; init; }

        /// <summary>Motorola byte order ("MM"), as Nikon and others write, rather than Intel ("II").</summary>
        public bool BigEndian { get; init; }

        private const ushort Short = 3, Long = 4, Ascii = 2;

        public byte[] BuildTiff()
        {
            var ifd0 = new List<Entry>();
            if (Make is not null) ifd0.Add(Entry.Text(0x010F, Make));
            if (Model is not null) ifd0.Add(Entry.Text(0x0110, Model));
            if (Description is not null) ifd0.Add(Entry.Text(0x010E, Description));
            if (Orientation is { } o) ifd0.Add(Entry.UInt16(0x0112, o));

            var sub = new List<Entry>();
            if (Iso is { } iso) sub.Add(Entry.UInt16(0x8827, iso));
            if (DateTimeOriginal is not null) sub.Add(Entry.Text(0x9003, DateTimeOriginal));
            if (PixelWidth is { } w) sub.Add(Entry.UInt32(0xA002, w));
            if (PixelHeight is { } h) sub.Add(Entry.UInt32(0xA003, h));

            var ifd1 = new List<Entry>();
            if (Thumbnail is not null)
                ifd1.AddRange([Entry.UInt16(0x0103, 6), Entry.UInt32(0x0201, 0), Entry.UInt32(0x0202, (uint)Thumbnail.Length)]);

            if (sub.Count > 0) ifd0.Add(Entry.UInt32(0x8769, 0)); // pointer, set below

            const int ifd0Offset = 8;
            var subOffset = ifd0Offset + IfdSize(ifd0);
            var ifd1Offset = subOffset + (sub.Count > 0 ? IfdSize(sub) : 0);
            var thumbnailOffset = ifd1Offset + (ifd1.Count > 0 ? IfdSize(ifd1) : 0);

            if (sub.Count > 0) ifd0[^1] = ifd0[^1] with { Value = (uint)subOffset };
            if (ifd1.Count > 0) ifd1[1] = ifd1[1] with { Value = (uint)thumbnailOffset };

            var tiff = new List<byte>();
            tiff.AddRange(BigEndian ? "MM"u8 : "II"u8);
            Put16(tiff, 42);
            Put32(tiff, ifd0Offset);
            WriteIfd(tiff, ifd0, ifd1.Count > 0 ? (uint)ifd1Offset : 0);
            if (sub.Count > 0) WriteIfd(tiff, sub, 0);
            if (ifd1.Count > 0) WriteIfd(tiff, ifd1, 0);
            if (Thumbnail is not null) tiff.AddRange(Thumbnail);
            return [.. tiff];
        }

        /// <summary>The IFD and the values too big to fit in it, which follow it.</summary>
        private static int IfdSize(List<Entry> entries) =>
            2 + entries.Count * 12 + 4 + entries.Where(e => e.Bytes is { Length: > 4 }).Sum(e => e.Bytes!.Length + e.Bytes.Length % 2);

        private void WriteIfd(List<byte> tiff, List<Entry> entries, uint next)
        {
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            Put16(tiff, (ushort)entries.Count);

            var dataOffset = tiff.Count + entries.Count * 12 + 4;
            var overflow = new List<byte[]>();
            foreach (var e in entries)
            {
                Put16(tiff, e.Tag);
                Put16(tiff, e.Type);
                Put32(tiff, e.Count);
                if (e.Bytes is null)
                {
                    if (e.Type == Short)
                    {
                        Put16(tiff, (ushort)e.Value);
                        Put16(tiff, 0);
                    }
                    else Put32(tiff, e.Value);
                }
                else if (e.Bytes.Length <= 4)
                {
                    tiff.AddRange(e.Bytes);
                    tiff.AddRange(new byte[4 - e.Bytes.Length]);
                }
                else
                {
                    Put32(tiff, (uint)dataOffset);
                    dataOffset += e.Bytes.Length + e.Bytes.Length % 2;
                    overflow.Add(e.Bytes);
                }
            }

            Put32(tiff, next);
            foreach (var data in overflow)
            {
                tiff.AddRange(data);
                if (data.Length % 2 == 1) tiff.Add(0);
            }
        }

        private void Put16(List<byte> tiff, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            else BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            tiff.AddRange(bytes);
        }

        private void Put32(List<byte> tiff, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            tiff.AddRange(bytes);
        }

        /// <summary>A text value (<paramref name="Bytes"/>), or a number (<paramref name="Value"/>).</summary>
        private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[]? Bytes, uint Value = 0)
        {
            public static Entry Text(ushort tag, string value)
            {
                var bytes = Encoding.ASCII.GetBytes(value + "\0");
                return new Entry(tag, Ascii, (uint)bytes.Length, bytes);
            }

            public static Entry UInt16(ushort tag, ushort value) => new(tag, Short, 1, null, value);

            public static Entry UInt32(ushort tag, uint value) => new(tag, Long, 1, null, value);
        }
    }
}

/// <summary>A temp directory that deletes itself.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("phototag-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
