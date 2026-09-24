namespace PhotoTag.Core.Tests;

public sealed class KeywordHierarchyTests
{
    private static readonly string[] Flat = ["Places", "UK", "Cornwall", "Beach"];
    private static readonly string[] Nested = ["Places|UK|Cornwall", "Beach"];

    private static IReadOnlyList<string> Without(params string[] removed) => [.. Flat.Where(k => !removed.Contains(k))];

    [Fact]
    public void RemovingATag_DropsItFromEachPath_KeepingTheRest()
    {
        Assert.Equal(["Places|UK", "Beach"], KeywordHierarchy.Update(Nested, Flat, Without("Cornwall")));
        Assert.Equal(["Places|Cornwall", "Beach"], KeywordHierarchy.Update(Nested, Flat, Without("UK")));
        Assert.Equal(["Places|UK|Cornwall"], KeywordHierarchy.Update(Nested, ["Places", "UK", "Cornwall", "BEACH"], ["Places", "UK", "Cornwall"]));
    }

    [Fact]
    public void RenamingATag_RenamesItInPlace_AndMergesDuplicates()
    {
        var renamed = KeywordHierarchy.Update(Nested, Flat, ["Places", "UK", "Kernow", "Beach"], new KeywordRename("cornwall", "Kernow"));
        Assert.Equal(["Places|UK|Kernow", "Beach"], renamed);

        string[] both = ["Places|Seaside", "Places|Beach"];
        Assert.Equal(["Places|Beach"],
            KeywordHierarchy.Update(both, ["Places", "Seaside", "Beach"], ["Places", "Beach"], new KeywordRename("Seaside", "Beach")));

        // Renaming to itself tidies the spelling.
        Assert.Equal(["Places|Beach"], KeywordHierarchy.Update(["Places|beach"], ["Places", "Beach"], ["Places", "Beach"], new KeywordRename("Beach", "Beach")));
    }

    [Fact]
    public void PartsThatWereNeverFlatTags_AreKept()
    {
        // Lightroom can leave parent keywords out of the flat list.
        Assert.Equal(["Places|UK"], KeywordHierarchy.Update(["Places|UK|Cornwall"], ["Cornwall"], []));
    }

    [Fact]
    public void NothingChanged_ReturnsTheSameList()
    {
        Assert.Same(Nested, KeywordHierarchy.Update(Nested, Flat, [.. Flat, "Dog"])); // adding a tag leaves them alone
        IReadOnlyList<string> none = [];
        Assert.Same(none, KeywordHierarchy.Update(none, Flat, []));
    }
}
