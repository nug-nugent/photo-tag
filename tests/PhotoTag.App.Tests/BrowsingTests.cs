using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>Finding your way around: library views, the breadcrumb, sorting and grouping by day, the tiles.</summary>
public sealed class BrowsingTests : UiTestBase
{
    [AvaloniaFact]
    public async Task LibraryViews_ShowCounts_AndAllPhotosComesFromEveryFolder()
    {
        Photo("a.jpg", "Beach");
        Photo(Path.Combine("2020", "b.jpg"));
        Photo(Path.Combine("2020", "c.jpg"), "Dog");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        await WaitForAsync(() => vm.AllCount == "3");
        Assert.Equal("1", vm.UntaggedCount);
        Assert.Equal("0", vm.FavouriteCount);
        Assert.Equal(["a.jpg"], vm.Photos.Select(p => p.FileName)); // the open folder only
        Assert.Equal(Path.GetFileName(DirPath), vm.Heading);
        Assert.Equal("1 of 1 tagged · 0 favourites", vm.Subheading);

        var all = Find<ToggleButton>(window, "AllPhotosButton");
        Click(window, all);
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], vm.Photos.Select(p => p.FileName).Order());
        Assert.Equal("All photos", vm.Heading);
        Assert.True(all.IsChecked);

        // Another view replaces it.
        Click(window, Find<ToggleButton>(window, "UntaggedButton"));
        await vm.PhotosLoading;
        Assert.False(all.IsChecked);
        Assert.Equal(["b.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal("Untagged", vm.Heading);

        // Switching it off goes back to the folder.
        Click(window, Find<ToggleButton>(window, "UntaggedButton"));
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg"], vm.Photos.Select(p => p.FileName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Breadcrumb_ShowsWhereYouAre_AndGoesBackUp()
    {
        Photo("a.jpg");
        Photo(Path.Combine("2020", "b.jpg"));
        var (window, vm) = await OpenAsync(writer: null);
        var root = vm.RootFolders.Single();
        await root.ChildrenLoading;
        vm.SelectedFolder = root.Children.Single();
        await vm.PhotosLoading;
        await vm.SummariesLoading;
        Assert.Equal([Path.GetFileName(DirPath), "2020"], vm.Breadcrumb.Select(c => c.Name));

        var crumb = await WaitForControlAsync(() => FindAll<Button>(window)
            .FirstOrDefault(b => b.Classes.Contains("crumb") && (b.Content as string) == Path.GetFileName(DirPath)));
        Click(window, crumb);
        await vm.PhotosLoading;
        Assert.Same(root, vm.SelectedFolder);
        Assert.Equal(["a.jpg"], vm.Photos.Select(p => p.FileName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tiles_MarkPhotosWithNoTags_OnceIndexed()
    {
        Photo("a.jpg", "Beach");
        Photo("b.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        await WaitForAsync(() => vm.Photos.All(p => p.IsIndexed));

        var marked = FindAll<Avalonia.Controls.Shapes.Rectangle>(window)
            .Where(r => r.Classes.Contains("untaggedMark") && r.IsEffectivelyVisible)
            .Select(r => ((PhotoItemViewModel)r.DataContext!).FileName);
        Assert.Equal(["b.jpg"], marked);
        Assert.Equal(["Beach"], vm.Photos[0].TagDots);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SortByDate_AndGroupByDay_WithFavouritesAtDoubleSize()
    {
        Photo("a.jpg", new DateTime(2019, 8, 13, 10, 0, 0));
        Photo("b.jpg", new DateTime(2019, 8, 12, 9, 0, 0));
        Photo("c.jpg", new DateTime(2019, 8, 12, 18, 0, 0));
        Photo("d.jpg"); // no date: last
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        await WaitForAsync(() => vm.Photos.All(p => p.IsIndexed));
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg", "d.jpg"], Names(vm));

        var sort = Find<ComboBox>(window, "SortBox");
        sort.SelectedIndex = 1;
        Assert.Equal(PhotoSort.DateTaken, vm.Sort);
        Assert.Equal(["b.jpg", "c.jpg", "a.jpg", "d.jpg"], Names(vm));
        Assert.Empty(vm.Days);

        sort.SelectedIndex = 2;
        Assert.Equal(["b.jpg", "c.jpg", "a.jpg", "d.jpg"], Names(vm));
        Assert.Equal(3, vm.Days.Count);
        Assert.Equal(["b.jpg", "c.jpg"], vm.Days[0].Photos.Select(p => p.FileName));
        Assert.Equal("No date", vm.Days[2].Label);
        Assert.Equal(PhotoSort.Days, AppSettings.Load(SettingsPath).Sort); // remembered
        await WaitForAsync(() => FindAll<TextBlock>(window).Any(t => t.Classes.Contains("dayLabel") && t.Text == vm.Days[0].Label));

        // A favourite takes up two columns and two rows, and the day counts it.
        var b = vm.Photos.Single(p => p.FileName == "b.jpg");
        var c = vm.Photos.Single(p => p.FileName == "c.jpg");
        c.IsFavourite = true;
        Assert.True(c.IsFeatured);
        Assert.Equal("2 photos · 1 favourite", vm.Days[0].Summary);
        await WaitForAsync(() => TileOf(window, c)?.Bounds.Width > 1.8 * TileOf(window, b)!.Bounds.Width);

        // Unless favourites aren't highlighted.
        Click(window, Find<ToggleButton>(window, "HighlightFavouritesButton"));
        Assert.False(vm.HighlightFavourites);
        Assert.False(c.IsFeatured);
        await WaitForAsync(() => Math.Abs(TileOf(window, c)!.Bounds.Width - TileOf(window, b)!.Bounds.Width) < 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task JumpToDay_NestsDaysInYearsAndMonths()
    {
        Photo("a.jpg", new DateTime(2019, 8, 12, 9, 0, 0));
        Photo("b.jpg", new DateTime(2019, 8, 13, 9, 0, 0));
        Photo("c.jpg", new DateTime(2019, 9, 1, 9, 0, 0));
        Photo("d.jpg", new DateTime(2020, 1, 5, 9, 0, 0));
        Photo("e.jpg"); // no date
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        await WaitForAsync(() => vm.Photos.All(p => p.IsIndexed));
        vm.Sort = PhotoSort.Days;

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Equal(["2019", "2020", "No date"], vm.DateTree.Select(n => n.Label));
        var y2019 = vm.DateTree[0];
        Assert.Equal([culture.DateTimeFormat.GetMonthName(8), culture.DateTimeFormat.GetMonthName(9)], y2019.Children.Select(m => m.Label));
        Assert.Equal(2, y2019.Children[0].Children.Count);
        Assert.False(y2019.IsExpanded); // two years: both start folded
        Assert.True(vm.DateTree[1].Children.Single().IsExpanded); // 2020's only month is open inside it
        Assert.Same(vm.Days[3], vm.DateTree[1].Children.Single().Children.Single().Day);
        Assert.Equal(vm.Days[0].GridIndex, y2019.GridIndex);

        // Clicking a year opens it (and jumps to its first day).
        var row = await WaitForControlAsync(() => FindAll<Button>(window)
            .FirstOrDefault(b => b.Classes.Contains("dateNode") && b.DataContext == y2019));
        Click(window, row);
        Assert.True(y2019.IsExpanded);
        await WaitForControlAsync(() => FindAll<Button>(window)
            .FirstOrDefault(b => b.Classes.Contains("dateNode") && b.DataContext == y2019.Children[0]));

        // Favourites add up the tree.
        vm.Photos.Single(p => p.FileName == "b.jpg").IsFavourite = true;
        Assert.Equal(1, y2019.FavouriteCount);
        Assert.Equal(1, y2019.Children[0].FavouriteCount);
        Assert.Equal(0, y2019.Children[1].FavouriteCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TileHeart_FavouritesThatPhoto_WithoutChangingTheSelection()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        Click(window, HeartOf(window, vm.Photos[1]));
        await WaitForAsync(() => !vm.Operations.IsBusy && vm.Photos[1].IsFavourite);
        Assert.True(PhotoMetadata.Read(b).IsFavourite);
        Assert.Same(details, vm.Details); // still a.jpg selected
        Assert.Contains("on", HeartOf(window, vm.Photos[1]).Classes);

        // The selected photo's heart goes through its panel, so the panel stays in step.
        Click(window, HeartOf(window, vm.Photos[0]));
        Assert.True(details.IsFavourite);
        await details.SaveCompletion;
        Assert.True(PhotoMetadata.Read(a).IsFavourite);

        // F in the grid does the same for the selection.
        Find<ScrollViewer>(window, "GridScroller").Focus();
        Press(window, PhysicalKey.F);
        Assert.False(details.IsFavourite);
        await details.SaveCompletion;
        Assert.False(PhotoMetadata.Read(a).IsFavourite);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SuggestedTags_AreTheLibrarysMostUsed_AndAddInOneClick()
    {
        await using var exifTool = RequireExifTool();
        Photo("a.jpg", "Beach", "Dog");
        Photo("b.jpg", "Beach");
        var c = Photo("c.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        await vm.Library.ScanCompletion;
        await WaitForAsync(() => vm.AllCount == "3");

        var details = await SelectSingleAsync(window, vm, 2);
        await WaitForAsync(() => details.SuggestedKeywords.Count == 2);
        Assert.Equal(["Beach", "Dog"], details.SuggestedKeywords);

        var dog = await WaitForControlAsync(() => FindAll<Button>(window)
            .FirstOrDefault(b => b.Classes.Contains("suggestion") && b.IsEffectivelyVisible && b.DataContext as string == "Dog"));
        Click(window, dog);
        await details.SaveCompletion;
        Assert.Equal(["Dog"], PhotoMetadata.Read(c).Keywords);
        Assert.Equal(["Beach"], details.SuggestedKeywords);
        window.Close();
    }

    private static string[] Names(MainWindowViewModel vm) => [.. vm.Photos.Select(p => p.FileName)];

    private static Border? TileOf(Window window, PhotoItemViewModel photo) =>
        FindAll<Border>(window).FirstOrDefault(t => t.Classes.Contains("tile") && t.DataContext == photo && t.IsEffectivelyVisible);

    private static Button HeartOf(Window window, PhotoItemViewModel photo) =>
        FindAll<Button>(window).First(b => b.Classes.Contains("tileHeart") && b.DataContext == photo && b.IsEffectivelyVisible);
}
