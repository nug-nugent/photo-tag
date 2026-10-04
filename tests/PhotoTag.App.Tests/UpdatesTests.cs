using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

public sealed class UpdatesTests : UiTestBase
{
    private sealed class FakeUpdater(string? available, bool fails = false) : IAppUpdater
    {
        public int Checks { get; private set; }
        public string? Available { get; set; } = available;
        public bool Fails { get; set; } = fails;
        /// <summary>When set, checks wait for it to complete.</summary>
        public TaskCompletionSource? Gate { get; set; }
        public bool Restarted { get; private set; }
        public bool IsInstalled => true;
        public string? CurrentVersion => "0.1.0";

        public async Task<string?> DownloadNewVersionAsync(CancellationToken cancellationToken)
        {
            Checks++;
            if (Gate is { } gate) await gate.Task;
            return Fails ? throw new HttpRequestException("offline") : Available;
        }

        public void RestartToUpdate() => Restarted = true;
    }

    [AvaloniaFact]
    public async Task ANewVersion_IsOfferedInTheStatusBar_AndRestartInstallsIt()
    {
        Photo("a.jpg");
        var updater = new FakeUpdater("0.2.0");
        var (window, vm) = await OpenAsync(writer: null, updater: updater);

        Assert.False(vm.Updates.IsUpdateReady);
        await vm.Updates.CheckOnStartupAsync();

        Assert.Equal("PhotoTag 0.2.0 is ready to install.", vm.Updates.ReadyText);
        var restart = Find<Button>(window, "RestartToUpdateButton");
        Assert.True(restart.IsEffectivelyVisible);
        Click(window, restart);
        Assert.True(updater.Restarted);
        Assert.Equal("PhotoTag 0.1.0", vm.Updates.VersionText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NothingIsShown_WhenUpToDate_Offline_OrTurnedOff()
    {
        Photo("a.jpg");

        var upToDate = new FakeUpdater(available: null);
        var (window, vm) = await OpenAsync(writer: null, updater: upToDate);
        await vm.Updates.CheckOnStartupAsync();
        Assert.False(vm.Updates.IsUpdateReady);
        Assert.False(Find<Button>(window, "RestartToUpdateButton").IsEffectivelyVisible);
        window.Close();

        var offline = new FakeUpdater("0.2.0", fails: true);
        (window, vm) = await OpenAsync(writer: null, updater: offline);
        await vm.Updates.CheckOnStartupAsync(); // doesn't throw
        Assert.False(vm.Updates.IsUpdateReady);
        window.Close();

        var turnedOff = new FakeUpdater("0.2.0");
        (window, vm) = await OpenAsync(writer: null, updater: turnedOff);
        vm.Updates.CheckAutomatically = false;
        Assert.False(AppSettings.Load(SettingsPath).CheckForUpdates);
        await vm.Updates.CheckOnStartupAsync();
        Assert.Equal(0, turnedOff.Checks);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CheckNow_InSettings_SaysWhatItFound()
    {
        Photo("a.jpg");
        var updater = new FakeUpdater(available: null, fails: true);
        var (window, vm) = await OpenAsync(writer: null, updater: updater);
        vm.Updates.CheckAutomatically = false;

        var settings = OpenSettings(window);
        var checkNow = Find<Button>(settings, "CheckNowButton");
        var status = Find<TextBlock>(settings, "UpdateStatusText");
        Assert.True(checkNow.IsEffectivelyEnabled);
        Assert.Null(status.Text);

        Click(settings, checkNow);
        await WaitForAsync(() => checkNow.IsEffectivelyEnabled);
        Assert.Equal("Couldn't check for a new version. Are you online?", status.Text);

        updater.Fails = false;
        Click(settings, checkNow);
        await WaitForAsync(() => checkNow.IsEffectivelyEnabled);
        Assert.Equal("PhotoTag is up to date.", status.Text);
        Assert.False(vm.Updates.IsUpdateReady);

        // While it checks, it says so and can't be clicked again.
        updater.Available = "0.2.0";
        updater.Gate = new TaskCompletionSource();
        Click(settings, checkNow);
        Assert.Equal("Checking for a new version…", status.Text);
        Assert.False(checkNow.IsEffectivelyEnabled);
        updater.Gate.SetResult();
        await WaitForAsync(() => checkNow.IsEffectivelyEnabled);
        Assert.Equal("PhotoTag 0.2.0 is ready to install. Restart PhotoTag to install it.", status.Text);
        Assert.True(Find<Button>(window, "RestartToUpdateButton").IsEffectivelyVisible);
        Assert.Equal(3, updater.Checks);

        // Once one is downloaded, checking again doesn't download it again.
        Click(settings, checkNow);
        await WaitForAsync(() => checkNow.IsEffectivelyEnabled);
        Assert.Equal(3, updater.Checks);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CheckNow_DuringTheStartupCheck_SharesIt()
    {
        Photo("a.jpg");
        var updater = new FakeUpdater("0.2.0") { Gate = new TaskCompletionSource() };
        var (window, vm) = await OpenAsync(writer: null, updater: updater);

        var startup = vm.Updates.CheckOnStartupAsync();
        var settings = OpenSettings(window);
        var checkNow = Find<Button>(settings, "CheckNowButton");
        Click(settings, checkNow);
        updater.Gate.SetResult();
        await startup;
        await WaitForAsync(() => checkNow.IsEffectivelyEnabled);

        Assert.Equal(1, updater.Checks);
        Assert.Equal("PhotoTag 0.2.0 is ready to install. Restart PhotoTag to install it.",
            Find<TextBlock>(settings, "UpdateStatusText").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DevelopmentBuilds_HaveNothingToUpdate()
    {
        Photo("a.jpg");
        var (window, vm) = await OpenAsync(writer: null);

        await vm.Updates.CheckOnStartupAsync();

        Assert.False(vm.Updates.CanUpdate);
        Assert.EndsWith("(development build)", vm.Updates.VersionText);
        Assert.False(Find<Button>(OpenSettings(window), "CheckNowButton").IsEffectivelyEnabled);
        window.Close();
    }

    [Fact]
    public async Task SelfCheck_ReportsEachPart()
    {
        var report = Path.Combine(Path.GetTempPath(), $"phototag-selfcheck-{Guid.NewGuid():N}.json");
        try
        {
            var exitCode = await SelfCheck.RunAsync(report);
            var json = await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken);

            Assert.Contains("\"sqlite\"", json);
            Assert.Contains("decoded 32x24", json);
            // In a test run ExifTool comes from PATH (if installed), so only check it was reported.
            Assert.Contains("\"exiftool\"", json);
            // A Debug build fails, as it uses PhotoTag-Dev for its files: only Release builds are released.
            Assert.Contains("\"data folder\"", json);
            var shouldPass = ExifToolSetup.Find().Status == ExifToolStatus.Ready && !AppData.IsDevelopmentBuild;
            Assert.Equal(shouldPass ? 0 : 1, exitCode);
        }
        finally
        {
            File.Delete(report);
        }
    }
}
