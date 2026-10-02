using System.Diagnostics;
using Avalonia.Threading;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// Runs an action at most once per <paramref name="interval"/>, on the UI thread. A call that comes too soon isn't
/// dropped: the action runs once more when the interval is up, so what's on screen always catches up.
/// </summary>
internal sealed class Throttle(TimeSpan interval, Action action) : IDisposable
{
    private readonly Stopwatch _sinceRun = new();
    private DispatcherTimer? _pending;

    public void Run()
    {
        if (_pending is not null) return; // it'll run soon anyway
        var wait = _sinceRun.IsRunning ? interval - _sinceRun.Elapsed : TimeSpan.Zero;
        if (wait <= TimeSpan.Zero)
        {
            RunNow();
            return;
        }
        _pending = new DispatcherTimer(wait, DispatcherPriority.Background, (_, _) =>
        {
            Dispose();
            RunNow();
        });
        _pending.Start();
    }

    private void RunNow()
    {
        _sinceRun.Restart();
        action();
    }

    /// <summary>Forgets a run that's waiting.</summary>
    public void Dispose()
    {
        _pending?.Stop();
        _pending = null;
    }
}
