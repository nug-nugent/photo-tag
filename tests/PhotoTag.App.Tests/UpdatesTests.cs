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
        public bool Restarted { get; private set; }
        public bool IsInstalled => true;
        public string? CurrentVersion => "0.1.0";

        public Task<string?> DownloadNewVersionAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return fails ? Task.FromException<string?>(new HttpRequestException("offline")) : Task.FromResult(available);
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
    public async Task DevelopmentBuilds_HaveNothingToUpdate()
    {
        Photo("a.jpg");
        var (window, vm) = await OpenAsync(writer: null);

        await vm.Updates.CheckOnStartupAsync();

        Assert.False(vm.Updates.CanUpdate);
        Assert.EndsWith("(development build)", vm.Updates.VersionText);
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
            Assert.Equal(ExifTool.Locate() is null ? 1 : 0, exitCode);
        }
        finally
        {
            File.Delete(report);
        }
    }
}
