using System.Net;

namespace PhotoTag.Core;

/// <summary>
/// Metadata to change. A <c>null</c> property is left alone; to clear a value, pass an empty
/// string, an empty keyword list, or a rating of 0.
/// </summary>
public sealed record MetadataChanges
{
    public IReadOnlyList<string>? Keywords { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int? Rating { get; init; }

    public bool IsEmpty => Keywords is null && Title is null && Description is null && Rating is null;
}

/// <summary>
/// Writes tags and captions into photo files via ExifTool. Values are written to XMP (read by
/// Lightroom, digiKam, Windows, macOS…), and for JPEGs also to IPTC for older software, so the
/// two never disagree. Pixel data is never touched, and file modified times are preserved.
/// </summary>
public sealed class PhotoMetadataWriter(ExifTool exifTool)
{
    public async Task WriteAsync(string path, MetadataChanges changes, CancellationToken cancellationToken = default)
    {
        if (changes.IsEmpty) return;
        if (!File.Exists(path)) throw new FileNotFoundException("Photo not found.", path);

        var output = await exifTool.ExecuteAsync(BuildArguments(path, changes), cancellationToken).ConfigureAwait(false);
        if (!output.Contains("1 image files updated", StringComparison.Ordinal)
            && !output.Contains("1 image files unchanged", StringComparison.Ordinal))
        {
            throw new ExifToolException($"ExifTool didn't update the file: {output.Trim()}");
        }
    }

    /// <summary>Trims, drops blanks and removes case-insensitive duplicates, keeping the first spelling.</summary>
    public static IReadOnlyList<string> NormalizeKeywords(IEnumerable<string> keywords) =>
        keywords.Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static List<string> BuildArguments(string path, MetadataChanges changes)
    {
        var isJpeg = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

        List<string> args =
        [
            "-charset", "filename=utf8", // file names are passed as UTF-8
            "-E",                         // values are HTML-escaped, so they can contain newlines
            "-m",                         // don't refuse to write because of minor quirks in existing metadata
            "-P",                         // keep the file's modified time
            "-overwrite_original_in_place", // no *_original backups; keeps created time and attributes
        ];

        if (changes.Keywords is { } keywords)
        {
            var normalized = NormalizeKeywords(keywords);
            SetList(args, "XMP-dc:Subject", normalized);
            if (isJpeg) SetList(args, "IPTC:Keywords", normalized);
        }

        if (changes.Title is { } title)
        {
            Set(args, "XMP-dc:Title", title);
            if (isJpeg) Set(args, "IPTC:ObjectName", title);
        }

        if (changes.Description is { } description)
        {
            Set(args, "XMP-dc:Description", description);
            if (isJpeg)
            {
                Set(args, "IPTC:Caption-Abstract", description);
                Set(args, "EXIF:ImageDescription", description);
            }
        }

        if (changes.Rating is { } rating)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rating);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rating, 5);
            Set(args, "XMP-xmp:Rating", rating == 0 ? "" : rating.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (isJpeg && (changes.Keywords is not null || changes.Title is not null || changes.Description is not null))
            args.Add("-IPTC:CodedCharacterSet=UTF8");

        args.Add(path);
        return args;
    }

    // "-Tag=" with no value deletes the tag.
    private static void Set(List<string> args, string tag, string value) =>
        args.Add($"-{tag}={Escape(value.Trim())}");

    // Assigning a list tag several times in one command replaces the whole list.
    private static void SetList(List<string> args, string tag, IReadOnlyList<string> values)
    {
        if (values.Count == 0) args.Add($"-{tag}=");
        foreach (var value in values) args.Add($"-{tag}={Escape(value)}");
    }

    private static string Escape(string value) =>
        WebUtility.HtmlEncode(value).Replace("\r\n", "&#xa;").Replace("\n", "&#xa;").Replace("\r", "&#xa;");
}
