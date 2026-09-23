using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>
/// Editing one photo: typing, clicking and focus changes go through Avalonia's input
/// pipeline, and edits are written to real files by real ExifTool.
/// </summary>
public sealed class TagEditingTests : UiTestBase
{
    [AvaloniaFact]
    public async Task EditsInThePanel_AreWrittenToTheFile()
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("photo.jpg", "Garden");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        // Type two tags into the tag box, comma-separated, and press Enter.
        var tagBox = Find<AutoCompleteBox>(window, "NewTagBox");
        tagBox.Focus();
        window.KeyTextInput("Beach, Sunset");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitForSaveAsync(details);
        Assert.Equal(["Garden", "Beach", "Sunset"], PhotoMetadata.Read(photo).Keywords);
        Assert.Equal("", details.NewKeyword);
        Assert.True(IsFocusWithin(window, tagBox), "focus should stay in the tag box so more tags can be typed");

        // Remove "Garden" with its ✕ button.
        Click(window, FindAll<Button>(window).First(b => b.Classes.Contains("chipRemove")));
        await WaitForSaveAsync(details);
        Assert.Equal(["Beach", "Sunset"], PhotoMetadata.Read(photo).Keywords);

        // Click the third star.
        Click(window, FindAll<Button>(window).Where(b => b.Classes.Contains("star")).ElementAt(2));
        await WaitForSaveAsync(details);
        Assert.Equal(3, PhotoMetadata.Read(photo).Rating);

        // Title and description save when focus leaves the box.
        var titleBox = Find<TextBox>(window, "TitleBox");
        var descriptionBox = Find<TextBox>(window, "DescriptionBox");
        titleBox.Focus();
        window.KeyTextInput("Christmas morning");
        descriptionBox.Focus();
        await WaitForSaveAsync(details);
        Assert.Equal("Christmas morning", PhotoMetadata.Read(photo).Title);

        window.KeyTextInput("Line one");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput("Line two & more");
        titleBox.Focus();
        await WaitForSaveAsync(details);
        Assert.Equal("Line one\nLine two & more", PhotoMetadata.Read(photo).Description); // stored with \n on every OS
        Assert.Equal("Christmas morning", PhotoMetadata.Read(photo).Title); // unchanged by the description save

        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingTheCurrentRating_ClearsIt()
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("photo.jpg", "Rated"); // test images with XMP have rating 4
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);
        Assert.Equal(4, details.Rating);

        Click(window, FindAll<Button>(window).Where(b => b.Classes.Contains("star")).ElementAt(3));
        await WaitForSaveAsync(details);

        Assert.Equal(0, details.Rating);
        Assert.Null(PhotoMetadata.Read(photo).Rating);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutExifTool_PanelIsReadOnly_AndExplainsWhy()
    {
        Photo("photo.jpg", "Existing");
        var (window, vm) = await OpenAsync(writer: null);
        var details = await SelectSingleAsync(window, vm, 0, waitForEditable: false);
        await WaitForAsync(() => details.IsLoaded);

        Assert.False(details.CanEdit);
        Assert.Equal(["Existing"], details.Keywords);
        Assert.False(Find<AutoCompleteBox>(window, "NewTagBox").IsEffectivelyEnabled);
        Assert.False(Find<TextBox>(window, "TitleBox").IsEffectivelyEnabled);
        Assert.Contains(FindAll<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text?.Contains("needs ExifTool") == true);
        window.Close();
    }

    private static async Task WaitForSaveAsync(PhotoDetailsViewModel details)
    {
        var saves = details.SaveCompletion;
        await WaitForAsync(() => saves.IsCompleted);
        Assert.False(details.SaveFailed, details.SaveStatus);
    }
}
