using Velopack;
using Velopack.Sources;

namespace PhotoTag.App;

/// <summary>Checks for, downloads and installs new versions. Behind an interface so tests can fake it.</summary>
public interface IAppUpdater
{
    /// <summary>False when running from a development build, where there's nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>e.g. "0.3.0", or null for a development build.</summary>
    string? CurrentVersion { get; }

    /// <summary>Downloads the newest release if there is one, returning its version; null if up to date.</summary>
    Task<string?> DownloadNewVersionAsync(CancellationToken cancellationToken);

    /// <summary>Installs the downloaded version and restarts PhotoTag.</summary>
    void RestartToUpdate();
}

/// <summary>For development builds and tests: never anything to update.</summary>
public sealed class NoUpdates : IAppUpdater
{
    public bool IsInstalled => false;
    public string? CurrentVersion => null;
    public Task<string?> DownloadNewVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public void RestartToUpdate() { }
}

/// <summary>Updates from this repo's GitHub Releases, via Velopack.</summary>
public sealed class GitHubReleasesUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/nug-nugent/photo-tag";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private UpdateInfo? _downloaded;

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.IsInstalled ? _manager.CurrentVersion?.ToString() : null;

    public async Task<string?> DownloadNewVersionAsync(CancellationToken cancellationToken)
    {
        if (!_manager.IsInstalled) return null;

        var update = await _manager.CheckForUpdatesAsync();
        if (update is null) return null;

        await _manager.DownloadUpdatesAsync(update, progress: null, cancellationToken);
        _downloaded = update;
        return update.TargetFullRelease.Version.ToString();
    }

    public void RestartToUpdate()
    {
        if (_downloaded is { } update) _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
