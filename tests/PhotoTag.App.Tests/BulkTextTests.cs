using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class BulkTextTests : UiTestBase
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

    [AvaloniaFact]
    public async Task Title_IsSetOnAll_AfterAWarning_AndCanBeClearedAndUndone()
    {
        await using var exifTool = RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var c = Photo("c.jpg");
        await writer.WriteAsync(a, new MetadataChanges { Title = "Old" }, TestContext.Current.CancellationToken);
        var (window, vm) = await OpenAsync(writer);
        var bulk = await SelectAllAsync(window, vm);

        var titleBox = Find<TextBox>(window, "BulkTitleBox");
        Assert.Equal("", titleBox.Text);
        Assert.Equal("Different on each photo. Type to replace them all.", titleBox.PlaceholderText);
        Assert.Equal("Add a description to all of them", Find<TextBox>(window, "BulkDescriptionBox").PlaceholderText);
        Assert.False(Find<Button>(window, "BulkApplyTextButton").IsEffectivelyVisible);

        // Typing shows Apply; Enter shows what will be replaced, and nothing is saved yet.
        titleBox.Focus();
        window.KeyTextInput("Harbour");
        Assert.True(Find<Button>(window, "BulkApplyTextButton").IsEffectivelyVisible);
        Press(window, PhysicalKey.Enter);
        Assert.True(bulk.IsConfirmingText);
        Assert.Equal("This sets the title on all 3 photos, replacing the title 1 of them already has. You can undo it afterwards.",
            bulk.TextWarning);
        Assert.Equal("Old", PhotoMetadata.Read(a).Title);

        Click(window, Find<Button>(window, "BulkConfirmTextButton"));
        await WaitForBulkAsync(vm);
        Assert.All([a, b, c], p => Assert.Equal("Harbour", PhotoMetadata.Read(p).Title));
        Assert.Equal("Updated 3 photos.", vm.StatusText);
        Assert.False(bulk.IsConfirmingText);
        Assert.False(bulk.HasTextEdits);
        Assert.Equal("Harbour", titleBox.Text);
        Assert.Null(titleBox.PlaceholderText);
        // The index knows straight away, so text search finds them.
        await WaitForAsync(async () => (await vm.Library.Index.SearchAsync(DirPath, new PhotoQuery { Terms = ["harbour"] })).Count == 3);

        // Emptying the box clears the title everywhere, after saying so.
        titleBox.Focus();
        Press(window, PhysicalKey.A, CommandKey);
        Press(window, PhysicalKey.Backspace);
        Click(window, Find<Button>(window, "BulkApplyTextButton"));
        Assert.Equal("This clears the title on 3 photos. You can undo it afterwards.", bulk.TextWarning);
        Click(window, Find<Button>(window, "BulkConfirmTextButton"));
        await WaitForBulkAsync(vm);
        Assert.All([a, b, c], p => Assert.Null(PhotoMetadata.Read(p).Title));
        Assert.Equal("Add a title to all of them", titleBox.PlaceholderText);

        // Undo brings them back.
        Click(window, Find<Button>(window, "UndoButton"));
        await WaitForBulkAsync(vm);
        Assert.All([a, b, c], p => Assert.Equal("Harbour", PhotoMetadata.Read(p).Title));
        Assert.Equal("Harbour", titleBox.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancel_PutsTheBoxesBack_WithoutSaving()
    {
        await using var exifTool = RequireExifTool();
        var a = Photo("a.jpg");
        var b = Photo("b.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var bulk = await SelectAllAsync(window, vm);

        Find<TextBox>(window, "BulkDescriptionBox").Focus();
        window.KeyTextInput("Oops");
        Assert.True(bulk.HasTextEdits);
        Click(window, Find<Button>(window, "BulkCancelTextButton"));

        Assert.False(bulk.HasTextEdits);
        Assert.Equal("", bulk.DescriptionField.Text);
        Assert.All([a, b], p => Assert.Null(PhotoMetadata.Read(p).Description));

        // A tag edit while a box has unsaved text leaves the text alone.
        Find<TextBox>(window, "BulkTitleBox").Focus();
        window.KeyTextInput("Draft");
        Find<AutoCompleteBox>(window, "BulkTagBox").Focus();
        window.KeyTextInput("Beach");
        Press(window, PhysicalKey.Enter);
        await WaitForBulkAsync(vm);
        Assert.Equal("Draft", bulk.TitleField.Text);
        Assert.True(bulk.HasTextEdits);
        window.Close();
    }
}
