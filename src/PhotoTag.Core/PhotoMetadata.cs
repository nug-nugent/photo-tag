using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.WebP;
using MetadataExtractor.Formats.Xmp;

namespace PhotoTag.Core;

/// <summary>What we know about a photo from its embedded EXIF / IPTC / XMP metadata.</summary>
public sealed record PhotoMetadata
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public DateTime? DateTaken { get; init; }
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? LensModel { get; init; }
    public string? ExposureTime { get; init; }
    public string? FNumber { get; init; }
    public int? Iso { get; init; }
    public string? FocalLength { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int? Rating { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// <summary>Keywords/tags, merged from IPTC Keywords and XMP dc:subject.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// Brand and model, e.g. "Canon EOS R6" or "NIKON Z5_2". Uses the first word of the make, so
    /// "NIKON CORPORATION" + "NIKON Z5_2" doesn't repeat itself and "RICOH IMAGING COMPANY, LTD."
    /// becomes "RICOH".
    /// </summary>
    public string? Camera
    {
        get
        {
            var brand = CameraMake?.Split(' ', ',')[0];
            return (brand, CameraModel) switch
            {
                (null, null) => null,
                (null, var model) => model,
                (_, null) => CameraMake,
                var (b, model) when model.StartsWith(b, StringComparison.OrdinalIgnoreCase) => model,
                var (b, model) => $"{b} {model}",
            };
        }
    }

    /// <summary>
    /// Reads a photo's metadata. For a RAW file with an .xmp sidecar, the sidecar's tags, title,
    /// description and rating replace whatever is embedded in the RAW: the sidecar is where
    /// PhotoTag, Lightroom and similar apps save edits.
    /// </summary>
    public static PhotoMetadata Read(string path)
    {
        var metadata = ReadEmbedded(path);
        if (PhotoFiles.IsRaw(path) && PhotoFiles.FindSidecar(path) is { } sidecar)
        {
            var xmp = new XmpReader().Extract(File.ReadAllBytes(sidecar)).GetXmpProperties();
            metadata = metadata with
            {
                Keywords = ReadKeywords(null, xmp),
                Title = Clean(Xmp(xmp, "dc:title[1]")),
                Description = Clean(Xmp(xmp, "dc:description[1]")),
                Rating = int.TryParse(Xmp(xmp, "xmp:Rating"), out var rating) && rating > 0 ? rating : null,
            };
        }
        return metadata;
    }

    /// <summary>Metadata stored inside the file itself, ignoring any sidecar.</summary>
    public static PhotoMetadata ReadEmbedded(string path)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        // RAW files have several sub-IFDs (the raw data, previews, the real EXIF block and so on);
        // take each value from the first one that has it.
        var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault();
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault()?.GetXmpProperties()
                  ?? new Dictionary<string, string>();

        string? SubIfdString(int tag) => subIfds.Select(d => Clean(d.GetString(tag))).FirstOrDefault(v => v is not null);
        string? SubIfdDescription(int tag) => subIfds.Select(d => Clean(d.GetDescription(tag))).FirstOrDefault(v => v is not null);
        DateTime? SubIfdDate(int tag) => subIfds.Select(d => ReadDate(d, tag)).FirstOrDefault(v => v is not null);
        int? SubIfdInt(int tag) => subIfds.Select(d => d.TryGetInt32(tag, out var v) ? v : (int?)null).FirstOrDefault(v => v is not null);

        var (width, height) = ReadDimensions(path, directories, subIfds, PhotoFiles.IsRaw(path));
        var location = gps?.GetGeoLocation();

        return new PhotoMetadata
        {
            Width = width,
            Height = height,
            DateTaken = SubIfdDate(ExifDirectoryBase.TagDateTimeOriginal)
                        ?? SubIfdDate(ExifDirectoryBase.TagDateTimeDigitized)
                        ?? ReadDate(ifd0, ExifDirectoryBase.TagDateTime),
            CameraMake = Clean(ifd0?.GetString(ExifDirectoryBase.TagMake)),
            CameraModel = Clean(ifd0?.GetString(ExifDirectoryBase.TagModel)),
            // Cameras record "----" and f/0 when a manual or adapted lens reports nothing.
            LensModel = SubIfdString(ExifDirectoryBase.TagLensModel) is { } lens && lens.Trim('-', ' ').Length > 0 ? lens : null,
            ExposureTime = SubIfdDescription(ExifDirectoryBase.TagExposureTime),
            FNumber = SubIfdDescription(ExifDirectoryBase.TagFNumber) is { } f && f != "f/0.0" ? f : null,
            Iso = SubIfdInt(ExifDirectoryBase.TagIsoEquivalent),
            FocalLength = SubIfdDescription(ExifDirectoryBase.TagFocalLength),
            Title = Clean(Xmp(xmp, "dc:title[1]") ?? iptc?.GetString(IptcDirectory.TagObjectName)),
            Description = Clean(Xmp(xmp, "dc:description[1]")
                                ?? iptc?.GetString(IptcDirectory.TagCaption)
                                ?? ifd0?.GetString(ExifDirectoryBase.TagImageDescription)),
            Rating = int.TryParse(Xmp(xmp, "xmp:Rating"), out var rating) ? rating : null,
            Latitude = location is { IsZero: false } ? location.Value.Latitude : null,
            Longitude = location is { IsZero: false } ? location.Value.Longitude : null,
            Keywords = ReadKeywords(iptc, xmp),
        };
    }

    private static (int?, int?) ReadDimensions(string path, IReadOnlyList<MetadataExtractor.Directory> directories,
        IReadOnlyList<ExifSubIfdDirectory> subIfds, bool isRaw)
    {
        // A RAW's JPEG directory describes its embedded preview, not the photo: use the EXIF size,
        // else the largest image the TIFF structure describes.
        if (isRaw)
        {
            if (FujifilmRaf.TryReadSize(path) is { } rafSize) return rafSize;
            foreach (var d in subIfds)
                if (d.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) && d.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h))
                    return (w, h);
            var largest = directories.OfType<ExifDirectoryBase>()
                .Select(d => d.TryGetInt32(ExifDirectoryBase.TagImageWidth, out var w) && d.TryGetInt32(ExifDirectoryBase.TagImageHeight, out var h) ? (W: w, H: h) : (W: 0, H: 0))
                .DefaultIfEmpty()
                .MaxBy(size => (long)size.W * size.H);
            if (largest.W > 0) return (largest.W, largest.H);
        }

        if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpeg)
            return (jpeg.GetImageWidth(), jpeg.GetImageHeight());
        if (directories.OfType<PngDirectory>().FirstOrDefault() is { } png
            && png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) && png.TryGetInt32(PngDirectory.TagImageHeight, out var ph))
            return (pw, ph);
        if (directories.OfType<WebPDirectory>().FirstOrDefault() is { } webp
            && webp.TryGetInt32(WebPDirectory.TagImageWidth, out var ww) && webp.TryGetInt32(WebPDirectory.TagImageHeight, out var wh))
            return (ww, wh);
        foreach (var d in subIfds)
            if (d.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && d.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh))
                return (ew, eh);
        return (null, null);
    }

    private static DateTime? ReadDate(MetadataExtractor.Directory? directory, int tag) =>
        directory is not null && directory.TryGetDateTime(tag, out var value) ? value : null;

    private static string? Xmp(IDictionary<string, string> xmp, string key) =>
        xmp.TryGetValue(key, out var value) ? value : null;

    private static IReadOnlyList<string> ReadKeywords(IptcDirectory? iptc, IDictionary<string, string> xmp)
    {
        var fromIptc = iptc?.GetStringArray(IptcDirectory.TagKeywords) ?? [];
        var fromXmp = xmp
            .Where(p => p.Key.StartsWith("dc:subject[", StringComparison.Ordinal))
            .OrderBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal) // [2] before [10]
            .Select(p => p.Value);

        return fromIptc.Concat(fromXmp)
            .Select(Clean)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Trims whitespace and the NUL padding cameras like to leave in EXIF strings.</summary>
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// Fujifilm RAF files wrap an embedded JPEG whose EXIF gives the preview's size, not the
/// photo's; the real size is in RAF's own header, which MetadataExtractor doesn't read.
/// </summary>
internal static class FujifilmRaf
{
    private const ushort RawImageCroppedSize = 0x111;

    public static (int?, int?)? TryReadSize(string path)
    {
        if (!path.EndsWith(".raf", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            if (!"FUJIFILMCCD-RAW"u8.SequenceEqual(reader.ReadBytes(15))) return null;

            file.Position = 0x5C; // offset of the header directory
            file.Position = ReadUInt32BigEndian(reader);
            var count = ReadUInt32BigEndian(reader);
            for (var i = 0; i < count && i < 1000; i++)
            {
                var tag = ReadUInt16BigEndian(reader);
                var size = ReadUInt16BigEndian(reader);
                if (tag == RawImageCroppedSize && size == 4)
                {
                    int a = ReadUInt16BigEndian(reader), b = ReadUInt16BigEndian(reader);
                    return (Math.Max(a, b), Math.Min(a, b)); // sensor data is always landscape
                }
                file.Position += size;
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static uint ReadUInt32BigEndian(BinaryReader reader) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
    private static ushort ReadUInt16BigEndian(BinaryReader reader) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
}
