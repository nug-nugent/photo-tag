using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// A year, month or day in "Jump to day", when the grid is grouped by day. Clicking one scrolls the
/// grid to its first day; years and months fold open like the folder tree.
/// </summary>
public partial class DateNodeViewModel : ViewModelBase
{
    private DateNodeViewModel(string label, DayHeaderViewModel? day, IReadOnlyList<DateNodeViewModel> children)
    {
        Label = label;
        Day = day;
        Children = children;
        Refresh();
    }

    public string Label { get; }

    /// <summary>The day this row stands for; null for a year or month.</summary>
    public DayHeaderViewModel? Day { get; }

    public IReadOnlyList<DateNodeViewModel> Children { get; }

    /// <summary>Where the grid scrolls to: this day's heading, or the first day's.</summary>
    public int GridIndex => Day?.GridIndex ?? Children[0].GridIndex;

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    [ObservableProperty] public partial int FavouriteCount { get; private set; }

    /// <summary>Recounts favourites, after one was added or removed.</summary>
    public void Refresh()
    {
        foreach (var child in Children) child.Refresh();
        FavouriteCount = Day?.FavouriteCount ?? Children.Sum(c => c.FavouriteCount);
    }

    /// <summary>
    /// Years, the most recent first, then months, then days, oldest first as in the grid. A year opens if it's the
    /// only one, and a month if it's its year's only one, so a single holiday shows its days straight away.
    /// </summary>
    public static IReadOnlyList<DateNodeViewModel> Build(IReadOnlyList<DayHeaderViewModel> days)
    {
        var culture = CultureInfo.CurrentCulture;
        var nodes = new List<DateNodeViewModel>();
        foreach (var year in days.Where(d => d.Day is not null).GroupBy(d => d.Day!.Value.Year).OrderByDescending(y => y.Key))
        {
            var months = year.GroupBy(d => d.Day!.Value.Month)
                .Select(month => new DateNodeViewModel(
                    culture.DateTimeFormat.GetMonthName(month.Key),
                    null,
                    [.. month.Select(d => new DateNodeViewModel(d.Day!.Value.ToString("ddd d", culture), d, []))]))
                .ToList();
            if (months.Count == 1) months[0].IsExpanded = true;
            nodes.Add(new DateNodeViewModel(year.Key.ToString(culture), null, months));
        }
        if (nodes.Count == 1) nodes[0].IsExpanded = true;
        foreach (var undated in days.Where(d => d.Day is null)) nodes.Add(new DateNodeViewModel(undated.Label, undated, []));
        return nodes;
    }
}
