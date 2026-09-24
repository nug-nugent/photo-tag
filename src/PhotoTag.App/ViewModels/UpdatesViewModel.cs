using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// Quietly checks for a new release at startup (if enabled), downloads it in the background, and
/// offers "Restart to update" in the status bar. Offline or failed checks are silently ignored;
/// the next launch tries again.
/// </summary>
public partial class UpdatesViewModel(IAppUpdater updater, AppSettings settings) : ViewModelBase
{
    /// <summary>"PhotoTag 0.3.0", or "PhotoTag (development build)".</summary>
    public string VersionText => updater.CurrentVersion is { } version
        ? $"PhotoTag {version}"
        : $"PhotoTag {DevelopmentVersion} (development build)";

    public bool CanUpdate => updater.IsInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateReady))]
    public partial string? ReadyVersion { get; private set; }

    public bool IsUpdateReady => ReadyVersion is not null;
    public string? ReadyText => ReadyVersion is { } v ? $"PhotoTag {v} is ready to install." : null;

    public bool CheckAutomatically
    {
        get => settings.CheckForUpdates;
        set
        {
            if (value == settings.CheckForUpdates) return;
            settings.CheckForUpdates = value;
            settings.Save();
            OnPropertyChanged();
            if (value) _ = CheckAsync();
        }
    }

    /// <summary>Called at startup; does nothing if automatic checks are off or this is a development build.</summary>
    public Task CheckOnStartupAsync() => settings.CheckForUpdates ? CheckAsync() : Task.CompletedTask;

    private async Task CheckAsync()
    {
        if (!updater.IsInstalled || IsUpdateReady) return;
        try
        {
            if (await updater.DownloadNewVersionAsync(CancellationToken.None) is { } version)
            {
                ReadyVersion = version;
                OnPropertyChanged(nameof(ReadyText));
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            // Offline, rate-limited or similar: try again next launch.
        }
    }

    [RelayCommand]
    private void RestartToUpdate() => updater.RestartToUpdate();

    private static string DevelopmentVersion =>
        typeof(UpdatesViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";
}
