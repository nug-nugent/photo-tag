using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class UndoTests : UiTestBase
{
    private static Task WaitForBulkAsync(MainWindowViewModel vm) => WaitForAsync(() => !vm.Operations.IsBusy);

    private static async Task<BulkDetailsViewModel> SelectAllAsync(Window window, MainWindowViewModel vm)
    {
        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded && bulk.CanEdit);
        return bulk;
    }

    private static async Task AddToAllAsync(Window window, MainWindowViewModel vm, string keyword)
    {
        Find<AutoCompleteBox>(window, "BulkTagBox").Focus();
        window.KeyTextInput(keyword);
        Press(window, PhysicalKey.Enter);
        await WaitForBulkAsync(vm);
    }

    [AvaloniaFact]
    public async Task UndoButton_PutsBackABulkEdit_AndCtrlZInTheGridDoesToo()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg", "Family");
        var b = Photo("b.jpg");
        var c = Photo("c.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var bulk = await SelectAllAsync(window, vm);
        var undo = Find<Button>(window, "UndoButton");
        Assert.False(undo.IsVisible);

        await AddToAllAsync(window, vm, "Beach");
        Assert.True(undo.IsVisible);
        Assert.Equal("Undo adding “Beach” to 3 photos", ToolTip.GetTip(undo));

        Click(window, undo);
        await WaitForBulkAsync(vm);
        Assert.Equal(["Family"], PhotoMetadata.Read(a).Keywords);
        Assert.Empty(PhotoMetadata.Read(b).Keywords);
        Assert.Empty(PhotoMetadata.Read(c).Keywords);
        Assert.Equal("Undone: put back 3 photos.", vm.StatusText);
        Assert.False(undo.IsVisible);
        Assert.Empty(vm.Photos[1].Metadata!.Keywords);
        await WaitForAsync(() => bulk.Keywords.All(k => k.Keyword != "Beach"));

        // Ctrl/⌘+Z in the grid undoes a bulk favourite.
        Click(window, Find<Button>(window, "BulkFavouriteButton"));
        await WaitForBulkAsync(vm);
        Assert.All(vm.Photos, p => Assert.True(p.IsFavourite));
        Find<ScrollViewer>(window, "GridScroller").Focus();
        Press(window, PhysicalKey.Z, CommandKey);
        await WaitForBulkAsync(vm);
        Assert.All([a, b, c], p => Assert.False(PhotoMetadata.Read(p).IsFavourite));
        Assert.All(vm.Photos, p => Assert.False(p.IsFavourite));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Undo_LeavesPhotosEditedSinceAlone()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        await SelectAllAsync(window, vm);
        await AddToAllAsync(window, vm, "Beach");

        // A later edit to one photo keeps the Undo offer, but that photo is then left alone.
        var details = await SelectSingleAsync(window, vm, 0);
        Find<AutoCompleteBox>(window, "NewTagBox").Focus();
        window.KeyTextInput("Later");
        Press(window, PhysicalKey.Enter);
        await details.SaveCompletion;
        Assert.True(vm.Operations.CanUndo);

        Click(window, Find<Button>(window, "UndoButton"));
        await WaitForBulkAsync(vm);
        Assert.Equal(["Beach", "Later"], PhotoMetadata.Read(a).Keywords);
        Assert.Empty(PhotoMetadata.Read(b).Keywords);
        Assert.Equal("Undone: put back 1 photo, left 1 photo alone (edited again since).", vm.StatusText);
        window.Close();
    }
}
