using System.Collections.ObjectModel;
using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>Every location, city, state and country in the library, to autocomplete the place boxes.</summary>
public sealed class PlaceSuggestions
{
    private readonly Dictionary<TextField, KeywordSuggestions> _byField =
        TextFields.Places.ToDictionary(f => f, _ => new KeywordSuggestions());

    public ObservableCollection<string> For(TextField field) => _byField[field].Items;

    public void Add(TextField field, string? value)
    {
        if (_byField.TryGetValue(field, out var suggestions) && !string.IsNullOrWhiteSpace(value))
            suggestions.Add([value.Trim()]);
    }

    public void Add(PhotoMetadata metadata)
    {
        foreach (var field in TextFields.Places) Add(field, metadata.Get(field));
    }

    public async Task LoadAsync(LibraryIndex index)
    {
        foreach (var field in TextFields.Places) _byField[field].Add(await index.GetPlaceValuesAsync(field));
    }
}

/// <summary>How the text fields are named in the UI.</summary>
public static class TextFieldNames
{
    public static string Label(this TextField field) => field switch
    {
        TextField.State => "State/Province",
        _ => field.ToString(),
    };

    /// <summary>For use mid-sentence: "the city", "the state/province".</summary>
    public static string Lower(this TextField field) => field.Label().ToLowerInvariant();
}
