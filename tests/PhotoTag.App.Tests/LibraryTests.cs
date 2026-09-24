using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>The library index as the user sees it: folder counts, search, and suggestions.</summary>
public sealed class LibraryTests : UiTestBase
{
    // root/            a.jpg [Beach]
    // root/2020/       b.jpg []  c.jpg [Dog]  e.jpg [Beach, Dog]
    // root/2020/Summer d.jpg [Beach]
    private void CreateLibrary()
    {
        Photo("a.jpg", "Beach");
        Photo(Path.Combine("2020", "b.jpg"));
        Photo(Path.Combine("2020", "c.jpg"), "Dog");
        Photo(Path.Combine("2020", "e.jpg"), "Beach", "Dog");
        Photo(Path.Combine("2020", "Summer", "d.jpg"), "Beach");
    }

    private static async Task<(FolderNodeViewModel Root, FolderNodeViewModel Year)> WaitForIndexAsync(MainWindowViewModel vm)
    {
        await vm.Library.ScanCompletion;
        var root = vm.RootFolders.Single();
        await root.ChildrenLoading;
        var year = root.Children.Single(c => c.Name == "2020");
        await WaitForAsync(() => root.Counts.Photos == 5 && year.Counts.Photos == 4);
        return (root, year);
    }

    [AvaloniaFact]
    public async Task FolderTree_ShowsPhotoAndTagCounts_IncludingSubfolders()
    {
        CreateLibrary();
        var (window, vm) = await OpenAsync(writer: null);
        var (root, year) = await WaitForIndexAsync(vm);

        Assert.Equal("5 · 4 tagged", root.CountText);
        Assert.Equal("4 · 3 tagged", year.CountText);
        Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "5 · 4 tagged" && t.IsEffectivelyVisible);

