namespace PhotoTag.Core;

/// <summary>
/// What changed under a watched folder: the paths of files and folders created, changed, deleted or
/// renamed (both names). <see cref="Incomplete"/> means notifications were lost, so anything may have changed.
/// </summary>
public sealed record FolderChanges(IReadOnlyCollection<string> Paths, bool Incomplete);

/// <summary>
/// Watches a folder tree for changes made outside PhotoTag and reports them in batches: a burst of
/// changes (copying in a card's worth of photos, another app retagging a folder) arrives as one
/// <see cref="Changed"/> once things go quiet for <c>quietPeriod</c>, or at the latest after
/// <c>maxDelay</c>. Raised on a thread-pool thread.
///
/// File system notifications can be unreliable on network shares, and some file systems have none;
/// <see cref="IsWatching"/> is false then, and callers should poll instead.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxDelay;
    private readonly Lock _lock = new();
    private HashSet<string> _pending = [];
    private bool _incomplete;
    private DateTime _batchStarted;
    private bool _disposed;

    public FolderWatcher(string root, TimeSpan? quietPeriod = null, TimeSpan? maxDelay = null)
    {
        Root = root;
        _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Flush());

        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024, // the most network shares allow
            };
            _watcher.Created += (_, e) => Add(e.FullPath);
            _watcher.Changed += (_, e) => Add(e.FullPath);
            _watcher.Deleted += (_, e) => Add(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Add(e.OldFullPath);
                Add(e.FullPath);
            };
            _watcher.Error += (_, e) =>
            {
                Log.Warn($"Lost track of changes under {root}", e.GetException());
                AddIncomplete();
            };
            _watcher.EnableRaisingEvents = true;
            IsWatching = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // No notifications here (or too many watchers already on Linux): the caller polls.
            Log.Info($"No change notifications for {root} ({e.Message}); polling instead");
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public string Root { get; }

    /// <summary>False when the file system can't report changes; callers should poll.</summary>
    public bool IsWatching { get; }

    public event EventHandler<FolderChanges>? Changed;

    private void Add(string path)
    {
        lock (_lock)
        {
            StartBatch();
            _pending.Add(path);
            Schedule();
        }
    }

    private void AddIncomplete()
    {
        lock (_lock)
        {
            StartBatch();
            _incomplete = true;
            Schedule();
        }
    }

    private void StartBatch()
    {
        if (_pending.Count == 0 && !_incomplete) _batchStarted = DateTime.UtcNow;
    }

    // Wait for a quiet spell, but not forever while changes keep coming.
    private void Schedule()
    {
        if (_disposed) return;
        var untilMax = _batchStarted + _maxDelay - DateTime.UtcNow;
        var due = TimeSpan.FromTicks(Math.Clamp(untilMax.Ticks, 0, _quietPeriod.Ticks));
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        FolderChanges changes;
        lock (_lock)
        {
            if (_disposed || (_pending.Count == 0 && !_incomplete)) return;
            changes = new FolderChanges(_pending, _incomplete);
            _pending = [];
            _incomplete = false;
        }
        if (changes.Incomplete) Restart();
        Changed?.Invoke(this, changes);
    }

    /// <summary>After an error (a share that dropped off the network, say), notifications may have stopped.</summary>
    private void Restart()
    {
        try
        {
            if (_watcher is null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Still gone; the caller's next rescan will say so.
        }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
