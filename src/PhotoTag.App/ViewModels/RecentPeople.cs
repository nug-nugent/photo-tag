namespace PhotoTag.App.ViewModels;

/// <summary>
/// The last few people added to a photo, most recent first, for one-click suggestions under a photo's people.
/// Kept in the settings, so they're still there after a restart.
/// </summary>
public sealed class RecentPeople(AppSettings settings)
{
    public const int MaxCount = 30;

    /// <summary>How many to suggest for one photo.</summary>
    public const int ShownCount = 10;

    public IReadOnlyList<string> Items => settings.RecentPeople;

    public event EventHandler? Changed;

    /// <summary>Puts these names first (the last one given ends up first), dropping the oldest beyond <see cref="MaxCount"/>.</summary>
    public void Add(IEnumerable<string> names)
    {
        var items = settings.RecentPeople.ToList();
        foreach (var name in names)
        {
            items.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, name);
        }
        if (items.Count > MaxCount) items.RemoveRange(MaxCount, items.Count - MaxCount);
        if (items.SequenceEqual(settings.RecentPeople)) return;

        settings.RecentPeople = items;
        settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The <see cref="ShownCount"/> most recent people that aren't in <paramref name="except"/>, most recent first.</summary>
    public IEnumerable<string> Suggest(IEnumerable<string> except)
    {
        var have = except.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Items.Where(n => !have.Contains(n)).Take(ShownCount);
    }
}
