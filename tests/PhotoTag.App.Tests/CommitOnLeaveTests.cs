using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>What's typed in a box is saved when you leave it, by Tab or a click, not only with Enter.</summary>
public sealed class CommitOnLeaveTests : UiTestBase
{
    [AvaloniaFact]
    public async Task ATagOrPersonTyped_IsAddedWhenTheBoxIsLeft()
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("a.jpg");
        Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        // Tab out of the tag box.
        Find<AutoCompleteBox>(window, "NewTagBox").Focus();
        window.KeyTextInput("Boat");
        Press(window, PhysicalKey.Tab);
        await WaitForAsync(() => details.Keywords.Contains("Boat"));

        // Click into another box after typing a person.
        Find<AutoCompleteBox>(window, "NewPersonBox").Focus();
        window.KeyTextInput("Mary Smith");
        Click(window, Find<TextBox>(window, "TitleBox"));
        await WaitForAsync(() => details.People.Contains("Mary Smith"));
        await details.SaveCompletion;

        var saved = PhotoMetadata.Read(photo);
        Assert.Equal(["Boat"], saved.Keywords);
        Assert.Equal(["Mary Smith"], saved.People);
        Assert.Equal("", Find<AutoCompleteBox>(window, "NewTagBox").Text ?? "");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingAnotherPhoto_SavesWhatWasTyped_ToThePhotoItWasTypedFor()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var first = await SelectSingleAsync(window, vm, 0);

        Find<AutoCompleteBox>(window, "NewTagBox").Focus();
        window.KeyTextInput("Boat");
        Find<TextBox>(window, "TitleBox").Focus(); // leaves the tag box: "Boat" is added
        window.KeyTextInput("Harbour at dusk");
        Find<AutoCompleteBox>(window, "NewPersonBox").Focus(); // leaves the title box: saved
        window.KeyTextInput("Mary Smith");

        ClickTile(window, 1); // still typing in the person box
        await WaitForAsync(() => vm.Details is PhotoDetailsViewModel { IsLoaded: true } d && d != first);
        await first.SaveCompletion;

