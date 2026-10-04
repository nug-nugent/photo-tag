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
    public async Task SeveralPhotos_PeopleAddedToThem_AreSuggestedAfterwards()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg");
        Photo("b.jpg");
        Photo("c.jpg");
        await writer.WriteAsync(a, new MetadataChanges { People = ["Mum"] }, Ct);
        var (window, vm) = await OpenAsync(writer);
        ClickTile(window, 0);
        ClickTile(window, 1, CommandKey);
        var bulk = Assert.IsType<BulkDetailsViewModel>(vm.Details);
        await WaitForAsync(() => bulk.IsLoaded && bulk.CanEdit);

        // Both ways of adding a person to several photos count: "+" for one only some have, and typing a name.
        Click(window, FindAll<Button>(window).Single(x => x.Classes.Contains("personAddAll")));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Find<AutoCompleteBox>(window, "BulkPersonBox").Focus();
        window.KeyTextInput("Dad");
        Press(window, PhysicalKey.Enter);
        await WaitForAsync(() => !vm.Operations.IsBusy);

        var details = await SelectSingleAsync(window, vm, 2);
        Assert.Equal(["Dad", "Mum"], details.SuggestedPeople);
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

    [AvaloniaFact]
    public async Task SuggestedPeople_AreTheLastTenAdded_AddInOneClick_AndAreRemembered()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var c = Photo("c.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));

        // Twelve people on the first photo.
        var details = await SelectSingleAsync(window, vm, 0);
        Assert.Empty(details.SuggestedPeople);
        Find<AutoCompleteBox>(window, "NewPersonBox").Focus();
        window.KeyTextInput("Tom");
        Press(window, PhysicalKey.Enter);
        await WaitForSaveAsync(details);
        window.KeyTextInput("Ann; Bob; Cat; Dan; Eve; Fay; Gus; Hal; Ivy; Jo; Kit");
        Press(window, PhysicalKey.Enter);
        await WaitForSaveAsync(details);
        Assert.Equal(["Tom", "Ann", "Bob", "Cat", "Dan", "Eve", "Fay", "Gus", "Hal", "Ivy", "Jo", "Kit"], PhotoMetadata.Read(a).People);
        Assert.Empty(details.SuggestedPeople); // it has them all

        // The next photo offers the last ten, most recent first; a click adds one and the next one along takes its place.
        details = await SelectSingleAsync(window, vm, 1);
        Assert.Equal(["Kit", "Jo", "Ivy", "Hal", "Gus", "Fay", "Eve", "Dan", "Cat", "Bob"], details.SuggestedPeople);
        var cat = await WaitForControlAsync(() => FindAll<Button>(window)
            .FirstOrDefault(button => button.Classes.Contains("suggestion") && button.IsEffectivelyVisible && button.DataContext as string == "Cat"));
        Click(window, cat);
        await WaitForSaveAsync(details);
        Assert.Equal(["Cat"], PhotoMetadata.Read(b).People);
        Assert.Equal(["Kit", "Jo", "Ivy", "Hal", "Gus", "Fay", "Eve", "Dan", "Bob", "Ann"], details.SuggestedPeople);
        window.Close();

        // Still there after a restart, with the one just clicked first.
        (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        details = await SelectSingleAsync(window, vm, 2);
        Assert.Equal(["Cat", "Kit", "Jo", "Ivy", "Hal", "Gus", "Fay", "Eve", "Dan", "Bob"], details.SuggestedPeople);
        Assert.Empty(PhotoMetadata.Read(c).People);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ASuggestedPerson_CanBeRemovedFromTheSuggestions_UntilAddedAgain()
    {
        await using var exifTool = RequireExifTool();
        Photo("a.jpg");
        var b = Photo("b.jpg");
        Photo("c.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));

        // A mistyped name, taken off the photo again: it's still suggested.
        var details = await SelectSingleAsync(window, vm, 0);
        Find<AutoCompleteBox>(window, "NewPersonBox").Focus();
        window.KeyTextInput("Ann; Bbo; Bob");
        Press(window, PhysicalKey.Enter);
        await WaitForSaveAsync(details);
        details = await SelectSingleAsync(window, vm, 1);
        Assert.Equal(["Bob", "Bbo", "Ann"], details.SuggestedPeople);

        // Right-click, Remove from suggestions: gone, and still gone after a restart.
        var typo = await WaitForControlAsync(() => FindAll<Button>(window)
            .FirstOrDefault(button => button.Classes.Contains("suggestion") && button.IsEffectivelyVisible && button.DataContext as string == "Bbo"));
        ChooseFromContextMenu(window, typo, "Remove from suggestions");
        Assert.Equal(["Bob", "Ann"], details.SuggestedPeople);
        Assert.Empty(PhotoMetadata.Read(b).People); // nothing written
        window.Close();
        (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        details = await SelectSingleAsync(window, vm, 1);
        Assert.Equal(["Bob", "Ann"], details.SuggestedPeople);

        // Added to a photo again, it's suggested again.
        Find<AutoCompleteBox>(window, "NewPersonBox").Focus();
        window.KeyTextInput("Bbo");
        Press(window, PhysicalKey.Enter);
        await WaitForSaveAsync(details);
        details = await SelectSingleAsync(window, vm, 2);
        Assert.Equal(["Bbo", "Bob", "Ann"], details.SuggestedPeople);
        window.Close();
    }
}
