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

        // The heart makes it a favourite.
        Click(window, Find<Button>(window, "FavouriteButton"));
        await WaitForSaveAsync(details);
        Assert.True(PhotoMetadata.Read(photo).IsFavourite);

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
    public async Task ClickingTheHeartAgain_Unfavourites()
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("photo.jpg", "Tagged");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);
        Assert.False(details.IsFavourite); // test images with XMP are rated 4★
        var heart = Find<Button>(window, "FavouriteButton");

        Click(window, heart);
        await WaitForSaveAsync(details);
        Assert.True(details.IsFavourite);
        Assert.Contains("on", heart.Classes); // filled and red

        Click(window, heart);
        await WaitForSaveAsync(details);
        Assert.False(details.IsFavourite);
        Assert.DoesNotContain("on", heart.Classes);
        Assert.False(PhotoMetadata.Read(photo).IsFavourite);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(ExifToolStatus.NotFound, "needs ExifTool")]
    [InlineData(ExifToolStatus.PerlMissing, "needs Perl")]
    public async Task WithoutExifTool_PanelIsReadOnly_AndExplainsWhy(ExifToolStatus status, string explanation)
    {
        Photo("photo.jpg", "Existing");
        var (window, vm) = await OpenAsync(writer: null, exifToolStatus: status);
        var details = await SelectSingleAsync(window, vm, 0, waitForEditable: false);
        await WaitForAsync(() => details.IsLoaded);

        Assert.False(details.CanEdit);
        Assert.Equal(["Existing"], details.Keywords);
        Assert.False(Find<AutoCompleteBox>(window, "NewTagBox").IsEffectivelyEnabled);
        Assert.False(Find<TextBox>(window, "TitleBox").IsEffectivelyEnabled);
        Assert.Contains(FindAll<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text?.Contains(explanation) == true);
        window.Close();
    }

    private static async Task WaitForSaveAsync(PhotoDetailsViewModel details)
    {
        var saves = details.SaveCompletion;
        await WaitForAsync(() => saves.IsCompleted);
        Assert.False(details.SaveFailed, details.SaveStatus);
    }
}