        year.IsExpanded = true;
        await year.ChildrenLoading;
        Assert.Equal("1 · 1 tagged", year.Children.Single().CountText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_FindsTaggedPhotosAcrossSubfolders_AndEscGoesBack()
    {
        CreateLibrary();
        var (window, vm) = await OpenAsync(writer: null);
        await WaitForIndexAsync(vm);

        var searchBox = Find<AutoCompleteBox>(window, "SearchBox");
        searchBox.Focus();
        window.KeyTextInput("beach");
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;

        Assert.True(vm.IsSearching);
        Assert.Null(vm.SelectedFolder);
        Assert.Equal(["a.jpg", "e.jpg", "d.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal($"3 photos matching beach in {Path.GetFileName(DirPath)}", vm.StatusText);

        // Several tags must all match.
        vm.SearchText = "Beach, dog";
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        Assert.Equal(["e.jpg"], vm.Photos.Select(p => p.FileName));

        // Titles and file names are searched too.
        var b = Path.Combine(DirPath, "2020", "b.jpg");
        await vm.Library.Index.UpdateAsync([(b, new PhotoMetadata { Title = "Beach hut" })]);
        vm.SearchText = "beach";
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg", "b.jpg", "e.jpg", "d.jpg"], vm.Photos.Select(p => p.FileName));
        vm.SearchText = "c.jpg";
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        Assert.Equal(["c.jpg"], vm.Photos.Select(p => p.FileName));

        // Esc returns to the folder that was showing.
        Press(window, PhysicalKey.Escape);
        await vm.PhotosLoading;
        Assert.False(vm.IsSearching);
        Assert.Equal(vm.RootFolders.Single(), vm.SelectedFolder);
        Assert.Equal(["a.jpg"], vm.Photos.Select(p => p.FileName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ChoosingAFolder_EndsTheSearch()
    {
        CreateLibrary();
        var (window, vm) = await OpenAsync(writer: null);
        var (_, year) = await WaitForIndexAsync(vm);

        Click(window, Find<ToggleButton>(window, "UntaggedButton"));
        await vm.PhotosLoading;
        Assert.Equal(["b.jpg"], vm.Photos.Select(p => p.FileName));

        Click(window, FindAll<TextBlock>(window).Single(t => t.Text == "2020"));
        await vm.PhotosLoading;

        Assert.Equal(year, vm.SelectedFolder);
        Assert.False(vm.IsSearching);
        Assert.False(vm.ShowUntagged);
        Assert.Equal(["b.jpg", "c.jpg", "e.jpg"], vm.Photos.Select(p => p.FileName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Suggestions_IncludeTagsFromFoldersNotYetOpened()
    {
        Photo("a.jpg");
        Photo(Path.Combine("deep", "inside", "z.jpg"), "Zebra");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;

        var details = await SelectSingleAsync(window, vm, 0, waitForEditable: false);
        Assert.Contains("Zebra", details.KeywordSuggestions);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TaggingAnUntaggedPhoto_UpdatesCountsAndSearch()
    {
        await using var exifTool = RequireExifTool();
        CreateLibrary();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var (root, _) = await WaitForIndexAsync(vm);

        Click(window, Find<ToggleButton>(window, "UntaggedButton"));
        await vm.PhotosLoading;
        var details = await SelectSingleAsync(window, vm, 0);
        Assert.Equal("b.jpg", details.FileName);

        Find<AutoCompleteBox>(window, "NewTagBox").Focus();
        window.KeyTextInput("Cat");
        Press(window, PhysicalKey.Enter);
        await details.SaveCompletion;

        await WaitForAsync(() => root.Counts.Tagged == 5);
        Assert.Equal("5 · 5 tagged", root.CountText);

        // Re-run the untagged search: nothing left.
        Click(window, Find<ToggleButton>(window, "UntaggedButton")); // off
        Click(window, Find<ToggleButton>(window, "UntaggedButton")); // on
        await vm.PhotosLoading;
        Assert.Empty(vm.Photos);
        Assert.Equal("No untagged photos", vm.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task GridTiles_ShowAHeartOnFavourites()
    {
        await using var exifTool = RequireExifTool();
        CreateLibrary();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var (root, year) = await WaitForIndexAsync(vm);
        vm.SelectedFolder = year; // b.jpg, c.jpg, e.jpg
        await vm.PhotosLoading;
        Assert.Equal(0, VisibleHearts(window, vm));

        // Favouriting in the details panel shows the heart straight away.
        var details = await SelectSingleAsync(window, vm, 1);
        Click(window, Find<Button>(window, "FavouriteButton"));
        Assert.True(vm.Photos[1].IsFavourite);
        await details.SaveCompletion;
        Assert.Equal(1, VisibleHearts(window, vm));

        // So does a bulk edit.
        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded);
        Click(window, Find<Button>(window, "BulkFavouriteButton"));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.All(vm.Photos, p => Assert.True(p.IsFavourite));
        Assert.Equal(3, VisibleHearts(window, vm));

        // Coming back to the folder, the hearts come from the index, without reading the files.
        await WaitForAsync(async () => (await vm.Library.Index.GetFavouritesAsync(DirPath)).Count == 3);
        vm.SelectedFolder = root;
        await vm.PhotosLoading;
        Assert.Equal(0, VisibleHearts(window, vm)); // a.jpg isn't a favourite
        vm.SelectedFolder = year;
        await vm.PhotosLoading;
        Assert.All(vm.Photos, p => Assert.True(p.IsFavourite));
        Assert.All(vm.Photos, p => Assert.Null(p.Metadata));
        Assert.Equal(3, VisibleHearts(window, vm));
        window.Close();
    }

    private static int VisibleHearts(Window window, MainWindowViewModel vm)
    {
        window.UpdateLayout();
        // Only tiles showing the current photos: the grid keeps recycled tiles around.
        return FindAll<Border>(window).Count(b => b.Classes.Contains("tileHeart") && b.IsEffectivelyVisible
                                                  && b.DataContext is PhotoItemViewModel p && vm.Photos.Contains(p));
    }

    [AvaloniaFact]
    public async Task Favourites_AreSearchable_AloneOrWithTags()
    {
        await using var exifTool = RequireExifTool();
        CreateLibrary();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var (_, year) = await WaitForIndexAsync(vm);

        // Favourite c.jpg [Dog] and e.jpg [Beach, Dog] in the 2020 folder.
        vm.SelectedFolder = year;
        await vm.PhotosLoading;
        foreach (var index in new[] { 1, 2 })
        {
            var details = await SelectSingleAsync(window, vm, index);
            Click(window, Find<Button>(window, "FavouriteButton"));
            await details.SaveCompletion;
        }
        await WaitForAsync(async () => (await vm.Library.Index.SearchAsync(DirPath, new PhotoQuery { FavouritesOnly = true })).Count == 2);

        var favourites = Find<ToggleButton>(window, "FavouritesButton");
        Click(window, favourites);
        await vm.PhotosLoading;
        Assert.True(vm.IsSearching);
        Assert.Equal(["c.jpg", "e.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal($"2 favourites in {Path.GetFileName(DirPath)}", vm.StatusText);

        // Combined with a tag search.
        vm.SearchText = "beach";
        Find<AutoCompleteBox>(window, "SearchBox").Focus();
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        Assert.Equal(["e.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.True(vm.ShowFavourites);

        // Switching Favourites off leaves the tag search.
        Click(window, favourites);
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg", "e.jpg", "d.jpg"], vm.Photos.Select(p => p.FileName));

        // Esc clears everything.
        Click(window, favourites);
        await vm.PhotosLoading;
        Find<AutoCompleteBox>(window, "SearchBox").Focus();
        Press(window, PhysicalKey.Escape);
        await vm.PhotosLoading;
        Assert.False(vm.IsSearching);
        Assert.False(vm.ShowFavourites);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BulkEdits_AreSearchableStraightAway()
    {
        await using var exifTool = RequireExifTool();
        CreateLibrary();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        await WaitForIndexAsync(vm);

        vm.SearchText = "Dog";
        Find<AutoCompleteBox>(window, "SearchBox").Focus();
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded);

        Find<AutoCompleteBox>(window, "BulkTagBox").Focus();
        window.KeyTextInput("Pets");
        Press(window, PhysicalKey.Enter);
        await WaitForAsync(() => !vm.Operations.IsBusy);

        vm.SearchText = "pets";
        await WaitForAsync(async () => (await vm.Library.Index.SearchAsync(DirPath, new PhotoQuery { Keywords = ["pets"] })).Count == 2);
        Find<AutoCompleteBox>(window, "SearchBox").Focus();
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        Assert.Equal(["c.jpg", "e.jpg"], vm.Photos.Select(p => p.FileName));
        window.Close();
    }
}
