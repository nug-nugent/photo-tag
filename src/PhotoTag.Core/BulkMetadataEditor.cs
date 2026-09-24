namespace PhotoTag.Core;

public readonly record struct BulkProgress(int Done, int Total);

public sealed record BulkFailure(string Path, string Error);

public sealed record BulkResult
{
    /// <summary>Photos whose metadata was written.</summary>
    public int Changed { get; init; }

    /// <summary>Photos that already had the requested values, so weren't touched.</summary>
    public int Unchanged { get; init; }

    public IReadOnlyList<BulkFailure> Failures { get; init; } = [];

    public bool Cancelled { get; init; }

    /// <summary>The metadata of every photo that was read, as it is after the operation.</summary>
    public IReadOnlyDictionary<string, PhotoMetadata> After { get; init; } = new Dictionary<string, PhotoMetadata>();
}

/// <summary>
/// Applies one change to many photos. Each file is re-read just before it's written, so edits
/// made since the selection was loaded aren't lost, and files that wouldn't change are skipped.
/// Cancelling stops between files: a file is either fully updated or untouched.
/// </summary>
public sealed class BulkMetadataEditor(PhotoMetadataWriter writer)
{
    public Task<BulkResult> AddKeywordsAsync(IReadOnlyList<string> paths, IReadOnlyList<string> keywords,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default) =>
        AddKeywordsAsync(Singles(paths), keywords, progress, cancellationToken);

    public Task<BulkResult> RemoveKeywordsAsync(IReadOnlyList<string> paths, IReadOnlyList<string> keywords,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RemoveKeywordsAsync(Singles(paths), keywords, progress, cancellationToken);

    public Task<BulkResult> SetFavouriteAsync(IReadOnlyList<string> paths, bool favourite,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SetFavouriteAsync(Singles(paths), favourite, progress, cancellationToken);

    private static IReadOnlyList<PhotoFile> Singles(IReadOnlyList<string> paths) => [.. paths.Select(PhotoFile.Single)];

    public Task<BulkResult> AddKeywordsAsync(IReadOnlyList<PhotoFile> paths, IReadOnlyList<string> keywords,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var toAdd = PhotoMetadataWriter.NormalizeKeywords(keywords);
        return ApplyAsync(paths, current =>
        {
            var updated = PhotoMetadataWriter.NormalizeKeywords(current.Keywords.Concat(toAdd));
            return updated.Count == current.Keywords.Count ? null : new MetadataChanges { Keywords = updated };
        }, progress, cancellationToken);
    }

    public Task<BulkResult> RemoveKeywordsAsync(IReadOnlyList<PhotoFile> paths, IReadOnlyList<string> keywords,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var toRemove = new HashSet<string>(PhotoMetadataWriter.NormalizeKeywords(keywords), StringComparer.OrdinalIgnoreCase);
        return ApplyAsync(paths, current =>
        {
            var updated = current.Keywords.Where(k => !toRemove.Contains(k)).ToList();
            return updated.Count == current.Keywords.Count ? null : new MetadataChanges { Keywords = updated };
        }, progress, cancellationToken);
    }

    /// <summary>
    /// Makes every photo a favourite, or not. Photos already in that state are left alone, so
    /// unfavouriting doesn't clear ratings below 5★ set in other apps.
    /// </summary>
    public Task<BulkResult> SetFavouriteAsync(IReadOnlyList<PhotoFile> paths, bool favourite,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ApplyAsync(paths,
            current => current.IsFavourite == favourite ? null : new MetadataChanges { Favourite = favourite },
            progress, cancellationToken);

    /// <param name="plan">Given a photo's current metadata, the change to make, or null for none.</param>
    private async Task<BulkResult> ApplyAsync(IReadOnlyList<PhotoFile> photos, Func<PhotoMetadata, MetadataChanges?> plan,
        IProgress<BulkProgress>? progress, CancellationToken cancellationToken)
    {
        int changed = 0, unchanged = 0, done = 0;
        var failures = new List<BulkFailure>();
        var after = new Dictionary<string, PhotoMetadata>(StringComparer.Ordinal);

        foreach (var photo in photos)
        {
            if (cancellationToken.IsCancellationRequested)
                return new BulkResult { Changed = changed, Unchanged = unchanged, Failures = failures, Cancelled = true, After = after };

            try
            {
                // Each file of a RAW+JPEG pair is planned against its own current values, so a pair
                // whose files disagree still ends up with the change in both.
                var anyChanged = false;
                foreach (var path in photo.AllPaths)
                {
                    var current = await Task.Run(() => PhotoMetadata.Read(path), CancellationToken.None).ConfigureAwait(false);
                    var changes = plan(current);
                    if (changes is not null)
                    {
                        // Not cancellable mid-write: see ExifTool.ExecuteAsync.
                        await writer.WriteAsync(path, changes, CancellationToken.None).ConfigureAwait(false);
                        anyChanged = true;
                        current = current with
                        {
                            Keywords = changes.Keywords is { } k ? PhotoMetadataWriter.NormalizeKeywords(k) : current.Keywords,
                            Rating = changes.Favourite is { } f ? (f ? PhotoMetadataWriter.FavouriteRating : null) : current.Rating,
                        };
                    }
                    if (path == photo.Path) after[path] = current;
                }

                if (anyChanged) changed++;
                else unchanged++;
            }
            catch (Exception e) when (e is ExifToolException or IOException or UnauthorizedAccessException
                                          or MetadataExtractor.ImageProcessingException)
            {
                failures.Add(new BulkFailure(photo.Path, e.Message));
            }

            progress?.Report(new BulkProgress(++done, photos.Count));
        }

        return new BulkResult { Changed = changed, Unchanged = unchanged, Failures = failures, After = after };
    }
}
