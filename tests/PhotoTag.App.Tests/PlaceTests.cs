using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class PlaceTests : UiTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task PlaceBoxes_SaveWhenFocusLeaves_AndAreSuggestedAndSearchable()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        await writer.WriteAsync(b, new MetadataChanges { City = "Penzance" }, Ct);
        var (window, vm) = await OpenAsync(writer);
        await vm.Library.ScanCompletion;
        var details = await SelectSingleAsync(window, vm, 0);
        Assert.Contains("Penzance", details.CitySuggestions); // from the index

        Find<AutoCompleteBox>(window, "CityBox").Focus();
        window.KeyTextInput("St Ives");
        Assert.Null(details.SaveStatus); // nothing saved while typing
        Find<AutoCompleteBox>(window, "CountryBox").Focus();
        window.KeyTextInput("UK");
        Find<TextBox>(window, "TitleBox").Focus();
        var saves = details.SaveCompletion; // the country save, queued after the city one
        await WaitForAsync(() => saves.IsCompleted); // reading while ExifTool rewrites the file would fail on Windows

        Assert.Equal(("St Ives", "UK"), (PhotoMetadata.Read(a).City, PhotoMetadata.Read(a).Country));
        Assert.False(details.SaveFailed, details.SaveStatus);
        Assert.Contains("St Ives", details.CitySuggestions);
        await WaitForAsync(async () => (await vm.Library.Index.SearchAsync(DirPath, new PhotoQuery { Terms = ["ives"] })).Count == 1);

        // Another photo shows its own place.
        var other = await SelectSingleAsync(window, vm, 1);
        Assert.Equal(("Penzance", null), (other.City, other.Country));
        window.Close();
    }

    [AvaloniaFact]
    public async Task BulkPlace_WarnsThenSetsAll_AndCanBeUndone()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        await writer.WriteAsync(a, new MetadataChanges { City = "Penzance", Country = "UK" }, Ct);
        var (window, vm) = await OpenAsync(writer);
        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded && bulk.CanEdit);

        Assert.Equal("Different on each photo. Type to replace them all.", bulk.CityField.Placeholder);
        Assert.Equal("Add a state/province to all of them", bulk.StateField.Placeholder);

        Find<AutoCompleteBox>(window, "BulkCityBox").Focus();
        window.KeyTextInput("St Ives");
        Press(window, PhysicalKey.Enter);
        Assert.Equal("This sets the city on all 2 photos, replacing the city 1 of them already has. You can undo it afterwards.",
            bulk.TextWarning);
        Click(window, Find<Button>(window, "BulkConfirmTextButton"));
        await WaitForAsync(() => !vm.Operations.IsBusy);

        Assert.All([a, b], p => Assert.Equal("St Ives", PhotoMetadata.Read(p).City));
        Assert.Equal("UK", PhotoMetadata.Read(a).Country); // other fields untouched
        Assert.Null(PhotoMetadata.Read(b).Country);
        Assert.Equal("St Ives", bulk.CityField.Text);
        Assert.Equal("Undo setting the city on 2 photos", vm.Operations.UndoToolTip);

        Click(window, Find<Button>(window, "UndoButton"));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.Equal("Penzance", PhotoMetadata.Read(a).City);
        Assert.Null(PhotoMetadata.Read(b).City);
        window.Close();
    }
}
