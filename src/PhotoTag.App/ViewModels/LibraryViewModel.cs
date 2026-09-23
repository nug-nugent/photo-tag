using CommunityToolkit.Mvvm.ComponentModel;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// Keeps the <see cref="LibraryIndex"/> in step with the files: scans the open folder tree in
/// the background, records PhotoTag's own edits straight away, and feeds library-wide tag
/// suggestions. Raises <see cref="CountsChanged"/> so the folder tree can refresh its counts.
/// </summary>
public partial class LibraryViewModel(LibraryIndex index, KeywordSuggestions suggestions) : ViewModelBase
{
    private CancellationTokenSource? _scan;

    public LibraryIndex Index { get; } = index;

    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial string? StatusText { get; private set; }

    /// <summary>Completes when the current scan (if any) has finished. For tests and shutdown.</summary>
    public Task ScanCompletion { get; private set; } = Task.CompletedTask;

    public event EventHandler? CountsChanged;

    /// <summary>Starts (or restarts) indexing everything under <paramref name="root"/>.</summary>
    public void StartScan(string root)
    {
        _scan?.Cancel();
        _scan?.Dispose();
        var cts = _scan = new CancellationTokenSource();
        ScanCompletion = ScanAsync(root, cts);
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
            }
        }
    }

    /// <summary>Records metadata that a bulk edit wrote.</summary>
    public async Task PhotosChangedAsync(IReadOnlyDictionary<string, PhotoMetadata> after)
    {
        await Index.UpdateAsync(after.Select(p => (p.Key, p.Value)));
        CountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads one photo after a single edit and records it.</summary>
    public async Task PhotoChangedAsync(string path)
    {
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
    }

    private async Task LoadSuggestionsAsync()
    {
        var keywords = await Index.GetKeywordsAsync();
        suggestions.Add(keywords.Select(k => k.Keyword));
    }

    public void Cancel()
    {
        _scan?.Cancel();
    }
}
