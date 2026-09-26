namespace PhotoTag.Core;

/// <summary>The list fields PhotoTag edits: several values per photo, each added and removed on its own.</summary>
public enum ListField
{
    /// <summary>Tags (keywords): XMP dc:subject, plus IPTC Keywords for JPEGs.</summary>
    Tags,

    /// <summary>People shown in the photo: XMP Iptc4xmpExt:PersonInImage.</summary>
    People,
}

/// <summary>Gets and sets a <see cref="ListField"/> on metadata and changes, so code can serve both.</summary>
public static class ListFields
{
    public static IReadOnlyList<ListField> All { get; } = Enum.GetValues<ListField>();

    public static IReadOnlyList<string> Get(this PhotoMetadata metadata, ListField field) => field switch
    {
        ListField.Tags => metadata.Keywords,
        ListField.People => metadata.People,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static PhotoMetadata With(this PhotoMetadata metadata, ListField field, IReadOnlyList<string> values) => field switch
    {
        ListField.Tags => metadata with { Keywords = values },
        ListField.People => metadata with { People = values },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static IReadOnlyList<string>? Get(this MetadataChanges changes, ListField field) => field switch
    {
        ListField.Tags => changes.Keywords,
        ListField.People => changes.People,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>Sets a list to write; empty clears it.</summary>
    public static MetadataChanges With(this MetadataChanges changes, ListField field, IReadOnlyList<string> values) => field switch
    {
        ListField.Tags => changes with { Keywords = values },
        ListField.People => changes with { People = values },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}
