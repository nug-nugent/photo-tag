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

    /// <summary>Make and model, without repeating the make when the model already includes it.</summary>
    public string? Camera => (CameraMake, CameraModel) switch
    {
        (null, null) => null,
        (null, var model) => model,
        (var make, null) => make,
        var (make, model) when model.StartsWith(make, StringComparison.OrdinalIgnoreCase) => model,
        var (make, model) => $"{make} {model}",
    };

    public static PhotoMetadata Read(string path)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault();
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault()?.GetXmpProperties()
                  ?? new Dictionary<string, string>();

        var (width, height) = ReadDimensions(directories, subIfd);
        var location = gps?.GetGeoLocation();

        return new PhotoMetadata
        {
            Width = width,
            Height = height,
            DateTaken = ReadDate(subIfd, ExifDirectoryBase.TagDateTimeOriginal)
                        ?? ReadDate(subIfd, ExifDirectoryBase.TagDateTimeDigitized)
                        ?? ReadDate(ifd0, ExifDirectoryBase.TagDateTime),
            CameraMake = Clean(ifd0?.GetString(ExifDirectoryBase.TagMake)),
            CameraModel = Clean(ifd0?.GetString(ExifDirectoryBase.TagModel)),
            LensModel = Clean(subIfd?.GetString(ExifDirectoryBase.TagLensModel)),
            ExposureTime = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagExposureTime)),
            FNumber = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagFNumber)),
            Iso = subIfd is not null && subIfd.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso) ? iso : null,
            FocalLength = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagFocalLength)),
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

    private static (int?, int?) ReadDimensions(IReadOnlyList<MetadataExtractor.Directory> directories, ExifSubIfdDirectory? subIfd)
    {
        if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpeg)
            return (jpeg.GetImageWidth(), jpeg.GetImageHeight());
        if (directories.OfType<PngDirectory>().FirstOrDefault() is { } png
            && png.TryGetInt32(PngDirectory.TagImageWidth, out var pw) && png.TryGetInt32(PngDirectory.TagImageHeight, out var ph))
            return (pw, ph);
        if (directories.OfType<WebPDirectory>().FirstOrDefault() is { } webp
            && webp.TryGetInt32(WebPDirectory.TagImageWidth, out var ww) && webp.TryGetInt32(WebPDirectory.TagImageHeight, out var wh))
            return (ww, wh);
        if (subIfd is not null
            && subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh))
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
