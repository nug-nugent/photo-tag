using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoTag.App.ViewModels;

/// <summary>A day's heading in the grid, when it's grouped by day: "Monday 12 August", where, and how many.</summary>
public partial class DayHeaderViewModel : ViewModelBase
{
    public DayHeaderViewModel(DateOnly? day, IReadOnlyList<PhotoItemViewModel> photos, int gridIndex)
    {
        Day = day;
        Photos = photos;
        GridIndex = gridIndex;
        var culture = CultureInfo.CurrentCulture;
        var sameYear = day?.Year == DateTime.Today.Year;
        Label = day?.ToString(sameYear ? "dddd d MMMM" : "dddd d MMMM yyyy", culture) ?? "No date";
        ShortLabel = day?.ToString(sameYear ? "ddd d MMM" : "d MMM yyyy", culture) ?? "No date";
        // Where most of the day's photos were taken, if the place fields say.
        Place = photos.Where(p => !string.IsNullOrWhiteSpace(p.City))
            .GroupBy(p => p.City!, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        Refresh();
    }

    public DateOnly? Day { get; }
    public IReadOnlyList<PhotoItemViewModel> Photos { get; }

    /// <summary>Position among the grid's items, to scroll to it.</summary>
    public int GridIndex { get; }

    public string Label { get; }

    /// <summary>For the "Jump to day" list: "Mon 12 Aug".</summary>
    public string ShortLabel { get; }

    public string? Place { get; }

    /// <summary>"10 photos · 3 favourites".</summary>
    [ObservableProperty] public partial string Summary { get; private set; } = "";

    [ObservableProperty] public partial int FavouriteCount { get; private set; }

    /// <summary>Recounts favourites, after one was added or removed.</summary>
    public void Refresh()
    {
        FavouriteCount = Photos.Count(p => p.IsFavourite);
        var photos = Photos.Count == 1 ? "1 photo" : $"{Photos.Count:N0} photos";
        Summary = FavouriteCount switch
        {
            0 => photos,
            1 => $"{photos} · 1 favourite",
            var n => $"{photos} · {n:N0} favourites",
        };
    }
}
