using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class PeopleTests : UiTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitForSaveAsync(PhotoDetailsViewModel details)
    {
        var saves = details.SaveCompletion;
        await WaitForAsync(() => saves.IsCompleted);
        Assert.False(details.SaveFailed, details.SaveStatus);
    }

    [AvaloniaFact]
    public async Task OnePhoto_PeopleAreAddedAndRemovedLikeTags_AndSearchable()
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("a.jpg", "Beach");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        Find<AutoCompleteBox>(window, "NewPersonBox").Focus();
        window.KeyTextInput("Mary Smith, Dad");
        Press(window, PhysicalKey.Enter);
        await WaitForSaveAsync(details);
        Assert.Equal(["Mary Smith", "Dad"], PhotoMetadata.Read(photo).People);
        Assert.Equal(["Beach"], PhotoMetadata.Read(photo).Keywords);
        Assert.Contains("Mary Smith", details.PeopleSuggestions);

        var removeDad = FindAll<Button>(window).Single(b => b.Classes.Contains("personRemove") && b.CommandParameter is "Dad");
        Click(window, removeDad);
        await WaitForSaveAsync(details);
        Assert.Equal(["Mary Smith"], PhotoMetadata.Read(photo).People);
        Assert.Equal(["Beach"], PhotoMetadata.Read(photo).Keywords);

        // Search matches any part of a name.
        await WaitForAsync(async () => (await vm.Library.Index.SearchAsync(DirPath, new PhotoQuery { Terms = ["smith"] })).Count == 1);
        vm.SearchText = "smith";
        Find<AutoCompleteBox>(window, "SearchBox").Focus();
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        Assert.Equal(["a.jpg"], vm.Photos.Select(p => p.FileName));
        window.Close();
    }

    [AvaloniaFact]
    public async Task SeveralPhotos_ShowPeopleCounts_AndAddOrRemoveForAll()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        await writer.WriteAsync(a, new MetadataChanges { People = ["Mum"] }, Ct);
        var (window, vm) = await OpenAsync(writer);
        ClickTile(window, 0);
        Press(window, PhysicalKey.A, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded && bulk.CanEdit);
        Assert.Equal(["Mum 1/2"], bulk.People.Select(p => $"{p.Keyword} {p.CountText}"));

        // "+" gives the person to the photo that lacks them.
        Click(window, FindAll<Button>(window).Single(x => x.Classes.Contains("personAddAll")));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.Equal(["Mum"], PhotoMetadata.Read(b).People);
        Assert.Equal(["Mum "], bulk.People.Select(p => $"{p.Keyword} {p.CountText}"));

        Find<AutoCompleteBox>(window, "BulkPersonBox").Focus();
        window.KeyTextInput("Dad");
        Press(window, PhysicalKey.Enter);
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.All([a, b], p => Assert.Equal(["Mum", "Dad"], PhotoMetadata.Read(p).People));

        Click(window, FindAll<Button>(window).Single(x => x.Classes.Contains("personRemove")
                                                         && x.DataContext is BulkKeywordViewModel { Keyword: "Mum" }));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.All([a, b], p => Assert.Equal(["Dad"], PhotoMetadata.Read(p).People));
        Assert.All([a, b], p => Assert.Empty(PhotoMetadata.Read(p).Keywords));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panel_PeopleSide_RenamesMergesAndDeletes_LeavingTagsAlone()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg", "Mum"); // a tag with the same name, which must stay
        var b = Photo(Path.Combine("2020", "b.jpg"));
        await writer.WriteAsync(a, new MetadataChanges { People = ["Mum", "Dad"] }, Ct);
        await writer.WriteAsync(b, new MetadataChanges { People = ["Mary Smith"] }, Ct);
        var (window, vm) = await OpenAsync(writer);
        await vm.Library.ScanCompletion;

        var button = Find<Button>(window, "TagsButton");
        var flyout = (Flyout)button.Flyout!;
        Click(window, button);
        await vm.TagManager.Loading;
        var content = (Control)flyout.Content!;
        Click(window, content.GetVisualDescendants().OfType<TabStripItem>().Single(t => Equals(t.Content, "People")));
        await WaitForAsync(() => vm.TagManager.Heading?.StartsWith("People") == true);
        await vm.TagManager.Loading;
        var tags = vm.TagManager;
        Assert.Equal(["Dad 1", "Mary Smith 1", "Mum 1"], tags.Tags.Select(t => $"{t.Keyword} {t.Count}").Order());

        // Merge "Mum" into "Mary Smith".
        var mum = tags.Tags.Single(t => t.Keyword == "Mum");
        mum.StartRenameCommand.Execute(null);
        mum.NewName = "Mary Smith";
        await mum.RenameCommand.ExecuteAsync(null);
        await WaitForAsync(() => !vm.Operations.IsBusy);
        await WaitForAsync(() => tags.Tags.Any(t => t is { Keyword: "Mary Smith", Count: 2 }));
        Assert.Equal(["Mary Smith", "Dad"], PhotoMetadata.Read(a).People);
        Assert.Equal(["Mum"], PhotoMetadata.Read(a).Keywords); // the tag is untouched
        Assert.DoesNotContain("Mum", (await vm.Library.Index.GetValuesAsync(ListField.People)).Select(p => p.Keyword));

        // Delete "Dad" everywhere, then undo.
        var dad = tags.Tags.Single(t => t.Keyword == "Dad");
        dad.StartDeleteCommand.Execute(null);
        await dad.DeleteCommand.ExecuteAsync(null);
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.Equal(["Mary Smith"], PhotoMetadata.Read(a).People);
        flyout.Hide();
        Click(window, Find<Button>(window, "UndoButton"));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.Equal(["Mary Smith", "Dad"], PhotoMetadata.Read(a).People);

        // The tags side still shows tags.
        tags.Field = ListField.Tags;
        tags.Open();
        await tags.Loading;
        Assert.Equal(["Mum"], tags.Tags.Select(t => t.Keyword));
        window.Close();
    }
}
