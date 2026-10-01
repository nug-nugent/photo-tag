using System.Net;

namespace PhotoTag.Core;

/// <summary>
/// Metadata to change. A <c>null</c> property is left alone; to clear a value, pass an empty
/// string or an empty keyword list.
/// </summary>
public sealed record MetadataChanges
{
    public IReadOnlyList<string>? Keywords { get; init; }
    public IReadOnlyList<string>? People { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Country { get; init; }

    /// <summary>True sets a 5★ rating; false clears the rating.</summary>
    public bool? Favourite { get; init; }

    /// <summary>A rating to copy into a new RAW sidecar, so one set in another app isn't lost.</summary>
    internal int? Rating { get; init; }

    /// <summary>
    /// Lightroom's nested keywords to write. Normally left null: when <see cref="Keywords"/> change,
    /// the writer works these out from the file (see <see cref="KeywordHierarchy"/>).
    /// </summary>
    internal IReadOnlyList<string>? HierarchicalKeywords { get; init; }

    /// <summary>Set when <see cref="Keywords"/> renames a tag, so nested keywords are renamed rather than dropped.</summary>
    internal KeywordRename? Rename { get; init; }

    public bool IsEmpty => Keywords is null && People is null && Favourite is null && Rating is null && HierarchicalKeywords is null
                           && TextFields.All.All(f => this.Get(f) is null);
}

/// <summary>
/// Writes tags and captions via ExifTool. Values are written to XMP (read by Lightroom, digiKam,
/// Windows, macOS…), and for JPEGs and TIFFs also to IPTC for older software, so the two never disagree.
/// Pixel data is never touched. Camera RAW files are never modified at all: their tags go in an
/// .xmp sidecar beside them (IMG_0001.CR2 → IMG_0001.xmp), as Lightroom does.
/// </summary>
public sealed class PhotoMetadataWriter(ExifTool exifTool)
{
    /// <summary>The xmp:Rating that marks a favourite.</summary>
    public const int FavouriteRating = 5;

    /// <summary>
    /// Keep each file's "date modified" when saving tags. Off by default: backup and sync tools
    /// usually spot changes by size and modified time, and many tag edits (favouriting a photo,
    /// one tag swapped for another of the same length) don't change the size, so with this on
    /// those edits would never reach the backup.
    /// </summary>
    public bool PreserveModifiedTime { get; set; }

