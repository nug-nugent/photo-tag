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

    /// <summary>What the last edit changed, until it's undone or another edit starts.</summary>
    private IReadOnlyList<BulkChange>? _undo;

    [ObservableProperty] public partial bool CanUndo { get; private set; }

    /// <summary>E.g. "Undo adding “Beach” to 12 photos".</summary>
    [ObservableProperty] public partial string? UndoToolTip { get; private set; }
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

    // Tag management works on photos across the library, most of them not on screen; the grid's
    // photos are passed as "shown" so any that were changed update too.

    public Task RenameKeywordAsync(IReadOnlyList<PhotoFile> files, string from, string to, IReadOnlyList<PhotoItemViewModel> shown)
    {
        suggestions.Add([to]);
        var verb = from.Equals(to, StringComparison.OrdinalIgnoreCase) ? $"Tidying “{to}” in" : $"Renaming “{from}” to “{to}” in";
        return RunAsync(files, shown, verb, (e, paths, p, ct) => e.RenameKeywordAsync(paths, from, to, p, ct));
    }

    public Task DeleteKeywordAsync(IReadOnlyList<PhotoFile> files, string keyword, IReadOnlyList<PhotoItemViewModel> shown) =>
        RunAsync(files, shown, $"Removing “{keyword}” from", (e, paths, p, ct) => e.RemoveKeywordsAsync(paths, [keyword], p, ct));

    /// <summary>
    /// Puts back what the last edit changed. Photos edited again since are left alone. The grid's
    /// photos are passed as "shown" so any that change update on screen.
    /// </summary>
    public Task UndoAsync(IReadOnlyList<PhotoItemViewModel> shown)
    {
        if (_undo is not { } changes || IsBusy) return Task.CompletedTask;
        suggestions.Add(changes.SelectMany(c => c.Before.Keywords)); // e.g. a deleted tag is back
        var photos = changes.Select(c => c.Photo).Distinct().Select(p => PhotoFile.Single(p)).ToList();
        return RunAsync(photos, shown, "Undoing the last change to", (e, _, p, ct) => e.UndoAsync(changes, p, ct), isUndo: true);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        ProgressText = "Cancelling…";
    }

    private Task RunAsync(IReadOnlyList<PhotoItemViewModel> photos, string verb, Operation operation) =>
        RunAsync([.. photos.Select(p => p.File)], photos, verb, operation);

    private delegate Task<BulkResult> Operation(BulkMetadataEditor editor, IReadOnlyList<PhotoFile> files,
        IProgress<BulkProgress> progress, CancellationToken cancellationToken);

    private async Task RunAsync(IReadOnlyList<PhotoFile> files, IReadOnlyList<PhotoItemViewModel> shown, string verb, Operation operation,
        bool isUndo = false)
    {
        if (_editor is null || IsBusy || files.Count == 0) return;

        _undo = null;
        CanUndo = false;
        UndoToolTip = null;

        using var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = $"{verb} {Photos(files.Count)}…";

        // Progress<T> posts back to the UI thread.
        var progress = new Progress<BulkProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            ProgressPercent = 100.0 * p.Done / p.Total;
            ProgressText = $"{verb} {Photos(p.Total)}… {p.Done:N0} done";
        });

        try
        {
            var result = await operation(_editor, files, progress, cts.Token);

            foreach (var photo in shown)
                if (result.After.TryGetValue(photo.Path, out var metadata))
                {
                    photo.Metadata = metadata;
                    photo.IsFavourite = metadata.IsFavourite;
                }

            if (!isUndo && result.Written.Count > 0)
            {
                _undo = result.Written;
                UndoToolTip = $"Undo {char.ToLowerInvariant(verb[0])}{verb[1..]} {Photos(result.Changed)}";
                CanUndo = true;
            }

            Summary?.Invoke(this, isUndo ? SummarizeUndo(result) : Summarize(result));
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

    private static string SummarizeUndo(BulkResult r)
    {
        var parts = new List<string> { $"Undone: put back {Photos(r.Changed)}" };
        if (r.ChangedSince > 0) parts.Add($"left {Photos(r.ChangedSince)} alone (edited again since)");
        if (r.Failures.Count > 0) parts.Add($"{Photos(r.Failures.Count)} couldn't be saved");
        return (r.Cancelled ? "Cancelled. " : "") + string.Join(", ", parts) + ".";
    }

    private static string Photos(int count) => count == 1 ? "1 photo" : $"{count:N0} photos";
}
