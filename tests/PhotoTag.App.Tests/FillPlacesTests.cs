using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class FillPlacesTests : UiTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // St Ives, Cornwall.
    private static Task StIves(ExifTool exifTool, string path) =>
        exifTool.ExecuteAsync(["-GPSLatitude=50.2083", "-GPSLatitudeRef=N", "-GPSLongitude=5.4908", "-GPSLongitudeRef=W",
            "-overwrite_original", path], Ct);

    [AvaloniaFact]
    public async Task OnePhoto_FillFromGps_FillsTheEmptyBoxes()
    {
        await using var exifTool = RequireExifTool();
        var withGps = Photo("a.jpg");
        Photo("b.jpg");
        await StIves(exifTool, withGps);
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        var button = Find<Button>(window, "FillFromGpsButton");
        Assert.True(button.IsEffectivelyVisible);
        Click(window, button);
        await WaitForAsync(() => details.FillNote is not null);
        var saves = details.SaveCompletion;
        await WaitForAsync(() => saves.IsCompleted);

        var m = PhotoMetadata.Read(withGps);
        Assert.Equal(("St Ives", "Cornwall", "United Kingdom"), (m.City, m.State, m.Country));
        Assert.Equal(("St Ives", "Cornwall", "United Kingdom"), (details.City, details.State, details.Country));
        Assert.StartsWith("Nearest town: St Ives", details.FillNote);

        // Again: nothing left to fill.
        Click(window, button);
        await WaitForAsync(() => details.FillNote?.Contains("already filled in") == true);

        await SelectSingleAsync(window, vm, 1);
        Assert.False(Find<Button>(window, "FillFromGpsButton").IsEffectivelyVisible); // no GPS
        window.Close();
    }

    [AvaloniaFact]
    public async Task SeveralPhotos_SummaryFirst_ThenFillsOnlyEmptyPlaces_AndUndo()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var c = Photo("c.jpg");
        await StIves(exifTool, a);
        await StIves(exifTool, b);
        await writer.WriteAsync(b, new MetadataChanges { City = "Carbis Bay" }, Ct);
        var (window, vm) = await OpenAsync(writer);
        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded && bulk.CanEdit);

        Click(window, Find<Button>(window, "BulkFillFromGpsButton"));
        await WaitForAsync(() => bulk.IsConfirmingFill);
        Assert.Equal("This fills in the place on 2 of 3 photos: St Ives, Cornwall, United Kingdom. 1 photo has no GPS. "
                     + "Places you've typed are kept, and you can undo it afterwards.", bulk.FillSummary);
        Assert.Null(PhotoMetadata.Read(a).City); // nothing written yet

        Click(window, Find<Button>(window, "BulkConfirmFillButton"));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.Equal(("St Ives", "Cornwall"), (PhotoMetadata.Read(a).City, PhotoMetadata.Read(a).State));
        Assert.Equal(("Carbis Bay", "Cornwall"), (PhotoMetadata.Read(b).City, PhotoMetadata.Read(b).State));
        Assert.Null(PhotoMetadata.Read(c).City);
        Assert.Equal("Undo filling places from GPS on 2 photos", vm.Operations.UndoToolTip);

        Click(window, Find<Button>(window, "UndoButton"));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.Null(PhotoMetadata.Read(a).City);
        Assert.Equal(("Carbis Bay", null), (PhotoMetadata.Read(b).City, PhotoMetadata.Read(b).State));
        window.Close();
    }
}
