namespace PhotoTag.Core;

/// <summary>The free-text fields PhotoTag edits, each holding one value per photo.</summary>
public enum TextField
{
    Title,
    Description,

    /// <summary>A place within the city: a beach, a venue, a street (IPTC "Sublocation").</summary>
    Location,
    City,

    /// <summary>State, province or county.</summary>
    State,
    Country,
}

/// <summary>Gets and sets a <see cref="TextField"/> on metadata and changes, so code can loop over them.</summary>
public static class TextFields
{
    public static IReadOnlyList<TextField> All { get; } = Enum.GetValues<TextField>();

    public static IReadOnlyList<TextField> Places { get; } = [TextField.Location, TextField.City, TextField.State, TextField.Country];

    public static string? Get(this PhotoMetadata metadata, TextField field) => field switch
    {
        TextField.Title => metadata.Title,
        TextField.Description => metadata.Description,
        TextField.Location => metadata.Location,
        TextField.City => metadata.City,
        TextField.State => metadata.State,
        TextField.Country => metadata.Country,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static PhotoMetadata With(this PhotoMetadata metadata, TextField field, string? value) => field switch
    {
        TextField.Title => metadata with { Title = value },
        TextField.Description => metadata with { Description = value },
        TextField.Location => metadata with { Location = value },
        TextField.City => metadata with { City = value },
        TextField.State => metadata with { State = value },
        TextField.Country => metadata with { Country = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static string? Get(this MetadataChanges changes, TextField field) => field switch
    {
        TextField.Title => changes.Title,
        TextField.Description => changes.Description,
        TextField.Location => changes.Location,
        TextField.City => changes.City,
        TextField.State => changes.State,
        TextField.Country => changes.Country,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>Sets a field to write; empty clears it.</summary>
    public static MetadataChanges With(this MetadataChanges changes, TextField field, string? value) => field switch
    {
        TextField.Title => changes with { Title = value },
        TextField.Description => changes with { Description = value },
        TextField.Location => changes with { Location = value },
        TextField.City => changes with { City = value },
        TextField.State => changes with { State = value },
        TextField.Country => changes with { Country = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}
