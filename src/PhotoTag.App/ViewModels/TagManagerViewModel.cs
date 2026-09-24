using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>
/// The "Tags" panel: every tag in the open library with its count, and rename, merge (rename to a
/// tag that exists) and delete everywhere. The edits run through <see cref="BulkOperations"/>, so
/// they show progress in the status bar and can be cancelled like any bulk edit.
/// </summary>
public partial class TagManagerViewModel : ViewModelBase
{
    private readonly LibraryViewModel _library;
    private readonly BulkOperations _operations;
    private readonly Func<string?> _root;
    private readonly Func<IReadOnlyList<PhotoItemViewModel>> _shown;
    private IReadOnlyList<TagRowViewModel> _all = [];
    private bool _isOpen;
    private bool _reloadSuggestions;

    /// <param name="root">The open library folder.</param>
    /// <param name="shown">The photos in the grid, so any that an edit changes update on screen.</param>
    public TagManagerViewModel(LibraryViewModel library, BulkOperations operations, Func<string?> root,
        Func<IReadOnlyList<PhotoItemViewModel>> shown)
    {
        _library = library;
        _operations = operations;
        _root = root;
        _shown = shown;
        _operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BulkOperations.IsBusy)) OnPropertyChanged(nameof(CanEdit));
        };
        // Fires once the index has recorded an edit (or a scan has finished).
        _library.CountsChanged += (_, _) => Loading = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_reloadSuggestions)
        {
            _reloadSuggestions = false;
            await _library.ReloadSuggestionsAsync(); // the old name shouldn't be suggested any more
        }
        if (_isOpen) await LoadAsync();
    }

    /// <summary>The tags shown, after <see cref="Filter"/>.</summary>
    [ObservableProperty] public partial IReadOnlyList<TagRowViewModel> Tags { get; private set; } = [];

    [ObservableProperty] public partial string? Filter { get; set; }

    [ObservableProperty] public partial string? Heading { get; private set; }

    /// <summary>"No tags yet", or "No tags match …"; null when there are tags to show.</summary>
    [ObservableProperty] public partial string? EmptyText { get; private set; }

    public bool CanEdit => _operations.IsAvailable && !_operations.IsBusy;

    public bool ExifToolMissing => !_operations.IsAvailable;

    /// <summary>Completes when the list has (re)loaded. For tests.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public void Open()
    {
        _isOpen = true;
        Filter = null;
        Loading = LoadAsync();
    }

    public void Close()
    {
        _isOpen = false;
        foreach (var row in _all) row.Reset();
    }

    partial void OnFilterChanged(string? value) => ApplyFilter();

    private async Task LoadAsync()
    {
        var root = _root();
        IReadOnlyList<KeywordCount> keywords = root is null ? [] : await _library.Index.GetKeywordsAsync(root);
        // Most used first, as the index returns them.
        _all = [.. keywords.Select(k => new TagRowViewModel(this, k))];
        Heading = root is null ? "Tags" : $"Tags in {Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = Filter?.Trim();
        Tags = string.IsNullOrEmpty(filter)
            ? _all
            : [.. _all.Where(t => t.Keyword.Contains(filter, StringComparison.CurrentCultureIgnoreCase))];
        EmptyText = Tags.Count > 0 ? null
            : _all.Count == 0 ? "No tags yet."
            : $"No tags match “{filter}”.";
    }

    internal void StartRename(TagRowViewModel row)
    {
        foreach (var other in _all) other.Reset();
        row.NewName = row.Keyword;
        row.IsRenaming = true;
    }

    internal void StartDelete(TagRowViewModel row)
    {
        foreach (var other in _all) other.Reset();
        row.IsConfirmingDelete = true;
    }

    internal async Task RenameAsync(TagRowViewModel row)
    {
        var to = (row.NewName ?? "").Trim();
        if (to.Length == 0)
        {
            row.Error = "Enter a name for the tag.";
            return;
        }
        if (to.Contains(','))
        {
            row.Error = "Tags can't contain commas.";
            return;
        }
        row.Reset();
        // Renaming to exactly the same name only does something if other spellings need tidying.
        if (to == row.Keyword && row.OtherSpellings.Count == 0) return;

        await RunAsync(row.Keyword, files => _operations.RenameKeywordAsync(files, row.Keyword, to, _shown()));
    }

    internal async Task DeleteAsync(TagRowViewModel row)
    {
        row.Reset();
        await RunAsync(row.Keyword, files => _operations.DeleteKeywordAsync(files, row.Keyword, _shown()));
    }

    private async Task RunAsync(string keyword, Func<IReadOnlyList<PhotoFile>, Task> edit)
    {
        if (!CanEdit || _root() is not { } root) return;

        // Every spelling (the index matches tags ignoring case); pairs need their RAW so both files change.
        var paths = await _library.Index.SearchAsync(root, new PhotoQuery { Keywords = [keyword] });
        var files = await Task.Run(() => paths.Where(File.Exists).Select(PhotoFiles.WithCompanions).ToList());
        if (files.Count == 0)
        {
            Loading = LoadAsync(); // the index was out of date
            return;
        }

        _reloadSuggestions = true;
        await edit(files);
    }
}

/// <summary>One tag in the <see cref="TagManagerViewModel"/> list, with its rename and delete state.</summary>
public partial class TagRowViewModel(TagManagerViewModel owner, KeywordCount tag) : ViewModelBase
{
    public TagManagerViewModel Owner => owner;
    public string Keyword => tag.Keyword;
    public int Count => tag.Count;
    public IReadOnlyList<string> OtherSpellings => tag.OtherSpellings;

    public string CountText => Count.ToString("N0");

    public string? SpellingsText => OtherSpellings.Count == 0 ? null
        : $"Also spelled {string.Join(", ", OtherSpellings.Select(s => $"“{s}”"))}. Rename to tidy them up.";

    public string DeleteQuestion =>
        $"Remove “{Keyword}” from {(Count == 1 ? "1 photo" : $"{Count:N0} photos")}? This changes the photos' files.";

    [ObservableProperty] public partial bool IsRenaming { get; set; }
    [ObservableProperty] public partial bool IsConfirmingDelete { get; set; }
    [ObservableProperty] public partial string? NewName { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    internal void Reset()
    {
        IsRenaming = false;
        IsConfirmingDelete = false;
        Error = null;
    }

    [RelayCommand]
    private void StartRename() => owner.StartRename(this);

    [RelayCommand]
    private Task Rename() => owner.RenameAsync(this);

    [RelayCommand]
    private void StartDelete() => owner.StartDelete(this);

    [RelayCommand]
    private Task Delete() => owner.DeleteAsync(this);

    [RelayCommand]
    private void Cancel() => Reset();
}
