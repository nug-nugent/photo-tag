using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class MultiSelectTests : UiTestBase
{
    [AvaloniaFact]
    public async Task Clicks_WithModifiers_SelectLikeAFileManager()
    {
        for (var i = 0; i < 6; i++) Photo($"p{i}.jpg");
        var (window, vm) = await OpenAsync(writer: null);

        ClickTile(window, 1);
        AssertSelected(window, vm, 1);
        Assert.IsType<PhotoDetailsViewModel>(vm.Details);

        ClickTile(window, 3, CommandKey); // toggle on
        AssertSelected(window, vm, 1, 3);
        Assert.IsType<BulkDetailsViewModel>(vm.Details);
        Assert.Equal("2 selected", vm.SelectionText);

        ClickTile(window, 5, RawInputModifiers.Shift); // range from the last click, replacing the rest
        AssertSelected(window, vm, 3, 4, 5);

        ClickTile(window, 0, CommandKey | RawInputModifiers.Shift); // add a range
        AssertSelected(window, vm, 0, 1, 2, 3, 4, 5);

        ClickTile(window, 4, CommandKey); // toggle off
        AssertSelected(window, vm, 0, 1, 2, 3, 5);

        ClickTile(window, 2); // plain click: just this one
        AssertSelected(window, vm, 2);
        Assert.IsType<PhotoDetailsViewModel>(vm.Details);
        Assert.Null(vm.SelectionText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_MovesAndExtendsTheSelection()
    {
        for (var i = 0; i < 12; i++) Photo($"p{i:D2}.jpg");
        var (window, vm) = await OpenAsync(writer: null);

        ClickTile(window, 0);
        Press(window, PhysicalKey.ArrowRight);
        AssertSelected(window, vm, 1);

        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        AssertSelected(window, vm, 1, 2, 3);

        // Down moves a whole row; Up comes back to the same column.
        Press(window, PhysicalKey.ArrowDown);
        var below = vm.CurrentPhoto!.Index;
        Assert.True(below > 4, $"expected to move down a row, got index {below}");
        AssertSelected(window, vm, below);
        Press(window, PhysicalKey.ArrowUp);
        AssertSelected(window, vm, 3);

        Press(window, PhysicalKey.End, RawInputModifiers.Shift);
        AssertSelected(window, vm, 3, 4, 5, 6, 7, 8, 9, 10, 11);

        Press(window, PhysicalKey.A, CommandKey);
        Assert.Equal(12, vm.SelectedCount);

        Press(window, PhysicalKey.Escape);
        AssertSelected(window, vm);
        Assert.Null(vm.Details);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BulkPanel_SummarisesTags_AndEditsAllSelectedPhotos()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg", "Beach", "Dog");
        var b = Photo("b.jpg", "Beach");
        var c = Photo("c.jpg", "Cat");
        var notSelected = Photo("d.jpg", "Beach");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));

        ClickTile(window, 0);
        ClickTile(window, 2, RawInputModifiers.Shift);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded);

        Assert.Equal("3 photos selected", bulk.Heading);
        Assert.Equal(["Beach 2/3", "Cat 1/3", "Dog 1/3"], bulk.Keywords.Select(k => $"{k.Keyword} {k.CountText}"));
        Assert.Equal(4, bulk.CommonRating); // test images with XMP are rated 4

        // Type a new tag: added to all three, nothing else lost, the unselected photo untouched.
        Find<AutoCompleteBox>(window, "BulkTagBox").Focus();
        window.KeyTextInput("Sunset");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitForBulkAsync(vm);
        Assert.Equal(["Beach", "Dog", "Sunset"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal(["Beach", "Sunset"], PhotoMetadata.Read(b).Keywords);
        Assert.Equal(["Cat", "Sunset"], PhotoMetadata.Read(c).Keywords);
        Assert.Equal(["Beach"], PhotoMetadata.Read(notSelected).Keywords);
        Assert.Contains("Sunset", bulk.Keywords.Where(k => k.IsOnAll).Select(k => k.Keyword));
        Assert.Equal("Updated 3 photos.", vm.StatusText);

        // "+" on a partial tag adds it to the photos that lack it.
        Click(window, ChipButton(window, "Beach", "chipAddAll"));
        await WaitForBulkAsync(vm);
        Assert.Equal(["Cat", "Sunset", "Beach"], PhotoMetadata.Read(c).Keywords);
        Assert.Equal("Updated 1 photo, 2 already up to date.", vm.StatusText);

        // "✕" removes a tag from all of them.
        Click(window, ChipButton(window, "Dog", "chipRemove", excludeClass: "chipAddAll"));
        await WaitForBulkAsync(vm);
        Assert.Equal(["Beach", "Sunset"], PhotoMetadata.Read(a).Keywords);
        Assert.DoesNotContain(bulk.Keywords, k => k.Keyword == "Dog");

        // A star sets the rating on all of them.
        Click(window, FindAll<Button>(window).Where(x => x.Classes.Contains("star")).ElementAt(1));
        await WaitForBulkAsync(vm);
        Assert.All([a, b, c], p => Assert.Equal(2, PhotoMetadata.Read(p).Rating));
        Assert.Equal(4, PhotoMetadata.Read(notSelected).Rating);

        // Back to one photo: its panel shows what bulk editing wrote.
        var single = await SelectSingleAsync(window, vm, 2);
        Assert.Equal(["Cat", "Sunset", "Beach"], single.Keywords);
        Assert.Equal(2, single.Rating);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BulkEdit_CanBeCancelled_AndLocksEditingWhileRunning()
    {
        await using var exifTool = RequireExifTool();
        var paths = Enumerable.Range(0, 60).Select(i => Photo($"p{i:D2}.jpg")).ToList();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));

        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded);

        Find<AutoCompleteBox>(window, "BulkTagBox").Focus();
        window.KeyTextInput("Partial");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(vm.Operations.IsBusy);
        Assert.False(bulk.CanEdit);

        Click(window, Find<Button>(window, "CancelBulkButton"));
        await WaitForBulkAsync(vm);

        var tagged = paths.Count(p => PhotoMetadata.Read(p).Keywords.Contains("Partial"));
        Assert.InRange(tagged, 1, paths.Count - 1);
        Assert.StartsWith("Cancelled. Updated", vm.StatusText);
        Assert.True(bulk.CanEdit);
        Assert.Contains(bulk.Keywords, k => k.Keyword == "Partial" && k.Count == tagged);
        window.Close();
    }

    private static void AssertSelected(Window window, MainWindowViewModel vm, params int[] expected)
    {
        Assert.Equal(expected, vm.SelectedPhotos.Select(p => p.Index));
        // What's drawn matches the model.
        Assert.Equal(expected, Tiles(window).Where(t => t.Classes.Contains("selected"))
            .Select(t => ((PhotoItemViewModel)t.DataContext!).Index));
    }

    private static Task WaitForBulkAsync(MainWindowViewModel vm) => WaitForAsync(() => !vm.Operations.IsBusy);

    /// <summary>A button inside the tag chip for <paramref name="keyword"/>.</summary>
    private static Button ChipButton(Window window, string keyword, string cssClass, string? excludeClass = null) =>
        FindAll<Button>(window).Single(b =>
            b.Classes.Contains(cssClass)
            && (excludeClass is null || !b.Classes.Contains(excludeClass))
            && b.DataContext is BulkKeywordViewModel k && k.Keyword == keyword);
}
