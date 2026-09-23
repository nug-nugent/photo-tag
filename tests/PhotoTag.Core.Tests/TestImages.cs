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

    /// <summary>A minimal little-endian TIFF/EXIF block: IFD0 plus an EXIF sub-IFD.</summary>
    internal sealed class Exif
    {
        public string? Make { get; init; }
        public string? Model { get; init; }
        public ushort? Orientation { get; init; }
        public string? DateTimeOriginal { get; init; } // "yyyy:MM:dd HH:mm:ss"
        public ushort? Iso { get; init; }

        private const ushort Short = 3, Long = 4, Ascii = 2;

        public byte[] BuildTiff()
        {
            var ifd0 = new List<Entry>();
            if (Make is not null) ifd0.Add(Entry.Text(0x010F, Make));
            if (Model is not null) ifd0.Add(Entry.Text(0x0110, Model));
            if (Orientation is { } o) ifd0.Add(Entry.UInt16(0x0112, o));

            var sub = new List<Entry>();
            if (Iso is { } iso) sub.Add(Entry.UInt16(0x8827, iso));
            if (DateTimeOriginal is not null) sub.Add(Entry.Text(0x9003, DateTimeOriginal));

            if (sub.Count > 0) ifd0.Add(new Entry(0x8769, Long, 1, new byte[4])); // pointer, patched below

            const int ifd0Offset = 8;
            var ifd0DataOffset = ifd0Offset + IfdSize(ifd0);
            var subOffset = ifd0DataOffset + DataSize(ifd0);

            if (sub.Count > 0)
                BinaryPrimitives.WriteUInt32LittleEndian(ifd0[^1].Data, (uint)subOffset);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("II"u8);
            w.Write((ushort)42);
            w.Write((uint)ifd0Offset);
            WriteIfd(w, ifd0, ifd0DataOffset);
            if (sub.Count > 0) WriteIfd(w, sub, subOffset + IfdSize(sub));
            return ms.ToArray();
        }

        private static int IfdSize(List<Entry> entries) => 2 + entries.Count * 12 + 4;

        private static int DataSize(List<Entry> entries) =>
            entries.Where(e => e.Data.Length > 4).Sum(e => e.Data.Length + e.Data.Length % 2);

        private static void WriteIfd(BinaryWriter w, List<Entry> entries, int dataOffset)
        {
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            w.Write((ushort)entries.Count);

            var overflow = new List<byte[]>();
            foreach (var e in entries)
            {
                w.Write(e.Tag);
                w.Write(e.Type);
                w.Write(e.Count);
                if (e.Data.Length <= 4)
                {
                    w.Write(e.Data);
                    w.Write(new byte[4 - e.Data.Length]);
                }
                else
                {
                    w.Write((uint)dataOffset);
                    dataOffset += e.Data.Length + e.Data.Length % 2;
                    overflow.Add(e.Data);
                }
            }

            w.Write((uint)0); // no next IFD
            foreach (var data in overflow)
            {
                w.Write(data);
                if (data.Length % 2 == 1) w.Write((byte)0);
            }
        }

        private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data)
        {
            public static Entry Text(ushort tag, string value)
            {
                var bytes = Encoding.ASCII.GetBytes(value + "\0");
                return new Entry(tag, Ascii, (uint)bytes.Length, bytes);
            }

            public static Entry UInt16(ushort tag, ushort value) =>
                new(tag, Short, 1, BitConverter.GetBytes(value));
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
