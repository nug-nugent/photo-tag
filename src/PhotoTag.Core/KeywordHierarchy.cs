namespace PhotoTag.Core;

/// <summary>A tag being renamed (or merged into another), so nested keywords can follow it.</summary>
internal sealed record KeywordRename(string From, string To);

/// <summary>
/// Lightroom's nested keywords (<c>lr:hierarchicalSubject</c>, e.g. "Places|UK|Cornwall"), which
/// Lightroom and digiKam keep beside the flat tag list and prefer when reading a file. PhotoTag
/// doesn't show or create them, but when it renames or removes a tag it updates them to match,
/// or those apps would bring the old tag back.
/// </summary>
internal static class KeywordHierarchy
{
    private const char Separator = '|';

    /// <summary>
    /// The nested keywords after the flat tags change from <paramref name="oldKeywords"/> to
    /// <paramref name="newKeywords"/>. A removed tag drops out of each path, keeping the rest
    /// ("Places|UK|Cornwall" without "UK" becomes "Places|Cornwall"); a renamed tag is renamed in
    /// place. Parts that were never flat tags (Lightroom can leave parents out) are kept. Returns
    /// <paramref name="entries"/> itself when nothing changes.
    /// </summary>
    public static IReadOnlyList<string> Update(IReadOnlyList<string> entries, IReadOnlyList<string> oldKeywords,
        IReadOnlyList<string> newKeywords, KeywordRename? rename = null)
    {
        if (entries.Count == 0) return entries;

        var kept = new HashSet<string>(newKeywords, StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(oldKeywords.Where(k => !kept.Contains(k)), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var updated = new List<string>();

        foreach (var entry in entries)
        {
            var parts = new List<string>();
            foreach (var part in entry.Split(Separator))
            {
                var name = part.Trim();
                if (rename is not null && name.Equals(rename.From, StringComparison.OrdinalIgnoreCase))
                {
                    changed |= !string.Equals(part, rename.To, StringComparison.Ordinal);
                    parts.Add(rename.To);
                }
                else if (removed.Contains(name))
                {
                    changed = true;
                }
                else
                {
                    parts.Add(part);
                }
            }
            if (parts.Count > 0) updated.Add(string.Join(Separator, parts));
        }

        if (!changed) return entries;
        return [.. updated.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
