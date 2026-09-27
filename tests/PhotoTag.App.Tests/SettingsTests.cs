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
        var photo = Photo("photo.jpg", "Tagged");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectSingleAsync(window, vm, 0);

        // Default: favouriting moves the date on, so backup tools notice.
        File.SetLastWriteTimeUtc(photo, LongAgo);
        Click(window, Find<Button>(window, "FavouriteButton"));
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
        Click(window, Find<Button>(window, "FavouriteButton"));
        await details.SaveCompletion;
        Assert.False(PhotoMetadata.Read(photo).IsFavourite);
        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(photo));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ShowLog_LinksToTheLogFile()
    {
        Photo("photo.jpg");
        var logPath = Path.Combine(Path.GetTempPath(), "PhotoTag", "log.txt");
        var (window, _) = await OpenAsync(writer: null, logPath: logPath);

        var settingsButton = Find<Button>(window, "SettingsButton");
        Click(window, settingsButton);
        var link = ((Control)((Flyout)settingsButton.Flyout!).Content!).GetVisualDescendants().OfType<HyperlinkButton>()
            .Single(l => l.Name == "ShowLogLink");
        Assert.True(link.IsEffectivelyVisible);
        Assert.Equal(new Uri(logPath), link.NavigateUri);
        Assert.Equal("file", link.NavigateUri!.Scheme);
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
}
