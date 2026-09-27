using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// What changed outside PhotoTag: photos changed on disk since they were indexed, photos new to the index,
/// photos gone, and folders whose contents changed (for the folder tree and the grid). <see cref="Everything"/>:
/// anything may have changed.
/// </summary>
public sealed record LibraryChanges(IReadOnlySet<string> Changed, IReadOnlySet<string> Added, IReadOnlySet<string> Removed,
    IReadOnlySet<string> Folders, bool Everything);

/// <summary>
/// Keeps the <see cref="LibraryIndex"/> in step with the files: scans the open folder tree in
/// the background, records PhotoTag's own edits straight away, watches for changes made outside
/// PhotoTag, and feeds library-wide tag suggestions. Raises <see cref="CountsChanged"/> so the
/// folder tree can refresh its counts, and <see cref="FilesChanged"/> after outside changes.
/// </summary>
public partial class LibraryViewModel(LibraryIndex index, KeywordSuggestions suggestions, KeywordSuggestions people,
    PlaceSuggestions places) : ViewModelBase, IDisposable
{
    /// <summary>How often to look at the shown folder where change notifications can't be trusted (network shares).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private CancellationTokenSource? _scan;
    private string? _root;
    private FolderWatcher? _watcher;
    private DispatcherTimer? _pollTimer;
    private readonly HashSet<string> _pendingPaths = new(PathComparer);
    private readonly HashSet<string> _pendingFolders = new(PathComparer);
    private bool _pendingEverything;
    private bool _refreshing;
    private int _recording;
    private TaskCompletionSource<bool>? _moveAnswer;

    public LibraryIndex Index { get; } = index;

    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial string? StatusText { get; private set; }

    /// <summary>Completes when the current scan (if any) has finished. For tests and shutdown.</summary>
    public Task ScanCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Where the open folder's photos seem to have been indexed before, while PhotoTag asks whether they moved.
    /// The scan waits for the answer: if they did, their index entries move too, instead of every photo being read again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MovedText))]
    public partial PreviousLocation? MovedFrom { get; private set; }

    public string? MovedText => MovedFrom is { } moved
        ? $"These look like the {moved.PhotoCount:N0} photos PhotoTag knew at {moved.Folder}. If they've moved here, PhotoTag " +
          "can bring their index with them rather than read every photo again."
        : null;

    /// <summary>"Yes, they moved here".</summary>
    [RelayCommand]
    private void AcceptMove() => _moveAnswer?.TrySetResult(true);

    /// <summary>"No, index them afresh".</summary>
    [RelayCommand]
    private void DeclineMove() => _moveAnswer?.TrySetResult(false);

    public event EventHandler? CountsChanged;

    /// <summary>Raised on the UI thread after files changed outside PhotoTag and the index has caught up.</summary>
    public event EventHandler<LibraryChanges>? FilesChanged;

    /// <summary>Completes when the outside changes noticed so far have been handled. For tests.</summary>
    public Task RefreshCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>True while PhotoTag is writing tags: outside changes wait, so PhotoTag's own writes aren't taken for them.</summary>
    public Func<bool> IsWriting { get; set; } = () => false;

    /// <summary>The folders to look at when polling: the one on screen.</summary>
    public Func<IEnumerable<string>> FoldersToPoll { get; set; } = () => [];

    /// <summary>Whether the open folder is polled as well as watched (network shares, or no notifications).</summary>
    public bool IsPolling => _pollTimer is not null;

    /// <summary>Starts (or restarts) indexing everything under <paramref name="root"/>, and watching it for changes.</summary>
    public void StartScan(string root)
    {
        _scan?.Cancel();
        _scan?.Dispose();
        var cts = _scan = new CancellationTokenSource();
        ScanCompletion = ScanAsync(root, cts);
        Watch(root);
    }

    private async Task ScanAsync(string root, CancellationTokenSource cts)
    {
        IsScanning = true;
        StatusText = "Indexing…";
        var progress = new Progress<IndexProgress>(p =>
        {
            if (!cts.IsCancellationRequested && p.Total > 0) StatusText = $"Indexing {100 * p.Done / p.Total}%";
        });

        try
        {
            await LoadSuggestionsAsync(); // what's already indexed, before the scan finishes
            await OfferMoveAsync(root, cts.Token);
            await Index.ScanAsync(root, progress, cts.Token);
            await LoadSuggestionsAsync();
            CountsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_scan == cts)
            {
                IsScanning = false;
                StatusText = null;
                MovedFrom = null;
            }
        }
    }

    /// <summary>If the folder's photos look like ones indexed elsewhere, asks whether they moved, and moves their index if so.</summary>
    private async Task OfferMoveAsync(string root, CancellationToken cancellationToken)
    {
        PreviousLocation? moved;
        try
        {
            moved = await Index.FindPreviousLocationAsync(root, cancellationToken: cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (moved is null) return;

        var answer = _moveAnswer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));
        (MovedFrom, StatusText) = (moved, null);
        try
        {
            if (!await answer.Task) return;
        }
        finally
        {
            MovedFrom = null;
            _moveAnswer = null;
        }

        StatusText = "Moving the index…";
        await Index.MoveFolderAsync(moved.Folder, root);
        await LoadSuggestionsAsync();
        CountsChanged?.Invoke(this, EventArgs.Empty);
        StatusText = "Indexing…";
    }

    // --- Changes made outside PhotoTag -------------------------------------------------------

    private void Watch(string root)
    {
        StopWatching();
        _root = root;
        var watcher = _watcher = new FolderWatcher(root);
        watcher.Changed += (_, changes) => Dispatcher.UIThread.Post(() =>
        {
            if (_watcher == watcher) Queue(changes.Paths, [], changes.Incomplete);
        });

        // Notifications from network shares can go missing, and some file systems have none.
        if (!watcher.IsWatching || PhotoFiles.IsOnNetworkDrive(root))
        {
            _pollTimer = new DispatcherTimer { Interval = PollInterval };
            _pollTimer.Tick += (_, _) => RefreshFolders(FoldersToPoll());
            _pollTimer.Start();
        }
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _pollTimer?.Stop();
        _pollTimer = null;
        _root = null;
        _pendingPaths.Clear();
        _pendingFolders.Clear();
        _pendingEverything = false;
    }

    /// <summary>Looks for changes in these folders (not their subfolders), e.g. the one on screen when the window is activated.</summary>
    public void RefreshFolders(IEnumerable<string> folders) => Queue([], folders, everything: false);

    private void Queue(IEnumerable<string> paths, IEnumerable<string> folders, bool everything)
    {
        if (_root is null) return;
        _pendingPaths.UnionWith(paths);
        _pendingFolders.UnionWith(folders);
        _pendingEverything |= everything;
        if (!_refreshing && (_pendingPaths.Count > 0 || _pendingFolders.Count > 0 || _pendingEverything))
            RefreshCompletion = ProcessChangesAsync();
    }

    private async Task ProcessChangesAsync()
    {
        _refreshing = true;
        try
        {
            while (_root is { } root && (_pendingPaths.Count > 0 || _pendingFolders.Count > 0 || _pendingEverything))
            {
                // Let PhotoTag's own writes finish and be recorded first, so they aren't taken for outside changes.
                while (IsWriting() || _recording > 0) await Task.Delay(250);
                if (_root != root) continue;

                string[] paths = [.. _pendingPaths], folders = [.. _pendingFolders];
                var everything = _pendingEverything;
                _pendingPaths.Clear();
                _pendingFolders.Clear();
                _pendingEverything = false;

                LibraryChanges changes;
                try
                {
                    changes = await RefreshIndexAsync(root, paths, folders, everything);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue; // a folder went away mid-refresh; the watcher reports that too
                }
                if (_root != root) continue;

                FilesChanged?.Invoke(this, changes);
                if (changes.Changed.Count > 0 || changes.Added.Count > 0 || changes.Removed.Count > 0)
                {
                    await LoadSuggestionsAsync();
                    CountsChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Works out which folders the changed paths affect, and brings the index up to date with just those.</summary>
    private async Task<LibraryChanges> RefreshIndexAsync(string root, IReadOnlyCollection<string> paths,
        IReadOnlyCollection<string> folders, bool everything)
    {
        if (everything)
        {
            var all = await Index.RefreshAsync(root, includeSubfolders: true);
            return new LibraryChanges(all.Changed.ToHashSet(PathComparer), all.Added.ToHashSet(PathComparer),
                all.Removed.ToHashSet(PathComparer), new HashSet<string>(PathComparer), Everything: true);
        }

        var (shallow, deep) = await Task.Run(() => AffectedFolders(root, paths, folders));
        var changed = new HashSet<string>(PathComparer);
        var added = new HashSet<string>(PathComparer);
        var removed = new HashSet<string>(PathComparer);
        foreach (var (folder, includeSubfolders) in deep.Select(f => (f, true)).Concat(shallow.Select(f => (f, false))))
        {
            var result = await Index.RefreshAsync(folder, includeSubfolders);
            changed.UnionWith(result.Changed);
            added.UnionWith(result.Added);
            removed.UnionWith(result.Removed);
        }
        return new LibraryChanges(changed, added, removed, shallow.Concat(deep).ToHashSet(PathComparer), Everything: false);
    }

    /// <summary>
    /// Folders to look at again: each changed path's folder (just that folder), and each folder created,
    /// deleted or renamed (with everything under it). Files that can't be photos or sidecars are ignored,
    /// and hidden folders skipped, as scans skip them.
    /// </summary>
    private static (HashSet<string> Shallow, HashSet<string> Deep) AffectedFolders(string root, IEnumerable<string> paths,
        IEnumerable<string> folders)
    {
        var shallow = new HashSet<string>(folders.Where(f => IsInside(root, f) && Directory.Exists(f)), PathComparer);
        var deep = new HashSet<string>(PathComparer);
        foreach (var path in paths)
        {
            if (!IsInside(root, path) || IsSame(root, path)) continue;
            if (Directory.Exists(path))
            {
                if (IsHidden(root, path)) continue;
                deep.Add(path);
            }
            else if (File.Exists(path))
            {
                if (!PhotoFiles.IsSupported(path) && !Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase)) continue;
            }
            else
            {
                deep.Add(path); // gone: a photo or a whole folder (the index knows which)
            }
            if (Path.GetDirectoryName(path) is { } parent && !IsHidden(root, parent)) shallow.Add(parent);
        }
        shallow.RemoveWhere(f => deep.Any(d => IsInside(d, f)));
        return (shallow, deep);
    }

    private static bool IsSame(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);

    private static bool IsInside(string folder, string path)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        path = Path.TrimEndingDirectorySeparator(path);
        return path.Equals(folder, PathComparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Whether a folder, or one above it up to <paramref name="root"/>, is hidden.</summary>
    private static bool IsHidden(string root, string folder)
    {
        for (var dir = folder; dir is not null && IsInside(root, dir) && !IsSame(root, dir); dir = Path.GetDirectoryName(dir))
        {
            try
            {
                if ((File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    // --- PhotoTag's own edits ----------------------------------------------------------------

    /// <summary>Records metadata that a bulk edit wrote.</summary>
    public async Task PhotosChangedAsync(IReadOnlyDictionary<string, PhotoMetadata> after)
    {
        _recording++;
        try
        {
            await Index.UpdateAsync(after.Select(p => (p.Key, p.Value)));
        }
        finally
        {
            _recording--;
        }
        CountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads one photo after a single edit and records it.</summary>
    public async Task PhotoChangedAsync(string path)
    {
        _recording++;
        try
        {
            var metadata = await Task.Run(() => PhotoMetadata.Read(path));
            await Index.UpdateAsync([(path, metadata)]);
            CountsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is IOException or MetadataExtractor.ImageProcessingException)
        {
            // The next scan will pick it up.
        }
        finally
        {
            _recording--;
        }
    }

    private async Task LoadSuggestionsAsync()
    {
        var keywords = await Index.GetKeywordsAsync();
        suggestions.Add(keywords.Select(k => k.Keyword));
        people.Add((await Index.GetValuesAsync(ListField.People)).Select(p => p.Keyword));
        await places.LoadAsync(Index);
    }

    /// <summary>Rebuilds the suggestions from the index, dropping tags and people no photo has any more.</summary>
    public async Task ReloadSuggestionsAsync()
    {
        var keywords = await Index.GetKeywordsAsync();
        suggestions.Reset(keywords.Select(k => k.Keyword));
        people.Reset((await Index.GetValuesAsync(ListField.People)).Select(p => p.Keyword));
    }

    public void Cancel()
    {
        _scan?.Cancel();
    }

    public void Dispose()
    {
        _scan?.Cancel();
        StopWatching();
    }
}
