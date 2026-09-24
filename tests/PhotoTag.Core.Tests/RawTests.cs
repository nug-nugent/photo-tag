using System.Security.Cryptography;
using SkiaSharp;

namespace PhotoTag.Core.Tests;

/// <summary>Camera RAW support, tested against real files from seven cameras.</summary>
public sealed class RawTests(ExifToolFixture fixture) : IClassFixture<ExifToolFixture>, IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, int> Cameras => new()
    {
        { RawSamples.CanonCr3, "Canon EOS R6", 3408 },
        { RawSamples.CanonCr2, "Canon EOS 40D", 3888 },
        { RawSamples.NikonNef, "NIKON Z5_2", 4000 },
        { RawSamples.SonyArw, "SONY ILCE-7S", 2768 },
        { RawSamples.FujiRaf, "FUJIFILM X-S10", 6240 },
        { RawSamples.RicohDng, "RICOH GR", 4960 },
        { RawSamples.PanasonicRw2, "Panasonic DMC-LX7", 1920 },
    };

    [Theory]
    [MemberData(nameof(Cameras))]
    public async Task Metadata_IsReadFromEveryFormat(string sample, string camera, int width)
    {
        var path = await RawSamples.CopyAsync(sample, _dir.Path);

        var metadata = PhotoMetadata.Read(path);

        Assert.Equal(camera, metadata.Camera);
        Assert.Equal(width, metadata.Width);
        Assert.NotNull(metadata.DateTaken);
        Assert.NotNull(metadata.ExposureTime);
        Assert.NotNull(metadata.Iso);
    }

    public static TheoryData<string> Samples => new(RawSamples.All);

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Previews_RenderAtThumbnailAndPanelSizes(string sample)
    {
        var renderer = new PhotoRenderer(new RawPreviewExtractor(fixture.RequireExifTool()));
        var path = await RawSamples.CopyAsync(sample, _dir.Path);

        using var thumbnail = SKBitmap.Decode(await renderer.RenderAsync(path, 320, Ct));
        Assert.Equal(320, Math.Max(thumbnail.Width, thumbnail.Height));
        Assert.True(thumbnail.Width > thumbnail.Height, "all samples are landscape");

        using var panel = SKBitmap.Decode(await renderer.RenderAsync(path, 1200, Ct));
        // Ricoh's DNG only embeds a 640 px preview; everything else has one at least 1200 px.
        Assert.Equal(sample == RawSamples.RicohDng ? 640 : 1200, Math.Max(panel.Width, panel.Height));
    }

    [Fact]
    public async Task Previews_PickTheSmallestEmbeddedJpegThatIsBigEnough()
    {
        // This Nikon embeds three: 640 px, 1620 px and full size (3984 px).
        var extractor = new RawPreviewExtractor(fixture.RequireExifTool());
        var path = await RawSamples.CopyAsync(RawSamples.NikonNef, _dir.Path);

        Assert.Equal(640, (await extractor.ExtractAsync(path, 320, Ct))!.Width);
        Assert.Equal(1620, (await extractor.ExtractAsync(path, 1200, Ct))!.Width);
        Assert.Equal(3984, (await extractor.ExtractAsync(path, 3000, Ct))!.Width);
        Assert.Equal(3984, (await extractor.ExtractAsync(path, 9999, Ct))!.Width); // nothing big enough: largest
    }

    [Fact]
    public async Task Previews_FollowTheRawsOrientation()
    {
        var exifTool = fixture.RequireExifTool();
        var path = await RawSamples.CopyAsync(RawSamples.CanonCr3, _dir.Path);
        // Mark the copy as shot in portrait (rotate 90° clockwise), as the camera would.
        await exifTool.ExecuteAsync(["-Orientation#=6", "-overwrite_original", path], Ct);

        using var thumbnail = SKBitmap.Decode(await new PhotoRenderer(new RawPreviewExtractor(exifTool)).RenderAsync(path, 320, Ct));

        Assert.True(thumbnail.Height > thumbnail.Width, $"expected portrait, got {thumbnail.Width}x{thumbnail.Height}");
    }

    [Fact]
    public async Task WithoutExifTool_RawPreviewsExplainWhy()
    {
        var path = await RawSamples.CopyAsync(RawSamples.SonyArw, _dir.Path);

        var error = await Assert.ThrowsAsync<PreviewUnavailableException>(() => PhotoRenderer.ImagesOnly.RenderAsync(path, 320, Ct));
        Assert.Contains("ExifTool", error.Message);
    }

    [Fact]
    public async Task Tags_GoInASidecar_AndTheRawIsNeverModified()
    {
        var writer = fixture.RequireWriter();
        var raw = await RawSamples.CopyAsync(RawSamples.SonyArw, _dir.Path, "DSC01234.ARW");
        var hash = Hash(raw);

        await writer.WriteAsync(raw, new MetadataChanges { Keywords = ["Beach", "Café"], Favourite = true, Title = "Dusk" }, Ct);

        Assert.Equal(hash, Hash(raw));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "DSC01234.xmp")), "sidecar should be named like Lightroom's");
        var metadata = PhotoMetadata.Read(raw);
        Assert.Equal(["Beach", "Café"], metadata.Keywords);
        Assert.True(metadata.IsFavourite);
        Assert.Equal("Dusk", metadata.Title);
        Assert.Equal("SONY ILCE-7S", metadata.Camera); // still read from the RAW itself

        // Clearing works too: the sidecar is authoritative, even when empty.
        await writer.WriteAsync(raw, new MetadataChanges { Keywords = [], Favourite = false }, Ct);
        Assert.Empty(PhotoMetadata.Read(raw).Keywords);
        Assert.False(PhotoMetadata.Read(raw).IsFavourite);
        Assert.Equal(hash, Hash(raw));
    }

    [Fact]
    public async Task ANewSidecar_KeepsTagsAlreadyEmbeddedInTheRaw()
    {
        var exifTool = fixture.RequireExifTool();
        var writer = fixture.RequireWriter();
        var raw = await RawSamples.CopyAsync(RawSamples.CanonCr3, _dir.Path);
        // Some other app embedded tags in the RAW itself.
        await exifTool.ExecuteAsync(["-XMP-dc:Subject=Embedded", "-XMP-dc:Title=From camera app", "-XMP-xmp:Rating=3", "-overwrite_original", raw], Ct);

        await writer.WriteAsync(raw, new MetadataChanges { Description = "Added in PhotoTag" }, Ct);

        var metadata = PhotoMetadata.Read(raw);
        Assert.Equal(["Embedded"], metadata.Keywords);
        Assert.Equal("From camera app", metadata.Title);
        Assert.Equal(3, metadata.Rating); // a rating set elsewhere survives, though PhotoTag only shows favourites
        Assert.False(metadata.IsFavourite);
    }

    [Fact]
    public async Task ExistingDarktableStyleSidecars_AreReadAndUpdated()
    {
        var writer = fixture.RequireWriter();
        var raw = await RawSamples.CopyAsync(RawSamples.NikonNef, _dir.Path, "shot.nef");
        var sidecar = raw + ".xmp";
        File.WriteAllText(sidecar, """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
             <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:subject><rdf:Bag><rdf:li>From darktable</rdf:li></rdf:Bag></dc:subject>
             </rdf:Description></rdf:RDF></x:xmpmeta>
            """);

        Assert.Equal(["From darktable"], PhotoMetadata.Read(raw).Keywords);

        await writer.WriteAsync(raw, new MetadataChanges { Keywords = ["From darktable", "PhotoTag"] }, Ct);

        Assert.Equal(["From darktable", "PhotoTag"], PhotoMetadata.Read(raw).Keywords);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "shot.xmp")), "should update the existing sidecar, not add another");
    }

    [Fact]
    public async Task RawAndJpegPairs_AreTaggedTogether()
    {
        var writer = fixture.RequireWriter();
        var raw = await RawSamples.CopyAsync(RawSamples.PanasonicRw2, _dir.Path, "P1000001.RW2");
        var jpeg = TestImages.Write(_dir.Path, "P1000001.JPG", TestImages.Jpeg(120, 80));

        var photo = Assert.Single(PhotoFiles.EnumeratePhotos(_dir.Path));
        Assert.Equal(jpeg, photo.Path);
        Assert.Equal([raw], photo.Companions);

        await writer.WriteAsync(photo, new MetadataChanges { Keywords = ["Pair"], Favourite = true }, Ct);

        Assert.Equal(["Pair"], PhotoMetadata.Read(jpeg).Keywords); // in the JPEG itself
        Assert.Equal(["Pair"], PhotoMetadata.Read(raw).Keywords);  // via the RAW's sidecar
        Assert.True(PhotoMetadata.Read(jpeg).IsFavourite);
        Assert.True(PhotoMetadata.Read(raw).IsFavourite);
    }

    [Fact]
    public async Task BulkEdits_ReachBothFilesOfAPair_EvenWhenTheyDisagree()
    {
        var writer = fixture.RequireWriter();
        var raw = await RawSamples.CopyAsync(RawSamples.PanasonicRw2, _dir.Path, "P1.RW2");
        var jpeg = TestImages.Write(_dir.Path, "P1.JPG", TestImages.Jpeg(120, 80, xmpKeywords: ["Beach"])); // RAW lacks it

        var result = await new BulkMetadataEditor(writer).AddKeywordsAsync(PhotoFiles.EnumeratePhotos(_dir.Path), ["Beach"], cancellationToken: Ct);

        Assert.Equal((1, 0), (result.Changed, result.Unchanged));
        Assert.Equal(["Beach"], PhotoMetadata.Read(raw).Keywords);
        Assert.Equal(["Beach"], PhotoMetadata.Read(jpeg).Keywords);
    }

    [Fact]
    public async Task Index_CountsPairsOnce_AndNoticesSidecarEdits()
    {
        var writer = fixture.RequireWriter();
        await RawSamples.CopyAsync(RawSamples.PanasonicRw2, _dir.Path, "pair.RW2");
        TestImages.Write(_dir.Path, "pair.JPG", TestImages.Jpeg(120, 80));
        var lone = await RawSamples.CopyAsync(RawSamples.RicohDng, _dir.Path, "lone.DNG");
        using var index = new LibraryIndex(Path.Combine(_dir.Path, "index", "library.db"));
        await index.ScanAsync(_dir.Path, cancellationToken: Ct);
        Assert.Equal(new FolderCounts(2, 0), await index.GetFolderCountsAsync(_dir.Path));

        // Another app (Lightroom, say) tags the RAW by writing a sidecar.
        await writer.WriteAsync(lone, new MetadataChanges { Keywords = ["Elsewhere"] }, Ct);
        var rescan = await index.ScanAsync(_dir.Path, cancellationToken: Ct);

        Assert.Equal(1, rescan.Updated);
        Assert.Equal([lone], await index.SearchAsync(_dir.Path, new PhotoQuery { Keywords = ["elsewhere"] }));
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose() => _dir.Dispose();
}
