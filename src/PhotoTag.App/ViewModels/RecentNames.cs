using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// The people (or tags) last added to a photo, most recent first, for one-click suggestions under a photo's people
/// and tags. Kept in the settings, so they're still there after a restart. More are kept than are shown, so a photo
/// that already has some of them still gets a full row.
/// </summary>
public sealed class RecentNames(AppSettings settings, ListField kind)
{
    public const int MaxCount = 30;

    private List<string> Stored
    {
        get => kind == ListField.People ? settings.RecentPeople : settings.RecentTags;
        set
        {
            if (kind == ListField.People) settings.RecentPeople = value;
            else settings.RecentTags = value;
        }
    }

    public IReadOnlyList<string> Items => Stored;

    /// <summary>
    /// Tags taken out of the suggestions with <see cref="Remove"/>, which mustn't come back as most used either.
    /// (People are only suggested from the recent ones, so for people it's always empty.)
    /// </summary>
    public IReadOnlyList<string> Hidden => kind == ListField.Tags ? settings.HiddenTagSuggestions : [];

    public event EventHandler? Changed;

    /// <summary>Puts these names first (the last one given ends up first), dropping the oldest beyond <see cref="MaxCount"/>.</summary>
    public void Add(IEnumerable<string> names)
    {
        var items = Stored.ToList();
        var hidden = Hidden.ToList();
        foreach (var name in names)
        {
            items.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, name);
            hidden.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        if (items.Count > MaxCount) items.RemoveRange(MaxCount, items.Count - MaxCount);
        if (items.SequenceEqual(Stored) && hidden.Count == Hidden.Count) return;

        Stored = items;
        if (kind == ListField.Tags) settings.HiddenTagSuggestions = hidden;
        settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes a name out of the suggestions (a mistyped one, say) until it's added to a photo again. A tag is hidden
    /// from the most used too.
    /// </summary>
    public void Remove(string name)
    {
        var items = Stored.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var hide = kind == ListField.Tags && !Hidden.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (items.Count == Stored.Count && !hide) return;

        Stored = items;
        if (hide) settings.HiddenTagSuggestions = [.. Hidden, name];
        settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The <paramref name="count"/> most recent names that aren't in <paramref name="except"/>, most recent first.</summary>
    public IEnumerable<string> Suggest(IEnumerable<string> except, int count)
    {
        var have = except.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Items.Where(n => !have.Contains(n)).Take(count);
    }
}
