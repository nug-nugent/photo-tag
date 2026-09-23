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
/// Writes tags and captions via ExifTool. Values are written to XMP (read by Lightroom, digiKam,
/// Windows, macOS…), and for JPEGs also to IPTC for older software, so the two never disagree.
/// Pixel data is never touched. Camera RAW files are never modified at all: their tags go in an
/// .xmp sidecar beside them (IMG_0001.CR2 → IMG_0001.xmp), as Lightroom does.
/// </summary>
public sealed class PhotoMetadataWriter(ExifTool exifTool)
{
    /// <summary>
    /// Keep each file's "date modified" when saving tags. Off by default: backup and sync tools
    /// usually spot changes by size and modified time, and many tag edits (a rating from 3 to 4,
    /// one tag swapped for another of the same length) don't change the size, so with this on
    /// those edits would never reach the backup.
    /// </summary>
    public bool PreserveModifiedTime { get; set; }

    /// <summary>Writes the same changes to every file of a shot (a RAW+JPEG pair gets both).</summary>
    public async Task WriteAsync(PhotoFile photo, MetadataChanges changes, CancellationToken cancellationToken = default)
    {
        foreach (var path in photo.AllPaths) await WriteAsync(path, changes, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string path, MetadataChanges changes, CancellationToken cancellationToken = default)
    {
        if (changes.IsEmpty) return;
        if (!File.Exists(path)) throw new FileNotFoundException("Photo not found.", path);

        if (!PhotoFiles.IsRaw(path))
        {
            await WriteFileAsync(path, changes, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (PhotoFiles.FindSidecar(path) is { } existing)
        {
            await WriteFileAsync(existing, changes, cancellationToken).ConfigureAwait(false);
            return;
        }

        // First edit of this RAW: create its sidecar. Once it exists, the sidecar's values replace
        // what's embedded in the RAW, so copy those across first, or a new rating would hide the
        // RAW's existing tags.
        var sidecar = PhotoFiles.NewSidecarPath(path);
        var seeded = SeedFromEmbedded(path, changes);
        await File.WriteAllTextAsync(sidecar, EmptyXmpPacket, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WriteFileAsync(sidecar, seeded, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(sidecar); // an empty sidecar would hide the RAW's own tags
            throw;
        }
    }

    private async Task WriteFileAsync(string path, MetadataChanges changes, CancellationToken cancellationToken)
    {
        var output = await exifTool.ExecuteAsync(BuildArguments(path, changes, PreserveModifiedTime), cancellationToken)
            .ConfigureAwait(false);
        if (!output.Contains("1 image files updated", StringComparison.Ordinal)
            && !output.Contains("1 image files unchanged", StringComparison.Ordinal))
        {
            throw new ExifToolException($"ExifTool didn't update the file: {output.Trim()}");
        }
    }

    private static MetadataChanges SeedFromEmbedded(string rawPath, MetadataChanges changes)
    {
        PhotoMetadata embedded;
        try
        {
            embedded = PhotoMetadata.ReadEmbedded(rawPath);
        }
        catch (Exception e) when (e is IOException or MetadataExtractor.ImageProcessingException)
        {
            return changes;
        }

        return changes with
        {
            Keywords = changes.Keywords ?? (embedded.Keywords.Count > 0 ? embedded.Keywords : null),
            Title = changes.Title ?? embedded.Title,
            Description = changes.Description ?? embedded.Description,
            Rating = changes.Rating ?? embedded.Rating,
        };
    }

    private const string EmptyXmpPacket = """
        <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    /// <summary>Trims, drops blanks and removes case-insensitive duplicates, keeping the first spelling.</summary>
    public static IReadOnlyList<string> NormalizeKeywords(IEnumerable<string> keywords) =>
        keywords.Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static List<string> BuildArguments(string path, MetadataChanges changes, bool preserveModifiedTime = false)
    {
        var isJpeg = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

        List<string> args =
        [
            "-charset", "filename=utf8", // file names are passed as UTF-8
            "-E",                         // values are HTML-escaped, so they can contain newlines
            "-m",                         // don't refuse to write because of minor quirks in existing metadata
            "-overwrite_original_in_place", // no *_original backups; keeps created time and attributes
        ];
        if (preserveModifiedTime) args.Add("-P");

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
