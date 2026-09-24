using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// Runs one bulk edit at a time and reports its progress in the status bar. It belongs to the
/// window rather than the details panel, so changing the selection doesn't cancel a running
/// edit; only the Cancel button does.
/// </summary>
public partial class BulkOperations(PhotoMetadataWriter? writer, KeywordSuggestions suggestions) : ViewModelBase
{
    private readonly BulkMetadataEditor? _editor = writer is null ? null : new BulkMetadataEditor(writer);
    private CancellationTokenSource? _cts;

    public bool IsAvailable => _editor is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string? ProgressText { get; private set; }
    [ObservableProperty] public partial double ProgressPercent { get; private set; }

    /// <summary>Raised on the UI thread after an operation finishes, so panels can refresh.</summary>
    public event EventHandler<BulkResult>? Completed;

    /// <summary>Raised with a one-line summary for the status bar.</summary>
    public event EventHandler<string>? Summary;

    public Task AddKeywordsAsync(IReadOnlyList<PhotoItemViewModel> photos, IReadOnlyList<string> keywords)
    {
        suggestions.Add(keywords);
        var label = keywords.Count == 1 ? $"“{keywords[0]}”" : $"{keywords.Count} tags";
        return RunAsync(photos, $"Adding {label} to", (e, paths, p, ct) => e.AddKeywordsAsync(paths, keywords, p, ct));
    }

    public Task RemoveKeywordsAsync(IReadOnlyList<PhotoItemViewModel> photos, IReadOnlyList<string> keywords)
    {
        var label = keywords.Count == 1 ? $"“{keywords[0]}”" : $"{keywords.Count} tags";
        return RunAsync(photos, $"Removing {label} from", (e, paths, p, ct) => e.RemoveKeywordsAsync(paths, keywords, p, ct));
    }

    public Task SetFavouriteAsync(IReadOnlyList<PhotoItemViewModel> photos, bool favourite) =>
        RunAsync(photos, favourite ? "Adding to favourites:" : "Removing from favourites:",
            (e, paths, p, ct) => e.SetFavouriteAsync(paths, favourite, p, ct));

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        ProgressText = "Cancelling…";
    }

    private async Task RunAsync(IReadOnlyList<PhotoItemViewModel> photos, string verb,
        Func<BulkMetadataEditor, IReadOnlyList<PhotoFile>, IProgress<BulkProgress>, CancellationToken, Task<BulkResult>> operation)
    {
        if (_editor is null || IsBusy || photos.Count == 0) return;

        using var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = $"{verb} {Photos(photos.Count)}…";

        // Progress<T> posts back to the UI thread.
        var progress = new Progress<BulkProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            ProgressPercent = 100.0 * p.Done / p.Total;
            ProgressText = $"{verb} {Photos(p.Total)}… {p.Done:N0} done";
        });

        try
        {
            var result = await operation(_editor, photos.Select(p => p.File).ToList(), progress, cts.Token);

            foreach (var photo in photos)
                if (result.After.TryGetValue(photo.Path, out var metadata)) photo.Metadata = metadata;

            Summary?.Invoke(this, Summarize(result));
            Completed?.Invoke(this, result);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            ProgressText = null;
        }
    }

    private static string Summarize(BulkResult r)
    {
        var parts = new List<string> { $"Updated {Photos(r.Changed)}" };
        if (r.Unchanged > 0) parts.Add($"{r.Unchanged:N0} already up to date");
        if (r.Failures.Count > 0)
        {
            var first = r.Failures[0];
            parts.Add($"{Photos(r.Failures.Count)} couldn't be saved ({Path.GetFileName(first.Path)}: {first.Error.Split('\n')[0].Trim()})");
        }
        return (r.Cancelled ? "Cancelled. " : "") + string.Join(", ", parts) + ".";
    }

    private static string Photos(int count) => count == 1 ? "1 photo" : $"{count:N0} photos";
}
