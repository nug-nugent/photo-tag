using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// The search popover: people and tags (all of them, or any one), a place, and the day, month and year a photo was
/// taken, each optional. A day and month without a year finds a birthday over the years. It adds to the words in
/// the search box and to Favourites.
/// </summary>
public partial class AdvancedSearchViewModel : ViewModelBase
{
    private readonly LibraryIndex _index;
    private readonly Func<string?> _root;
    private readonly Action _search;

    public AdvancedSearchViewModel(LibraryIndex index, Func<string?> root, ObservableCollection<string> tagSuggestions,
        ObservableCollection<string> peopleSuggestions, Action search)
    {
        _index = index;
        _root = root;
        _search = search;
        TagSuggestions = tagSuggestions;
        PeopleSuggestions = peopleSuggestions;
        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCriteria));
        People.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCriteria));
    }

    public ObservableCollection<string> TagSuggestions { get; }
    public ObservableCollection<string> PeopleSuggestions { get; }

    public ObservableCollection<string> Tags { get; } = [];
    public ObservableCollection<string> People { get; } = [];

    [ObservableProperty] public partial string? NewTag { get; set; }
    [ObservableProperty] public partial string? NewPerson { get; set; }

    /// <summary>"All of" (0) or "Any of" (1) the tags.</summary>
    [ObservableProperty] public partial int TagsMatch { get; set; }

    /// <summary>"All of" (0) or "Any of" (1) the people.</summary>
    [ObservableProperty] public partial int PeopleMatch { get; set; }

    public static IReadOnlyList<string> MatchChoices { get; } = ["All of", "Any of"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    public partial string? Place { get; set; }

    /// <summary>"Any day", then 1 to 31: the index is the day.</summary>
    public static IReadOnlyList<string> DayChoices { get; } =
        ["Any day", .. Enumerable.Range(1, 31).Select(d => d.ToString(CultureInfo.InvariantCulture))];

    /// <summary>"Any month", then January to December: the index is the month.</summary>
    public static IReadOnlyList<string> MonthChoices { get; } = ["Any month", .. MonthNames];

    private static IEnumerable<string> MonthNames => CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    public partial int DayIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    public partial int MonthIndex { get; set; }

    /// <summary>"Any year", then the years the library's photos were taken in, newest first.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> YearChoices { get; private set; } = ["Any year"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCriteria))]
    public partial int YearIndex { get; set; }

    public int? Day => DayIndex > 0 ? DayIndex : null;
    public int? Month => MonthIndex > 0 ? MonthIndex : null;
    public int? Year => YearIndex > 0 && YearIndex < YearChoices.Count ? int.Parse(YearChoices[YearIndex], CultureInfo.InvariantCulture) : null;

    /// <summary>After Search or Clear, so the popover closes.</summary>
    public event EventHandler? Searched;

    public bool HasCriteria => Tags.Count > 0 || People.Count > 0 || !string.IsNullOrWhiteSpace(Place)
                               || Day is not null || Month is not null || Year is not null;

    /// <summary>Finding the years to choose from, after the popover opened.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public void Open() => Loading = LoadYearsAsync();

    private async Task LoadYearsAsync()
    {
        if (_root() is not { } root) return;
        var years = (await _index.GetYearsAsync(root)).ToList();
        // The year chosen now, which may have changed while the index was asked.
        var chosen = Year;
        if (chosen is { } year && !years.Contains(year)) years.Insert(0, year);
        YearChoices = ["Any year", .. years.Select(y => y.ToString(CultureInfo.InvariantCulture))];
        YearIndex = chosen is null ? 0 : years.IndexOf(chosen.Value) + 1;
    }

    [RelayCommand]
    private void AddTag() => Add(Tags, NewTag, () => NewTag = "");

    [RelayCommand]
    private void AddPerson() => Add(People, NewPerson, () => NewPerson = "");

    [RelayCommand]
    private void RemoveTag(string tag) => Tags.Remove(tag);

    [RelayCommand]
    private void RemovePerson(string person) => People.Remove(person);

    private static void Add(ObservableCollection<string> list, string? text, Action clear)
    {
        foreach (var value in ListInput.Split(text))
        {
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }
        clear();
    }

    /// <summary>The Search button and Enter: anything still typed in the boxes counts too.</summary>
    [RelayCommand]
    private void Search()
    {
        AddTag();
        AddPerson();
        _search();
        Searched?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Clear button: no criteria, but the search box and Favourites stay as they are.</summary>
    [RelayCommand]
    private void ClearAndSearch()
    {
        Clear();
        _search();
        Searched?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Tags.Clear();
        People.Clear();
        NewTag = NewPerson = "";
        TagsMatch = PeopleMatch = 0;
        Place = null;
        DayIndex = MonthIndex = YearIndex = 0;
    }

    public PhotoQuery AddTo(PhotoQuery query) => query with
    {
        Keywords = [.. query.Keywords, .. Tags],
        AnyKeywords = TagsMatch == 1,
        People = [.. query.People, .. People],
        AnyPeople = PeopleMatch == 1,
        Place = string.IsNullOrWhiteSpace(Place) ? null : Place.Trim(),
        Day = Day,
        Month = Month,
        Year = Year,
    };

    /// <summary>The criteria in words, for the status line: "with Ann or Bob, tagged Cake, taken on 14 July".</summary>
    public string? Describe()
    {
        var parts = new List<string>();
        if (People.Count > 0) parts.Add($"with {Join(People, PeopleMatch == 1)}");
        if (Tags.Count > 0) parts.Add($"tagged {Join(Tags, TagsMatch == 1)}");
        if (!string.IsNullOrWhiteSpace(Place)) parts.Add($"in {Place.Trim()}");
        if (DescribeDate() is { } date) parts.Add($"taken {date}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private string? DescribeDate()
    {
        var month = Month is { } m ? MonthChoices[m] : null;
        return (Day, month, Year) switch
        {
            (null, null, null) => null,
            ({ } d, { } mo, { } y) => $"on {d} {mo} {y}",
            ({ } d, { } mo, null) => $"on {d} {mo}, any year",
            ({ } d, null, { } y) => $"on day {d} of any month in {y}",
            ({ } d, null, null) => $"on day {d} of any month",
            (null, { } mo, { } y) => $"in {mo} {y}",
            (null, { } mo, null) => $"in {mo}, any year",
            (null, null, { } y) => $"in {y}",
        };
    }

    private static string Join(IReadOnlyList<string> values, bool any) => values.Count switch
    {
        1 => values[0],
        _ => $"{string.Join(", ", values.Take(values.Count - 1))} {(any ? "or" : "and")} {values[^1]}",
    };
}
