using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class AdvancedSearchTests : UiTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Flyout> OpenPopoverAsync(Window window, MainWindowViewModel vm)
    {
        var button = Find<Button>(window, "AdvancedSearchButton");
        var flyout = (Flyout)button.Flyout!;
        Click(window, button);
        Assert.True(flyout.IsOpen);
        await vm.AdvancedSearch.Loading;
        Settle();
        return flyout;
    }

    private static T InPopover<T>(Flyout flyout, string name) where T : Control
    {
        Settle();
        return ((Control)flyout.Content!).GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    }

    [AvaloniaFact]
    public async Task People_Tags_AndADayAndMonthOverTheYears()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg", new DateTime(2019, 7, 14, 10, 0, 0), "Cake");
        var b = Photo("b.jpg", new DateTime(2021, 7, 14, 18, 30, 0), "Beach");
        var c = Photo("c.jpg", new DateTime(2021, 7, 15, 9, 0, 0), "Beach", "Cake");
        Photo("d.jpg", new DateTime(2020, 1, 1, 12, 0, 0));
        await writer.WriteAsync(a, new MetadataChanges { People = ["Ann"] }, Ct);
        await writer.WriteAsync(b, new MetadataChanges { People = ["Ann", "Bob"] }, Ct);
        await writer.WriteAsync(c, new MetadataChanges { People = ["Bob"] }, Ct);
        var (window, vm) = await OpenAsync(writer);
        await vm.Library.ScanCompletion;

        // Ann's birthday, every year: a person typed in the popover, a day and a month, no year.
        var flyout = await OpenPopoverAsync(window, vm);
        Assert.Equal(["Any year", "2021", "2020", "2019"], vm.AdvancedSearch.YearChoices);
        InPopover<AutoCompleteBox>(flyout, "SearchPersonBox").Focus();
        window.KeyTextInput("Ann");
        Press(window, PhysicalKey.Enter);
        Assert.Equal(["Ann"], vm.AdvancedSearch.People);
        InPopover<ComboBox>(flyout, "SearchDayBox").SelectedIndex = 14;
        InPopover<ComboBox>(flyout, "SearchMonthBox").SelectedIndex = 7;
        Click(window, InPopover<Button>(flyout, "AdvancedSearchGoButton"));
        Assert.False(flyout.IsOpen);
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg", "b.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.StartsWith("2 photos with Ann, taken on 14 July, any year", vm.StatusText);
        Assert.Equal("Search results", vm.Heading);
        Assert.Contains("active", Find<Button>(window, "AdvancedSearchButton").Classes);

        // Ann or Bob, in 2021.
        flyout = await OpenPopoverAsync(window, vm);
        InPopover<ComboBox>(flyout, "PeopleMatchBox").SelectedIndex = 1;
        InPopover<ComboBox>(flyout, "SearchDayBox").SelectedIndex = 0;
        InPopover<ComboBox>(flyout, "SearchMonthBox").SelectedIndex = 0;
        InPopover<ComboBox>(flyout, "SearchYearBox").SelectedIndex = 1;
        InPopover<AutoCompleteBox>(flyout, "SearchPersonBox").Focus();
        window.KeyTextInput("Bob");
        Press(window, PhysicalKey.Enter);
        Click(window, InPopover<Button>(flyout, "AdvancedSearchGoButton"));
        await vm.PhotosLoading;
        Assert.Equal(["b.jpg", "c.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.StartsWith("2 photos with Ann or Bob, taken in 2021", vm.StatusText);

        // The popover keeps what was chosen. Tags combine with the words in the search box: all of them, then any.
        // What's still typed in a box counts when Search is clicked.
        flyout = await OpenPopoverAsync(window, vm);
        Assert.Equal(1, InPopover<ComboBox>(flyout, "SearchYearBox").SelectedIndex);
        Assert.Equal(["Ann", "Bob"], vm.AdvancedSearch.People);
        Click(window, InPopover<Button>(flyout, "AdvancedSearchClearButton"));
        Assert.False(vm.AdvancedSearch.HasCriteria);
        flyout = await OpenPopoverAsync(window, vm);
        InPopover<AutoCompleteBox>(flyout, "SearchTagBox").Focus();
        window.KeyTextInput("cake; beach");
        Click(window, InPopover<Button>(flyout, "AdvancedSearchGoButton"));
        await vm.PhotosLoading;
        Assert.Equal(["c.jpg"], vm.Photos.Select(p => p.FileName));
        flyout = await OpenPopoverAsync(window, vm);
        InPopover<ComboBox>(flyout, "TagsMatchBox").SelectedIndex = 1;
        Click(window, InPopover<Button>(flyout, "AdvancedSearchGoButton"));
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], vm.Photos.Select(p => p.FileName));
        vm.SearchText = "ann";
        Find<AutoCompleteBox>(window, "SearchBox").Focus();
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg", "b.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.StartsWith("2 photos matching ann, tagged cake or beach", vm.StatusText);

        // Clearing the search clears the popover too, and goes back to the folder.
        Click(window, Find<Button>(window, "ClearSearchButton"));
        await vm.PhotosLoading;
        Assert.False(vm.AdvancedSearch.HasCriteria);
        Assert.False(vm.IsSearching);
        Assert.Equal(4, vm.Photos.Count);
        window.Close();
    }
}
