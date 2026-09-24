using System.Collections.ObjectModel;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// Every tag seen this session, kept sorted, used to autocomplete the tag box.
/// (A persistent, library-wide list comes with the SQLite index.)
/// </summary>
public sealed class KeywordSuggestions
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<string> Items { get; } = [];

    public void Add(IEnumerable<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (!_seen.Add(keyword)) continue;

            var index = 0;
            while (index < Items.Count && StringComparer.CurrentCultureIgnoreCase.Compare(Items[index], keyword) < 0) index++;
            Items.Insert(index, keyword);
        }
    }

    /// <summary>Replaces the whole list, e.g. after a tag was renamed or deleted everywhere.</summary>
    public void Reset(IEnumerable<string> keywords)
    {
        _seen.Clear();
        Items.Clear();
        Add(keywords);
    }
}