    /// <summary>Writes the same changes to every file of a shot (a RAW+JPEG pair gets both).</summary>
    public async Task WriteAsync(PhotoFile photo, MetadataChanges changes, CancellationToken cancellationToken = default)
    {
        foreach (var path in photo.AllPaths) await WriteAsync(path, changes, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteAsync(string path, MetadataChanges changes, CancellationToken cancellationToken = default) =>
        // On the thread pool from the start: working out the write reads the file, which is slow on a network share
        // and mustn't hold up the UI thread that saves from the details panel.
        Task.Run(() => WriteCoreAsync(path, changes, cancellationToken), CancellationToken.None);

    private async Task WriteCoreAsync(string path, MetadataChanges changes, CancellationToken cancellationToken)
    {
        if (changes.IsEmpty) return;
        if (!File.Exists(path)) throw new FileNotFoundException("Photo not found.", path);
        changes = WithHierarchy(path, changes);

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
        // what's embedded in the RAW, so copy those across first, or a new favourite would hide the
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

    /// <summary>Where ExifTool writes each new file before it's copied over the photo.</summary>
    internal string TempFolder { get; init; } = Path.Combine(Path.GetTempPath(), "PhotoTag-writes");

    /// <summary>
    /// ExifTool rewrites the whole file. Left to update it in place, it writes a full copy beside the photo, copies that
    /// back over it and deletes it: on a network share, four times the photo's size over the network (1.6 s for a 3 MB
    /// JPEG on the owner's NAS), and the deleted copy kept in the NAS's recycle bin. So it writes the new file here
    /// instead, and that's copied over the photo: twice the size, nothing left on the share, and the photo is still
    /// the same file (its created time, attributes and permissions are kept).
    /// </summary>
    private async Task WriteFileAsync(string path, MetadataChanges changes, CancellationToken cancellationToken)
    {
        var modified = PreserveModifiedTime ? File.GetLastWriteTimeUtc(path) : default;
        Directory.CreateDirectory(TempFolder);
        var written = Path.Combine(TempFolder, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        var keep = false;
        try
        {
            var output = await exifTool.ExecuteAsync(BuildArguments(path, changes, written), cancellationToken).ConfigureAwait(false);
            if (!output.Contains("1 image files created", StringComparison.Ordinal) || !File.Exists(written))
                throw new ExifToolException($"ExifTool didn't update the file: {output.Trim()}");

            await CopyOverAsync(written, path).ConfigureAwait(false);
            if (PreserveModifiedTime) File.SetLastWriteTimeUtc(path, modified);
        }
        catch (HalfWrittenException)
        {
            keep = true; // the only good copy now
            throw;
        }
        finally
        {
            if (!keep) TryDelete(written);
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> over the contents of <paramref name="target"/>. Throws
    /// <see cref="HalfWrittenException"/> if it fails part way, after trying again.
    /// </summary>
    private static async Task CopyOverAsync(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            var started = false;
            try
            {
                using var from = PhotoFiles.OpenRead(source);
                using var to = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, CopyBuffer);
                started = true;
                from.CopyTo(to, CopyBuffer);
                to.SetLength(from.Length);
                to.Flush(flushToDisk: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not started: the photo is as it was (locked, say), and the caller reports it like any other failure.
                if (!started) throw;
                // A network share dropping out mid-copy leaves the photo half written: try again, then keep the new file.
                if (attempt < 3)
                {
                    Log.Warn($"Couldn't finish saving {target}; trying again", e);
                    await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false);
                    continue;
                }
                Log.Error($"Couldn't finish saving {target}, which may be damaged. The saved version is at {source}", e);
                throw new HalfWrittenException($"Couldn't finish saving {Path.GetFileName(target)}: it may be damaged. " +
                                               $"A good copy, with the new tags, is at {source}.", e);
            }
        }
    }

    private sealed class HalfWrittenException(string message, Exception inner) : IOException(message, inner);

    private const int CopyBuffer = 1 << 20; // fewer, larger writes: each one is a round trip on a network share

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't delete {path}", e);
        }
    }

    /// <summary>When tags change, updates Lightroom's nested keywords to match, if the file has any.</summary>
    private static MetadataChanges WithHierarchy(string path, MetadataChanges changes)
    {
        if (changes.Keywords is not { } keywords || changes.HierarchicalKeywords is not null) return changes;

        PhotoMetadata current;
        try
        {
            current = PhotoMetadata.Read(path); // for a RAW, the sidecar if there is one
        }
        catch (Exception e) when (e is IOException or MetadataExtractor.ImageProcessingException)
        {
            return changes;
        }

        var updated = KeywordHierarchy.Update(current.HierarchicalKeywords, current.Keywords, NormalizeKeywords(keywords), changes.Rename);
        return ReferenceEquals(updated, current.HierarchicalKeywords) ? changes : changes with { HierarchicalKeywords = updated };
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
            People = changes.People ?? (embedded.People.Count > 0 ? embedded.People : null),
            Title = changes.Title ?? embedded.Title,
            Description = changes.Description ?? embedded.Description,
            Location = changes.Location ?? embedded.Location,
            City = changes.City ?? embedded.City,
            State = changes.State ?? embedded.State,
            Country = changes.Country ?? embedded.Country,
            Rating = changes.Favourite is null ? embedded.Rating : null,
            HierarchicalKeywords = changes.HierarchicalKeywords ?? (embedded.HierarchicalKeywords.Count > 0 ? embedded.HierarchicalKeywords : null),
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

    /// <summary>A title or description as it's saved: trimmed, with \n line endings. Empty means none.</summary>
    public static string NormalizeText(string? value) => (value ?? "").ReplaceLineEndings("\n").Trim();

    /// <summary>Trims, drops blanks and removes case-insensitive duplicates, keeping the first spelling.</summary>
    public static IReadOnlyList<string> NormalizeKeywords(IEnumerable<string> keywords) =>
        keywords.Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <param name="output">Where ExifTool writes the new file; the photo itself is only read.</param>
    internal static List<string> BuildArguments(string path, MetadataChanges changes, string output)
    {
        // JPEGs and TIFFs also get the older IPTC and EXIF fields. Other apps write them there and PhotoTag reads
        // them, so if they were left alone a tag removed from XMP would come back from IPTC.
        var hasLegacyFields = PhotoFiles.IsJpeg(path) || PhotoFiles.IsTiff(path);

        List<string> args =
        [
            "-charset", "filename=utf8", // file names are passed as UTF-8
            "-E",                         // values are HTML-escaped, so they can contain newlines
            "-m",                         // don't refuse to write because of minor quirks in existing metadata
            "-o", output,
        ];

        if (changes.Keywords is { } keywords)
        {
            var normalized = NormalizeKeywords(keywords);
            SetList(args, "XMP-dc:Subject", normalized);
            if (hasLegacyFields) SetList(args, "IPTC:Keywords", normalized);
        }

        // IPTC's older fields have no equivalent, so people go in XMP only.
        if (changes.People is { } people)
            SetList(args, "XMP-iptcExt:PersonInImage", NormalizeKeywords(people));

        if (changes.HierarchicalKeywords is { } hierarchy)
            SetList(args, "XMP-lr:HierarchicalSubject", hierarchy);

        if (changes.Title is { } title)
        {
            Set(args, "XMP-dc:Title", title);
            if (hasLegacyFields) Set(args, "IPTC:ObjectName", title);
        }

        if (changes.Description is { } description)
        {
            Set(args, "XMP-dc:Description", description);
            if (hasLegacyFields)
            {
                Set(args, "IPTC:Caption-Abstract", description);
                Set(args, "EXIF:ImageDescription", description);
            }
        }

        // XMP names as Lightroom and the IPTC Core standard use them, and the older IPTC fields for JPEGs and TIFFs.
        SetText(args, changes.Location, "XMP-iptcCore:Location", hasLegacyFields ? "IPTC:Sub-location" : null);
        SetText(args, changes.City, "XMP-photoshop:City", hasLegacyFields ? "IPTC:City" : null);
        SetText(args, changes.State, "XMP-photoshop:State", hasLegacyFields ? "IPTC:Province-State" : null);
        SetText(args, changes.Country, "XMP-photoshop:Country", hasLegacyFields ? "IPTC:Country-PrimaryLocationName" : null);

        if (changes.Favourite is { } favourite)
            Set(args, "XMP-xmp:Rating", favourite ? FavouriteRating.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        else if (changes.Rating is { } rating)
            Set(args, "XMP-xmp:Rating", rating.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (hasLegacyFields && (changes.Keywords is not null || TextFields.All.Any(f => changes.Get(f) is not null)))
            args.Add("-IPTC:CodedCharacterSet=UTF8");

        args.Add(path);
        return args;
    }

    // "-Tag=" with no value deletes the tag.
    private static void Set(List<string> args, string tag, string value) =>
        args.Add($"-{tag}={Escape(value.Trim())}");

    private static void SetText(List<string> args, string? value, string xmpTag, string? iptcTag)
    {
        if (value is null) return;
        Set(args, xmpTag, value);
        if (iptcTag is not null) Set(args, iptcTag, value);
    }

    // Assigning a list tag several times in one command replaces the whole list.
    private static void SetList(List<string> args, string tag, IReadOnlyList<string> values)
    {
        if (values.Count == 0) args.Add($"-{tag}=");
        foreach (var value in values) args.Add($"-{tag}={Escape(value)}");
    }

    private static string Escape(string value) =>
        WebUtility.HtmlEncode(value).Replace("\r\n", "&#xa;").Replace("\n", "&#xa;").Replace("\r", "&#xa;");
}
