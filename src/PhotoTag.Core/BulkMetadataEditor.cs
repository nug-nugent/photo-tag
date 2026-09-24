namespace PhotoTag.Core;

public readonly record struct BulkProgress(int Done, int Total);

public sealed record BulkFailure(string Path, string Error);

/// <summary>One file a bulk edit wrote: <paramref name="Photo"/> is the photo it belongs to (the JPEG, for a RAW+JPEG pair).</summary>
public sealed record BulkChange(string Photo, string Path, PhotoMetadata Before, PhotoMetadata After);

public sealed record BulkResult
{
    /// <summary>Photos whose metadata was written.</summary>
    public int Changed { get; init; }

    /// <summary>Photos that already had the requested values, so weren't touched.</summary>
    public int Unchanged { get; init; }

    public IReadOnlyList<BulkFailure> Failures { get; init; } = [];

    public bool Cancelled { get; init; }

    /// <summary>Every file that was written, with its metadata before and after, so the edit can be undone.</summary>
    public IReadOnlyList<BulkChange> Written { get; init; } = [];

    /// <summary>For an undo: photos edited again since, so left as they are.</summary>
    public int ChangedSince { get; init; }

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

    public Task<BulkResult> SetTextAsync(IReadOnlyList<string> paths, string? title, string? description,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SetTextAsync(Singles(paths), title, description, progress, cancellationToken);

    public Task<BulkResult> RenameKeywordAsync(IReadOnlyList<string> paths, string from, string to,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RenameKeywordAsync(Singles(paths), from, to, progress, cancellationToken);

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
    /// Replaces <paramref name="from"/> (in any case) with <paramref name="to"/>. If a photo already has
    /// <paramref name="to"/>, the two merge into one. Every spelling of <paramref name="to"/> becomes
    /// that exact spelling, so renaming a tag to itself tidies up case variants ("beach" into "Beach").
    /// </summary>
    public Task<BulkResult> RenameKeywordAsync(IReadOnlyList<PhotoFile> paths, string from, string to,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0) throw new ArgumentException("Tags can't be empty.");
        return ApplyAsync(paths, current =>
        {
            var updated = PhotoMetadataWriter.NormalizeKeywords(current.Keywords.Select(k =>
                k.Equals(from, StringComparison.OrdinalIgnoreCase) || k.Equals(to, StringComparison.OrdinalIgnoreCase) ? to : k));
            return updated.SequenceEqual(current.Keywords, StringComparer.Ordinal)
                ? null
                : new MetadataChanges { Keywords = updated, Rename = new KeywordRename(from, to) };
        }, progress, cancellationToken);
    }

    /// <summary>
    /// Gives every photo the same title and/or description, replacing what they had. Null leaves
    /// that field alone; empty clears it.
    /// </summary>
    public Task<BulkResult> SetTextAsync(IReadOnlyList<PhotoFile> paths, string? title, string? description,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        title = title is null ? null : PhotoMetadataWriter.NormalizeText(title);
        description = description is null ? null : PhotoMetadataWriter.NormalizeText(description);
        return ApplyAsync(paths, current =>
        {
            var changes = new MetadataChanges
            {
                Title = title is not null && !SameText(title, current.Title) ? title : null,
                Description = description is not null && !SameText(description, current.Description) ? description : null,
            };
            return changes.IsEmpty ? null : changes;
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

    /// <summary>
    /// Puts back the tags, favourite, title and description an earlier edit changed. A file whose
    /// values have changed again since is left alone (counted in <see cref="BulkResult.ChangedSince"/>), so undo
    /// never throws away later work.
    /// </summary>
    public async Task<BulkResult> UndoAsync(IReadOnlyList<BulkChange> changes,
        IProgress<BulkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var byPath = changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var photos = changes.GroupBy(c => c.Photo, StringComparer.Ordinal)
            .Select(g => new PhotoFile(g.Key, [.. g.Select(c => c.Path).Where(p => p != g.Key)]))
            .ToList();
        var changedSince = new HashSet<string>(StringComparer.Ordinal);

        var result = await ApplyAsync(photos, (path, current) =>
        {
            if (!byPath.TryGetValue(path, out var change)) return null; // e.g. only the RAW of a pair was written
            if (!SameEditableValues(current, change.After))
            {
                changedSince.Add(change.Photo);
                return null;
            }
            return Restore(change.Before, current);
        }, progress, cancellationToken).ConfigureAwait(false);

        return result with { ChangedSince = changedSince.Count };
    }

    // What bulk edits can change: tags (and Lightroom's nested keywords), rating, title and description.
    private static bool SameEditableValues(PhotoMetadata a, PhotoMetadata b) =>
        a.Rating == b.Rating && a.Keywords.SequenceEqual(b.Keywords, StringComparer.Ordinal)
        && a.HierarchicalKeywords.SequenceEqual(b.HierarchicalKeywords, StringComparer.Ordinal)
        && SameText(a.Title, b.Title) && SameText(a.Description, b.Description);

    private static bool SameText(string? a, string? b) => PhotoMetadataWriter.NormalizeText(a) == PhotoMetadataWriter.NormalizeText(b);

    private static MetadataChanges? Restore(PhotoMetadata before, PhotoMetadata current)
    {
        var changes = new MetadataChanges
        {
            Keywords = before.Keywords.SequenceEqual(current.Keywords, StringComparer.Ordinal) ? null : before.Keywords,
            // Put these back exactly, rather than working them out again from the tag changes.
            HierarchicalKeywords = before.HierarchicalKeywords.SequenceEqual(current.HierarchicalKeywords, StringComparer.Ordinal)
                                   && before.Keywords.SequenceEqual(current.Keywords, StringComparer.Ordinal)
                ? null
                : before.HierarchicalKeywords,
            Title = SameText(before.Title, current.Title) ? null : PhotoMetadataWriter.NormalizeText(before.Title),
            Description = SameText(before.Description, current.Description) ? null : PhotoMetadataWriter.NormalizeText(before.Description),
        };
        if (before.Rating != current.Rating)
        {
            changes = before.Rating switch
            {
                null => changes with { Favourite = false },
                PhotoMetadataWriter.FavouriteRating => changes with { Favourite = true },
                var rating => changes with { Rating = rating }, // e.g. 4★ from another app
            };
        }
        return changes.IsEmpty ? null : changes;
    }

    private Task<BulkResult> ApplyAsync(IReadOnlyList<PhotoFile> photos, Func<PhotoMetadata, MetadataChanges?> plan,
        IProgress<BulkProgress>? progress, CancellationToken cancellationToken) =>
        ApplyAsync(photos, (_, current) => plan(current), progress, cancellationToken);

    /// <param name="plan">Given a file and its current metadata, the change to make, or null for none.</param>
    private async Task<BulkResult> ApplyAsync(IReadOnlyList<PhotoFile> photos, Func<string, PhotoMetadata, MetadataChanges?> plan,
        IProgress<BulkProgress>? progress, CancellationToken cancellationToken)
    {
        int changed = 0, unchanged = 0, done = 0;
        var failures = new List<BulkFailure>();
        var after = new Dictionary<string, PhotoMetadata>(StringComparer.Ordinal);
        var written = new List<BulkChange>();

        foreach (var photo in photos)
        {
            if (cancellationToken.IsCancellationRequested)
                return new BulkResult { Changed = changed, Unchanged = unchanged, Failures = failures, Cancelled = true, After = after, Written = written };

            try
            {
                // Each file of a RAW+JPEG pair is planned against its own current values, so a pair
                // whose files disagree still ends up with the change in both.
                var anyChanged = false;
                foreach (var path in photo.AllPaths)
                {
                    var current = await Task.Run(() => PhotoMetadata.Read(path), CancellationToken.None).ConfigureAwait(false);
                    var changes = plan(path, current);
                    if (changes is { Keywords: { } newKeywords, HierarchicalKeywords: null })
                    {
                        // Worked out here rather than by the writer, so After (and so undo) knows the result.
                        var hierarchy = KeywordHierarchy.Update(current.HierarchicalKeywords, current.Keywords,
                            PhotoMetadataWriter.NormalizeKeywords(newKeywords), changes.Rename);
                        if (!ReferenceEquals(hierarchy, current.HierarchicalKeywords)) changes = changes with { HierarchicalKeywords = hierarchy };
                    }
                    if (changes is not null)
                    {
                        // Not cancellable mid-write: see ExifTool.ExecuteAsync.
                        await writer.WriteAsync(path, changes, CancellationToken.None).ConfigureAwait(false);
                        anyChanged = true;
                        var before = current;
                        current = current with
                        {
                            Keywords = changes.Keywords is { } k ? PhotoMetadataWriter.NormalizeKeywords(k) : current.Keywords,
                            Rating = changes.Favourite is { } f ? (f ? PhotoMetadataWriter.FavouriteRating : null) : changes.Rating ?? current.Rating,
                            Title = changes.Title is { } t ? NullIfEmpty(t) : current.Title,
                            Description = changes.Description is { } d ? NullIfEmpty(d) : current.Description,
                            HierarchicalKeywords = changes.HierarchicalKeywords ?? current.HierarchicalKeywords,
                        };
                        written.Add(new BulkChange(photo.Path, path, before, current));
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

        return new BulkResult { Changed = changed, Unchanged = unchanged, Failures = failures, After = after, Written = written };
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text; // as PhotoMetadata reads an empty field
}
