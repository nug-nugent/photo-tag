using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class SettingsTests : UiTestBase
{
    private static readonly DateTime LongAgo = new(2021, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public async Task DateModified_UpdatesByDefault_AndTheSettingKeepsIt()
    {
        await using var exifTool = RequireExifTool();
        var photo = Photo("photo.jpg", "Rated"); // rated 4
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        // Default: a rating change moves the date on, so backup tools notice.
        File.SetLastWriteTimeUtc(photo, LongAgo);
        Click(window, Star(window, 3));
        await details.SaveCompletion;
        Assert.True(File.GetLastWriteTimeUtc(photo) > LongAgo.AddYears(1));

        // Tick "keep date modified" in the settings flyout.
        var settingsButton = Find<Button>(window, "SettingsButton");
        var flyout = (Flyout)settingsButton.Flyout!;
        Click(window, settingsButton);
        Assert.True(flyout.IsOpen);
        var checkBox = ((Control)flyout.Content!).GetVisualDescendants().OfType<CheckBox>()
            .Single(c => c.Name == "PreserveModifiedTimeBox");
        Click(window, checkBox);
        Assert.True(vm.PreserveModifiedTime);
        Assert.True(AppSettings.Load(SettingsPath).PreserveModifiedTime);
        flyout.Hide(); // as a click elsewhere would; that first click only closes the flyout

        // Now the date is kept.
        File.SetLastWriteTimeUtc(photo, LongAgo);
        Click(window, Star(window, 5));
        await details.SaveCompletion;
        Assert.Equal(5, PhotoMetadata.Read(photo).Rating);
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(photo));
        window.Close();
    }

    [AvaloniaFact]
    public async Task SavedSetting_IsAppliedAtStartup()
    {
        await using var exifTool = RequireExifTool();
        var settings = AppSettings.Load(SettingsPath);
        settings.PreserveModifiedTime = true;
        settings.Save();

        Photo("photo.jpg");
        var writer = new PhotoMetadataWriter(exifTool);
        var (window, vm) = await OpenAsync(writer);

        Assert.True(vm.PreserveModifiedTime);
        Assert.True(writer.PreserveModifiedTime);
        window.Close();
    }

    private static Button Star(Window window, int stars) =>
        FindAll<Button>(window).Where(b => b.Classes.Contains("star")).ElementAt(stars - 1);
}
