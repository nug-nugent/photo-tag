using Avalonia.Headless.XUnit;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;
using PhotoTag.Core.Tests;

namespace PhotoTag.App.Tests;

/// <summary>Photos and folders changed by other apps (or copied in, or deleted) while PhotoTag is open.</summary>
public sealed class OutsideChangesTests : UiTestBase
{
    [AvaloniaFact]
    public async Task APhotoCopiedIn_Appears_AndTheSelectionStays()
    {
        Photo("a.jpg");
        Photo("b.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        var first = vm.Photos[0];
        var selected = (await SelectSingleAsync(window, vm, 1, waitForEditable: false)).Photo;

        Photo("c.jpg", "New");

        await WaitForAsync(() => vm.Photos.Count == 3);
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Same(first, vm.Photos[0]);
        Assert.True(selected.IsSelected);
        Assert.Same(selected, Assert.IsType<PhotoDetailsViewModel>(vm.Details).Photo);
        await WaitForAsync(() => vm.Photos[2].Keywords.SequenceEqual(["New"]));
        await WaitForAsync(() => vm.RootFolders[0].CountText == "1 / 3");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ADeletedPhoto_Disappears_AndLeavesTheSelection()
    {
        Photo("a.jpg");
        var doomed = Photo("b.jpg", "Gone");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        await SelectSingleAsync(window, vm, 1, waitForEditable: false);

        File.Delete(doomed);

        await WaitForAsync(() => vm.Photos.Count == 1);
        Assert.Equal(["a.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Null(vm.Details);
        Assert.Equal(0, vm.SelectedCount);
        await WaitForAsync(() => vm.RootFolders[0].CountText == "0 / 1");
        window.Close();
    }

    [AvaloniaFact]
    public async Task APhotoRetaggedByAnotherApp_UpdatesItsTileAndThePanel()
    {
        var path = Photo("a.jpg", "Before");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        var details = await SelectSingleAsync(window, vm, 0, waitForEditable: false);
        await WaitForAsync(() => details.IsLoaded);
        Assert.Equal(["Before"], details.Keywords);

        File.WriteAllBytes(path, TestImages.Jpeg(120, 90, xmpKeywords: ["After", "Another app"]));

        await WaitForAsync(() => details.Keywords.SequenceEqual(["After", "Another app"]));
        await WaitForAsync(() => vm.Photos[0].Keywords.SequenceEqual(["After", "Another app"]));
        Assert.Same(details, vm.Details);
        window.Close();
    }

    [AvaloniaFact]
    public async Task APhotoRetaggedWhileSeveralAreSelected_IsReadAgain()
    {
        var path = Photo("a.jpg", "Before");
        Photo("b.jpg", "Before");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        ClickTile(window, 0);
        Press(window, Avalonia.Input.PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded);
        Assert.Equal([("Before", 2)], bulk.Keywords.Select(k => (k.Keyword, k.Count)));

        File.WriteAllBytes(path, TestImages.Jpeg(120, 90, xmpKeywords: ["After"]));

        await WaitForAsync(() => bulk.Keywords.Any(k => k.Keyword == "After"));
        Assert.Equal([("After", 1), ("Before", 1)], bulk.Keywords.Select(k => (k.Keyword, k.Count)).Order());
        Assert.Same(bulk, vm.Details);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NewFoldersAppearInTheTree_AndADeletedFolderOnScreen_GoesBackUp()
    {
        Photo(Path.Combine("2020", "a.jpg"));
        Photo(Path.Combine("2020", "Summer", "c.jpg"), "Sun");
        Photo("b.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        var root = vm.RootFolders.Single();
        await root.ChildrenLoading;

        Directory.CreateDirectory(Path.Combine(DirPath, "2021"));
        await WaitForAsync(() => root.Children.Select(c => c.Name).SequenceEqual(["2020", "2021"]));

        vm.SelectedFolder = root.Children[0];
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg"], vm.Photos.Select(p => p.FileName));

        Directory.Delete(Path.Combine(DirPath, "2020"), recursive: true);

        await WaitForAsync(() => vm.SelectedFolder == root);
        await vm.PhotosLoading;
        Assert.Equal(["b.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal(["2021"], root.Children.Select(c => c.Name));
        await WaitForAsync(() => root.CountText == "0 / 1"); // the subfolder's photos went too
        window.Close();
    }

    [AvaloniaFact]
    public async Task PhotoTagsOwnSaves_AreNotTakenForOutsideChanges()
    {
        await using var exifTool = RequireExifTool();
        Photo("a.jpg");
        var (window, vm) = await OpenAsync(new Core.PhotoMetadataWriter(exifTool));
        await vm.Library.ScanCompletion;
        var details = await SelectSingleAsync(window, vm, 0);
        await WaitForAsync(() => details.Preview is not null);
        var preview = details.Preview;

        details.NewKeyword = "Mine";
        await details.AddKeywordCommand.ExecuteAsync(null);
        await details.SaveCompletion;

        // Give the watcher time to report the write, then let it be handled.
        await Task.Delay(2500, TestContext.Current.CancellationToken);
        await WaitForAsync(() => vm.Library.RefreshCompletion.IsCompleted);
        await vm.FilesChangedHandling;

        Assert.Same(preview, details.Preview); // not reloaded
        Assert.Equal(["Mine"], details.Keywords);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATagTheOtherPcAddedJustBefore_IsKeptWhenThisPanelSaves(bool adding)
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("a.jpg", "Beach");
        // The other PC's version of the photo: "Boat" added. Made outside the library, so nothing notices it yet.
        using var otherPc = new TempDir();
        var theirs = Path.Combine(otherPc.Path, "a.jpg");
        File.Copy(photo, theirs);
        await new PhotoMetadataWriter(exifTool).WriteAsync(theirs, new MetadataChanges { Keywords = ["Beach", "Boat"] }, Ct);

        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        await vm.Library.ScanCompletion;
        var details = await SelectSingleAsync(window, vm, 0);
        Assert.Equal(["Beach"], details.Keywords);

        // The other PC saves, and this panel saves before anything has reloaded it.
        File.Copy(theirs, photo, overwrite: true);
        if (adding)
        {
            details.NewKeyword = "Mine";
            await details.AddKeywordCommand.ExecuteAsync(null);
        }
        else
        {
            await details.RemoveKeywordCommand.ExecuteAsync("Beach");
        }
        await details.SaveCompletion;

        string[] expected = adding ? ["Beach", "Boat", "Mine"] : ["Boat"];
        Assert.Equal(expected, PhotoMetadata.Read(photo).Keywords);
        Assert.Equal(expected, details.Keywords); // and the panel shows it
        window.Close();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
