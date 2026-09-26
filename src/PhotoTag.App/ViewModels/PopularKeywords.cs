namespace PhotoTag.App.ViewModels;

/// <summary>
/// The library's most used tags, most used first, for one-click suggestions under a photo's tags.
/// Reloaded from the index whenever its counts change.
/// </summary>
public sealed class PopularKeywords
{
    public IReadOnlyList<string> Items { get; private set; } = [];

    public event EventHandler? Changed;

    public void Reset(IEnumerable<string> keywordsByUse)
    {
        var items = keywordsByUse.ToList();
        if (items.SequenceEqual(Items)) return;
        Items = items;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The top <paramref name="count"/> tags that aren't in <paramref name="except"/>.</summary>
    public IEnumerable<string> Suggest(IEnumerable<string> except, int count)
    {
        var have = except.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Items.Where(k => !have.Contains(k)).Take(count);
    }
}
