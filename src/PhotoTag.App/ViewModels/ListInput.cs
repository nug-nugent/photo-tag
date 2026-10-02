using PhotoTag.Core;

namespace PhotoTag.App.ViewModels;

/// <summary>What's typed in a tag or person box: several at once, separated by semicolons or commas.</summary>
internal static class ListInput
{
    /// <summary>"Beach; Family, Dog" is three, trimmed, without blanks or repeats (ignoring case).</summary>
    public static IReadOnlyList<string> Split(string? text) => PhotoMetadataWriter.NormalizeKeywords((text ?? "").Split([';', ',']));
}
