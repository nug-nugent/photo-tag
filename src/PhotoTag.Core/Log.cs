using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace PhotoTag.Core;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// PhotoTag's log, for diagnosing bug reports: what happened (folders opened, scans, update checks) and what
/// went wrong (ExifTool errors, unreadable photos, unreachable shares). Does nothing until <see cref="Start"/>,
/// so tests and tools don't write one.
/// </summary>
public static class Log
{
    private static LogFile? _file;

    /// <summary>Where the log is being written, or null if it isn't.</summary>
    public static LogFile? File => _file;

    public static void Start(LogFile file) => _file = file;

    public static void Stop() => _file = null;

    public static void Info(string message) => _file?.Write(LogLevel.Info, message);

    /// <summary>Something failed in a way PhotoTag expects and copes with (a locked file, a share that's asleep).</summary>
    public static void Warn(string message, Exception? exception = null) => _file?.Write(LogLevel.Warn, message, exception);

    /// <summary>Something that shouldn't happen: logged with its stack trace.</summary>
    public static void Error(string message, Exception? exception = null) => _file?.Write(LogLevel.Error, message, exception);
}

/// <summary>
/// A text log that rolls over: when it passes <c>maxSize</c> it becomes <see cref="OldPath"/> (replacing the
/// one before) and a new one starts, so it never takes more than about twice that. Lines are written on a
/// background thread: logging never waits for the disk, and never throws.
/// </summary>
public sealed class LogFile : IAsyncDisposable
{
    public const long DefaultMaxSize = 1024 * 1024;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTag", "log.txt");

    // Lines to write, and flush requests (completed once everything queued before them is written).
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly long _maxSize;
    private readonly Task _writing;

    public LogFile(string path, long maxSize = DefaultMaxSize)
    {
        FilePath = path;
        OldPath = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + ".old" + Path.GetExtension(path));
        _maxSize = maxSize;
        _writing = Task.Run(WriteQueuedAsync);
    }

    public string FilePath { get; }

    /// <summary>The previous log, e.g. log.old.txt.</summary>
    public string OldPath { get; }

    /// <summary>"2026-09-27 14:03:12.345 WARN  message: IOException: details". Later lines of a message are indented.</summary>
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (exception is not null)
            message += level == LogLevel.Error ? Environment.NewLine + exception : $": {exception.GetType().Name}: {exception.Message}";
        var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var label = level.ToString().ToUpperInvariant().PadRight(5);
        var lines = message.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var text = new StringBuilder();
        text.Append(time).Append(' ').Append(label).Append(' ').Append(lines[0]).Append(Environment.NewLine);
        foreach (var line in lines.Skip(1)) text.Append("    ").Append(line).Append(Environment.NewLine);
        _queue.Writer.TryWrite(text.ToString());
    }

    /// <summary>Completes when everything logged so far is on disk.</summary>
    public Task FlushAsync()
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _queue.Writer.TryWrite(flushed) ? flushed.Task : _writing;
    }

    /// <summary>Writes what's queued, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writing.ConfigureAwait(false);
    }

    private async Task WriteQueuedAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var text = new StringBuilder();
            var flushed = new List<TaskCompletionSource>();
            while (reader.TryRead(out var item))
            {
                if (item is TaskCompletionSource flush) flushed.Add(flush);
                else text.Append((string)item);
            }
            if (text.Length > 0) Append(text.ToString());
            foreach (var flush in flushed) flush.TrySetResult();
        }
    }

    private void Append(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var existing = new FileInfo(FilePath);
            if (existing.Exists && existing.Length > 0 && existing.Length + Encoding.UTF8.GetByteCount(text) > _maxSize)
                File.Move(FilePath, OldPath, overwrite: true);
            File.AppendAllText(FilePath, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere to say so. (A second copy of PhotoTag writing at the same moment, say.)
        }
    }
}
