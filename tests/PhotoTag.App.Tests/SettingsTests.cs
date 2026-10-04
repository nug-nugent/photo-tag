using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoTag.App.ViewModels;
using PhotoTag.App.Views;
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

        // Tick "keep date modified" in the settings window.
        var settings = OpenSettings(window);
        Click(settings, Find<CheckBox>(settings, "PreserveModifiedTimeBox"));
        Assert.True(vm.Settings.PreserveModifiedTime);
        Assert.True(AppSettings.Load(SettingsPath).PreserveModifiedTime);
        Click(settings, Find<Button>(settings, "CloseButton"));
        Assert.Null(window.OpenSettings);

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

        var settings = OpenSettings(window);
        var link = Find<HyperlinkButton>(settings, "ShowLogLink");
        Assert.True(link.IsEffectivelyVisible);
        Assert.Equal(new Uri(logPath), link.NavigateUri);
        Assert.Equal("file", link.NavigateUri!.Scheme);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ThumbnailLimit_DefaultsTo2GB_AndLoweringItCleansUpStraightAway()
    {
        Photo("photo.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        await WaitForAsync(() => Tiles(window)[0].DataContext is PhotoItemViewModel { Thumbnail: not null });

        // An old thumbnail of 600 MB (sparse, so it takes no real space), and the one just made.
        var cache = ThumbnailsPath(0);
        var big = Path.Combine(cache, "00", "big.thumb");
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        using (var stream = File.Create(big)) stream.SetLength(600L * 1024 * 1024);
        File.SetLastWriteTimeUtc(big, DateTime.UtcNow.AddDays(-30));

        var settings = OpenSettings(window);
        var box = Find<ComboBox>(settings, "ThumbnailLimitBox");
        Assert.Equal("2 GB", box.SelectedItem);
        await WaitForAsync(() => vm.Settings.ThumbnailSizeText is not null);
        Assert.Equal("Thumbnails take 600 MB at the moment.", vm.Settings.ThumbnailSizeText);
        Assert.True(Find<TextBlock>(settings, "ThumbnailSizeText").IsEffectivelyVisible);

        box.SelectedIndex = 0;
        Assert.Equal(500L * 1024 * 1024, AppSettings.Load(SettingsPath).ThumbnailCacheLimit);
        await WaitForAsync(() => !File.Exists(big));
        await WaitForAsync(() => vm.Settings.ThumbnailSizeText == "Thumbnails take less than 1 MB at the moment.");
        Assert.Single(Directory.GetFiles(cache, "*.thumb", SearchOption.AllDirectories)); // the one in use stays

        // Esc closes the window (on key down, so there's nothing to release the key on).
        settings.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(window.OpenSettings);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ThumbnailLimit_ShowsTheNearestChoice_ForAnyOtherValue()
    {
        var saved = AppSettings.Load(SettingsPath);
        saved.ThumbnailCacheLimit = 3L * 1024 * 1024 * 1024 - 1;
        saved.Save();
        Photo("photo.jpg");
        var (window, _) = await OpenAsync(writer: null);

        Assert.Equal("2 GB", Find<ComboBox>(OpenSettings(window), "ThumbnailLimitBox").SelectedItem);
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

        Assert.True(vm.Settings.PreserveModifiedTime);
        Assert.True(writer.PreserveModifiedTime);
        window.Close();
    }
}