        var saved = PhotoMetadata.Read(a);
        Assert.Equal(["Boat"], saved.Keywords);
        Assert.Equal("Harbour at dusk", saved.Title);
        Assert.Equal(["Mary Smith"], saved.People);
        var other = PhotoMetadata.Read(b);
        Assert.Empty(other.Keywords);
        Assert.Empty(other.People);
        Assert.Null(other.Title);
        window.Close();
    }

    [AvaloniaFact]
    public async Task InTheViewer_TheNextButtonSavesWhatWasTyped()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        ClickTile(window, 0);
        Press(window, PhysicalKey.Space);
        var first = Assert.IsType<PhotoDetailsViewModel>(vm.Details);
        await WaitForAsync(() => first.CanEdit);

        InViewer<AutoCompleteBox>(window, "NewPersonBox").Focus();
        window.KeyTextInput("Mary Smith");
        InViewer<AutoCompleteBox>(window, "CityBox").Focus();
        window.KeyTextInput("Penzance");
        Click(window, Find<Button>(window, "ViewerNextButton"));
        Assert.Same(vm.Photos[1], vm.Viewer!.Current);
        await WaitForAsync(() => !first.IsSaving && PhotoMetadata.Read(a).City is not null);

        var saved = PhotoMetadata.Read(a);
        Assert.Equal(["Mary Smith"], saved.People);
        Assert.Equal("Penzance", saved.City);
        Assert.Empty(PhotoMetadata.Read(b).People);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ATagTypedForSeveralPhotos_IsAddedToAllWhenTheBoxIsLeft()
    {
        await using var exifTool = RequireExifTool();
        var paths = Enumerable.Range(0, 3).Select(i => Photo($"p{i}.jpg")).ToList();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded);

        Find<AutoCompleteBox>(window, "BulkTagBox").Focus();
        window.KeyTextInput("Wedding");
        Press(window, PhysicalKey.Tab);
        await WaitForAsync(() => vm.Operations.IsBusy || bulk.Keywords.Any(k => k.Keyword == "Wedding"));
        await WaitForAsync(() => !vm.Operations.IsBusy);

        Assert.All(paths, p => Assert.Equal(["Wedding"], PhotoMetadata.Read(p).Keywords));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ATitleBeingTyped_IsSavedToItsPhoto_WhenAnotherTileIsClicked()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var first = await SelectSingleAsync(window, vm, 0);

        Find<TextBox>(window, "TitleBox").Focus();
        window.KeyTextInput("Harbour at dusk");
        ClickTile(window, 1);
        await WaitForAsync(() => vm.Details is PhotoDetailsViewModel { IsLoaded: true } d && d != first);
        await first.SaveCompletion;

        Assert.Equal("Harbour at dusk", PhotoMetadata.Read(a).Title);
        Assert.Null(PhotoMetadata.Read(b).Title);
        Assert.Equal("", Find<TextBox>(window, "TitleBox").Text ?? ""); // b's title, which is empty
        window.Close();
    }

    [AvaloniaFact]
    public async Task SeveralTagsOrPeople_CanBeAddedAtOnce_WithSemicolons()
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("a.jpg");
        var others = Enumerable.Range(0, 2).Select(i => Photo($"p{i}.jpg")).ToList();
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        Find<AutoCompleteBox>(window, "NewTagBox").Focus();
        window.KeyTextInput("Beach; Family;dog, beach");
        Press(window, PhysicalKey.Enter);
        Find<AutoCompleteBox>(window, "NewPersonBox").Focus();
        window.KeyTextInput("Mary Smith;Dad");
        Press(window, PhysicalKey.Tab);
        await WaitForAsync(() => details.People.Count == 2);
        await details.SaveCompletion;
        Assert.Equal(["Beach", "Family", "dog"], PhotoMetadata.Read(photo).Keywords);
        Assert.Equal(["Mary Smith", "Dad"], PhotoMetadata.Read(photo).People);

        // And for several photos at once.
        ClickTile(window, 1);
        ClickTile(window, 2, RawInputModifiers.Shift);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded);
        Find<AutoCompleteBox>(window, "BulkTagBox").Focus();
        window.KeyTextInput("Wedding; Church");
        Press(window, PhysicalKey.Enter);
        await WaitForAsync(() => !vm.Operations.IsBusy && bulk.Keywords.Count == 2);
        Assert.All(others, p => Assert.Equal(["Wedding", "Church"], PhotoMetadata.Read(p).Keywords));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingASuggestion_AddsIt_NotWhatWasHalfTyped()
    {
        await using var exifTool = RequireExifTool();
        Photo("a.jpg", "Beach");
        var photo = Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        await vm.Library.ScanCompletion;
        var details = await SelectSingleAsync(window, vm, 1);

        var box = Find<AutoCompleteBox>(window, "NewTagBox");
        box.Focus();
        window.KeyTextInput("Bea");
        await WaitForAsync(() => box.IsDropDownOpen);
        var list = box.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single().Child!;
        var item = await WaitForControlAsync(() => list.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(i => i.DataContext as string == "Beach"));
        Click(window, item);

        // Choosing a suggestion leaves the box for its list, so it's added straight away: the suggestion, not "Bea".
        await WaitForAsync(() => details.Keywords.Count > 0);
        await details.SaveCompletion;
        Assert.Equal(["Beach"], details.Keywords);
        Assert.Equal(["Beach"], PhotoMetadata.Read(photo).Keywords);
        window.Close();
    }

    /// <summary>A control in the viewer's panel: the side panel, under it, has one of the same name.</summary>
    private static T InViewer<T>(Window window, string name) where T : Control =>
        Find<Grid>(window, "ViewerPanel").GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the viewer.");
}
