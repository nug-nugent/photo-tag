using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class TagManagerTests : UiTestBase
{
    private static async Task<(Flyout Flyout, TagManagerViewModel Tags)> OpenTagsAsync(Window window, MainWindowViewModel vm)
    {
        await vm.Library.ScanCompletion;
        var button = Find<Button>(window, "TagsButton");
        var flyout = (Flyout)button.Flyout!;
        Click(window, button);
        Assert.True(flyout.IsOpen);
        await vm.TagManager.Loading;
        return (flyout, vm.TagManager);
    }

    private static T InRow<T>(Flyout flyout, string keyword, string className) where T : Control =>
        ((Control)flyout.Content!).GetVisualDescendants().OfType<T>()
            .Single(c => c.Classes.Contains(className) && c.DataContext is TagRowViewModel row && row.Keyword == keyword);

    private static string Summary(TagManagerViewModel tags) =>
        string.Join(", ", tags.Tags.Select(t => $"{t.Keyword} {t.Count}"));

    [AvaloniaFact]
    public async Task RenameMergeTidyAndDelete_ChangeEveryPhotoInTheLibrary()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg", "Beach", "Dog");
        var b = Photo(Path.Combine("2020", "b.jpg"), "beach");
        var c = Photo(Path.Combine("2020", "c.jpg"), "Seaside");
        var d = Photo(Path.Combine("2020", "d.jpg"), "Dog");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var (flyout, tags) = await OpenTagsAsync(window, vm);

        Assert.Equal("Beach 2, Dog 2, Seaside 1", Summary(tags));
        Assert.Contains("“beach”", tags.Tags[0].SpellingsText);

        // Rename "Seaside" to "Beach": the two merge. The box starts with the old name selected, so typing replaces it.
        Click(window, InRow<Button>(flyout, "Seaside", "tagRename"));
        var box = InRow<TextBox>(flyout, "Seaside", "tagNewName");
        await WaitForAsync(() => IsFocusWithin(window, box));
        window.KeyTextInput("Beach");
        Press(window, PhysicalKey.Enter);
        await WaitForAsync(() => Summary(tags) == "Beach 3, Dog 2");
        Assert.Equal(["Beach"], PhotoMetadata.Read(c).Keywords);
        Assert.Equal(["beach"], PhotoMetadata.Read(b).Keywords); // untouched: it never had "Seaside"
        Assert.DoesNotContain("Seaside", vm.KeywordSuggestions);

        // Renaming "Beach" to itself tidies the other spelling.
        Click(window, InRow<Button>(flyout, "Beach", "tagRename"));
        Click(window, InRow<Button>(flyout, "Beach", "tagConfirmRename"));
        await WaitForAsync(() => tags.Tags.Count > 0 && tags.Tags[0].SpellingsText is null);
        Assert.Equal(["Beach"], PhotoMetadata.Read(b).Keywords);

        // Delete asks first, then removes the tag from every photo, including the one on screen.
        Click(window, InRow<Button>(flyout, "Dog", "tagDelete"));
        var dog = tags.Tags.Single(t => t.Keyword == "Dog");
        Assert.True(dog.IsConfirmingDelete);
        Assert.Contains("from 2 photos", dog.DeleteQuestion);
        Assert.Equal(["Beach", "Dog"], PhotoMetadata.Read(a).Keywords);
        Click(window, InRow<Button>(flyout, "Dog", "tagConfirmDelete"));
        await WaitForAsync(() => Summary(tags) == "Beach 3");
        Assert.Equal(["Beach"], PhotoMetadata.Read(a).Keywords);
        Assert.Empty(PhotoMetadata.Read(d).Keywords);
        Assert.Equal(["Beach"], vm.Photos.Single(p => p.Path == a).Metadata?.Keywords);
        Assert.DoesNotContain("Dog", vm.KeywordSuggestions);
        await WaitForAsync(() => vm.RootFolders.Single().CountText == "4 · 3 tagged");

        // Undo brings the deleted tag back everywhere.
        flyout.Hide();
        Click(window, Find<Button>(window, "UndoButton"));
        await WaitForAsync(() => !vm.Operations.IsBusy);
        Assert.Equal(["Beach", "Dog"], PhotoMetadata.Read(a).Keywords);
        Assert.Equal(["Dog"], PhotoMetadata.Read(d).Keywords);
        Assert.Contains("Dog", vm.KeywordSuggestions);
        await WaitForAsync(() => vm.RootFolders.Single().CountText == "4 · 4 tagged");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Rename_RejectsEmptyNamesAndCommas_AndFilterNarrowsTheList()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg", "Beach", "Dog");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var (flyout, tags) = await OpenTagsAsync(window, vm);

        var dog = tags.Tags.Single(t => t.Keyword == "Dog");
        dog.StartRenameCommand.Execute(null);
        dog.NewName = "Dogs, Cats";
        await dog.RenameCommand.ExecuteAsync(null);
        Assert.Equal("Tags can't contain commas.", dog.Error);
        dog.NewName = "  ";
        await dog.RenameCommand.ExecuteAsync(null);
        Assert.Equal("Enter a name for the tag.", dog.Error);
        Assert.Equal(["Beach", "Dog"], PhotoMetadata.Read(a).Keywords);

        tags.Filter = "BEA";
        Assert.Equal("Beach 1", Summary(tags));
        tags.Filter = "cat";
        Assert.Equal("No tags match “cat”.", tags.EmptyText);

        flyout.Hide();
        Assert.False(dog.IsRenaming);
        window.Close();
    }
}
