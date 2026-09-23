using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;
using PhotoTag.Core.Tests;

namespace PhotoTag.App.Tests;

/// <summary>RAW files and RAW+JPEG pairs in the real window.</summary>
public sealed class RawUiTests : UiTestBase
{
    [AvaloniaFact]
    public async Task RawFiles_ShowAsTiles_WithBadges_AndPairsOnce()
    {
        await using var exifTool = RequireExifTool();
        await RawSamples.CopyAsync(RawSamples.PanasonicRw2, DirPath, "P1000001.RW2");
        Photo("P1000001.JPG");
        await RawSamples.CopyAsync(RawSamples.SonyArw, DirPath, "DSC01234.ARW");

        var (window, vm) = await OpenAsync(writer: null, new PhotoRenderer(new RawPreviewExtractor(exifTool)));

        Assert.Equal([("DSC01234.ARW", "RAW"), ("P1000001.JPG", "RAW+JPEG")], vm.Photos.Select(p => (p.FileName, p.Badge)));
        Assert.Contains(FindAll<TextBlock>(window), t => t.Text == "RAW+JPEG" && t.IsEffectivelyVisible);

        // The RAW's thumbnail comes from its embedded preview. (Headless mode doesn't really decode
        // bitmaps, so sizes and orientation are checked in the core RawTests instead.)
        await WaitForAsync(() => vm.Photos.All(p => p.Thumbnail is not null || p.LoadFailed));
        Assert.All(vm.Photos, p => Assert.False(p.LoadFailed, p.LoadFailedText));
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutExifTool_RawTilesSayWhy()
    {
        await RawSamples.CopyAsync(RawSamples.SonyArw, DirPath, "DSC01234.ARW");
        var (window, vm) = await OpenAsync(writer: null);

        await WaitForAsync(() => vm.Photos[0].LoadFailed);

        Assert.Equal("Showing RAW files needs ExifTool.", vm.Photos[0].LoadFailedText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TaggingARaw_WritesASidecar_AndLeavesTheRawAlone()
    {
        await using var exifTool = RequireExifTool();
        await using var previewTool = RequireExifTool();
        var raw = await RawSamples.CopyAsync(RawSamples.FujiRaf, DirPath, "DSCF0001.RAF");
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(raw)));
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool), new PhotoRenderer(new RawPreviewExtractor(previewTool)));

        var details = await SelectSingleAsync(window, vm, 0);
        await WaitForAsync(() => details.Preview is not null);
        Assert.Equal("FUJIFILM X-S10", details.Camera);
        Assert.Equal("RAW file. Tags are saved in DSCF0001.xmp beside it; the RAW itself is never changed.", details.FilesNote);

        Find<AutoCompleteBox>(window, "NewTagBox").Focus();
        window.KeyTextInput("Mountains");
        Press(window, PhysicalKey.Enter);
        await details.SaveCompletion;
        Assert.False(details.SaveFailed, details.SaveStatus);

        Assert.True(File.Exists(Path.Combine(DirPath, "DSCF0001.xmp")));
        Assert.Equal(["Mountains"], PhotoMetadata.Read(raw).Keywords);
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(raw))));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TaggingAPair_TagsBothFiles_AndSearchFindsItOnce()
    {
        await using var exifTool = RequireExifTool();
        var raw = await RawSamples.CopyAsync(RawSamples.PanasonicRw2, DirPath, "P1000001.RW2");
        var jpeg = Photo("P1000001.JPG");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));

        var details = await SelectSingleAsync(window, vm, 0);
        Assert.StartsWith("Shot as RAW+JPEG.", details.FilesNote);
        Find<AutoCompleteBox>(window, "NewTagBox").Focus();
        window.KeyTextInput("Harbour");
        Press(window, PhysicalKey.Enter);
        await details.SaveCompletion;

        Assert.Equal(["Harbour"], PhotoMetadata.Read(jpeg).Keywords);
        Assert.Equal(["Harbour"], PhotoMetadata.Read(raw).Keywords);

        await vm.Library.ScanCompletion;
        await WaitForAsync(async () => (await vm.Library.Index.SearchAsync(DirPath, new PhotoQuery { Keywords = ["harbour"] })).Count == 1);
        vm.SearchText = "harbour";
        Find<AutoCompleteBox>(window, "SearchBox").Focus();
        Press(window, PhysicalKey.Enter);
        await vm.PhotosLoading;
        var result = Assert.Single(vm.Photos);
        Assert.Equal("RAW+JPEG", result.Badge); // search results keep the pair together
        window.Close();
    }
}
