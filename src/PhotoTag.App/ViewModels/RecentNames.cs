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

    public event EventHandler? Changed;

    /// <summary>Puts these names first (the last one given ends up first), dropping the oldest beyond <see cref="MaxCount"/>.</summary>
    public void Add(IEnumerable<string> names)
    {
        var items = Stored.ToList();
        foreach (var name in names)
        {
            items.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, name);
        }
        if (items.Count > MaxCount) items.RemoveRange(MaxCount, items.Count - MaxCount);
        if (items.SequenceEqual(Stored)) return;

        Stored = items;
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
