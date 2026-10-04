using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>The large viewer, and the command bar for several photos.</summary>
public sealed class ViewerTests : UiTestBase
{
    [AvaloniaFact]
    public async Task Viewer_StepsThroughPhotos_WithFavouritesAndTagsFromTheKeyboard()
    {
        await using var exifTool = RequireExifTool();
        for (var i = 0; i < 4; i++) Photo($"p{i}.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));

        ClickTile(window, 1);
        Press(window, PhysicalKey.Space);
        var viewer = Assert.IsType<ViewerViewModel>(vm.Viewer);
        Assert.Same(vm.Photos[1], viewer.Current);
        Assert.True(Find<Grid>(window, "ViewerPanel").IsEffectivelyVisible);
        Assert.Equal("2 of 4", viewer.PositionText);

        // → moves, and selects, so the tags beside it are that photo's.
        Press(window, PhysicalKey.ArrowRight);
        var photo = vm.Photos[2];
        Assert.Same(photo, viewer.Current);
        Assert.Same(photo, vm.CurrentPhoto);
        var details = Assert.IsType<PhotoDetailsViewModel>(vm.Details);
        await WaitForAsync(() => details.CanEdit);

        Press(window, PhysicalKey.F);
        await details.SaveCompletion;
        Assert.True(PhotoMetadata.Read(photo.Path).IsFavourite);
        Assert.Equal("1 picked", viewer.PickedText);

        // T jumps to the tag box; typing there doesn't move or favourite.
        Press(window, PhysicalKey.T);
        var tagBox = InViewer<AutoCompleteBox>(window, "NewTagBox");
        Assert.True(IsFocusWithin(window, tagBox));
        window.KeyTextInput("Harbour");
        Press(window, PhysicalKey.Enter);
        await details.SaveCompletion;
        Assert.Equal(["Harbour"], PhotoMetadata.Read(photo.Path).Keywords);
        Assert.Same(photo, viewer.Current);

        // Esc leaves the box, then the viewer, back where it left off.
        Press(window, PhysicalKey.Escape);
        Assert.False(IsFocusWithin(window, tagBox));
        Assert.NotNull(vm.Viewer);
        Press(window, PhysicalKey.Escape);
        Assert.Null(vm.Viewer);
        Assert.False(Find<Grid>(window, "ViewerPanel").IsEffectivelyVisible);
        Assert.Same(photo, vm.CurrentPhoto);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Viewer_EditsPeopleAndPlaces_AsTheSidePanelDoes()
    {
        await using var exifTool = RequireExifTool();
        for (var i = 0; i < 2; i++) Photo($"p{i}.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        ClickTile(window, 0);
        Press(window, PhysicalKey.Space);
        var photo = vm.Photos[0];
        var details = Assert.IsType<PhotoDetailsViewModel>(vm.Details);
        await WaitForAsync(() => details.CanEdit);

        // P jumps to the viewer's own person box (without typing a "p"); Enter saves.
        Press(window, PhysicalKey.P);
        var personBox = InViewer<AutoCompleteBox>(window, "NewPersonBox");
        Assert.True(IsFocusWithin(window, personBox));
        window.KeyTextInput("Mary Smith");
        Press(window, PhysicalKey.Enter);
        await details.SaveCompletion;

        // A town, and a title: saved as the box is left (Esc goes back to the photo).
        InViewer<AutoCompleteBox>(window, "CityBox").Focus();
        window.KeyTextInput("Penzance");
        Press(window, PhysicalKey.Escape);
        InViewer<TextBox>(window, "TitleBox").Focus();
        window.KeyTextInput("Harbour at dusk");
        Press(window, PhysicalKey.Escape);
        await details.SaveCompletion;
        Assert.NotNull(vm.Viewer); // Esc only left the box

        var saved = PhotoMetadata.Read(photo.Path);
        Assert.Equal(["Mary Smith"], saved.People);
        Assert.Equal("Penzance", saved.City);
        Assert.Equal("Harbour at dusk", saved.Title);

        // The next photo starts empty: the boxes follow the viewer.
        Press(window, PhysicalKey.ArrowRight);
        var next = Assert.IsType<PhotoDetailsViewModel>(vm.Details);
        await WaitForAsync(() => next.CanEdit);
        Assert.Empty(next.People);
        Assert.Equal("", InViewer<AutoCompleteBox>(window, "CityBox").Text ?? "");
        Assert.Empty(PhotoMetadata.Read(vm.Photos[1].Path).People);
        window.Close();
    }

    /// <summary>A control in the viewer's panel: the side panel, under it, has one of the same name.</summary>
    private static T InViewer<T>(Window window, string name) where T : Control =>
        Find<Grid>(window, "ViewerPanel").GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the viewer.");

    [AvaloniaFact]
    public async Task DoubleClickingATile_OpensTheViewer_AndTheFilmstripMovesIt()
    {
        for (var i = 0; i < 3; i++) Photo($"p{i}.jpg");
        var (window, vm) = await OpenAsync(writer: null);

        var tile = Tiles(window)[2];
        var centre = tile.TranslatePoint(new Point(tile.Bounds.Width / 2, tile.Bounds.Height / 3), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var viewer = Assert.IsType<ViewerViewModel>(vm.Viewer);
        Assert.Same(vm.Photos[2], viewer.Current);

        var first = await WaitForControlAsync(() => FindAll<StackPanel>(window)
            .FirstOrDefault(s => s.DataContext == vm.Photos[0] && s.Parent is ItemsRepeater { Name: "Filmstrip" }));
        Click(window, first);
        Assert.Same(vm.Photos[0], viewer.Current);
        Assert.Same(vm.Photos[0], vm.CurrentPhoto);

        Click(window, Find<Button>(window, "ViewerBackButton"));
        Assert.Null(vm.Viewer);

        // Two quick clicks either side of Ctrl/⌘+A aren't a double-click: the first didn't select just that tile.
        ClickTile(window, 1);
        Press(window, PhysicalKey.A, CommandKey);
        Assert.Equal(3, vm.SelectedCount);
        ClickTile(window, 1);
        Assert.Null(vm.Viewer);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingThePhotoInTheViewer_GoesFullScreen_AndEscComesBack()
    {
        for (var i = 0; i < 3; i++) Photo($"p{i}.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        var before = window.WindowState;
        ClickTile(window, 0);
        Press(window, PhysicalKey.Space);
        var viewer = Assert.IsType<ViewerViewModel>(vm.Viewer);

        var photo = await WaitForControlAsync(() => FindAll<Panel>(window).FirstOrDefault(p => p.Name == "ViewerPhoto"));
        Click(window, photo);
        Assert.True(viewer.IsFullScreen);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.False(Find<Button>(window, "ViewerBackButton").IsEffectivelyVisible); // just the photo
        Assert.False(Find<ItemsRepeater>(window, "Filmstrip").IsEffectivelyVisible);

        // Arrows still move; a second click comes out.
        Press(window, PhysicalKey.ArrowRight);
        Assert.Same(vm.Photos[1], viewer.Current);
        Click(window, photo);
        Assert.False(viewer.IsFullScreen);
        Assert.Equal(before, window.WindowState);

        // Esc leaves full screen first, then the viewer.
        Click(window, photo);
        Press(window, PhysicalKey.Escape);
        Assert.False(viewer.IsFullScreen);
        Assert.Equal(before, window.WindowState);
        Assert.NotNull(vm.Viewer);
        Press(window, PhysicalKey.Escape);
        Assert.Null(vm.Viewer);

        // Closing the viewer while full screen puts the window back too.
        Press(window, PhysicalKey.Space);
        vm.Viewer!.IsFullScreen = true;
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        vm.CloseViewer();
        Assert.Equal(before, window.WindowState);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingThePreviewInTheDetailsPanel_OpensTheViewer()
    {
        for (var i = 0; i < 3; i++) Photo($"p{i}.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        await SelectSingleAsync(window, vm, 1, waitForEditable: false);

        Click(window, await WaitForControlAsync(() => FindAll<Border>(window).FirstOrDefault(b => b.Name == "DetailsPreview")));
        var viewer = Assert.IsType<ViewerViewModel>(vm.Viewer);
        Assert.Same(vm.Photos[1], viewer.Current);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CommandBar_ActsOnTheSelection_AndViewsJustThosePhotos()
    {
        await using var exifTool = RequireExifTool();
        var paths = Enumerable.Range(0, 4).Select(i => Photo($"p{i}.jpg")).ToList();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        Assert.False(Find<ContentControl>(window, "CommandBar").IsVisible);

        ClickTile(window, 1);
        ClickTile(window, 3, CommandKey);
        Assert.True(Find<ContentControl>(window, "CommandBar").IsEffectivelyVisible);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.CanEdit);

        Click(window, await WaitForControlAsync(() => FindAll<Button>(window).FirstOrDefault(b => b.Name == "CommandBarFavourite")));
        await WaitForAsync(() => !vm.Operations.IsBusy && bulk.AllFavourites);
        Assert.Equal([false, true, false, true], paths.Select(p => PhotoMetadata.Read(p).IsFavourite));

        // The viewer steps through just the two selected, starting at the last one clicked.
        Click(window, Find<Button>(window, "CommandBarViewer"));
        var viewer = Assert.IsType<ViewerViewModel>(vm.Viewer);
        Assert.Equal([vm.Photos[1], vm.Photos[3]], viewer.Photos);
        Assert.Equal("2 of 2", viewer.PositionText);
        Press(window, PhysicalKey.ArrowLeft);
        Assert.Same(vm.Photos[1], viewer.Current);
        Press(window, PhysicalKey.Escape);

        // Clear empties the selection and the bar goes.
        ClickTile(window, 0);
        ClickTile(window, 2, CommandKey);
        Click(window, await WaitForControlAsync(() => FindAll<Button>(window).FirstOrDefault(b => b.Name == "CommandBarClear")));
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(Find<ContentControl>(window, "CommandBar").IsVisible);
        window.Close();
    }
}
