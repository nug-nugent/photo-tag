using SkiaSharp;

namespace PhotoTag.Core.Tests;

/// <summary>One ExifTool process shared by all writer tests, like the app does.</summary>
public sealed class ExifToolFixture : IAsyncDisposable
{
    public ExifToolFixture()
    {
        var path = ExifTool.Locate();
        if (path is not null) ExifTool = new ExifTool(path);
    }

    public ExifTool? ExifTool { get; }

    /// <summary>Skips the test when ExifTool isn't installed, unless CI says it must be.</summary>
    public PhotoMetadataWriter RequireWriter()
    {
        if (ExifTool is null)
        {
            if (Environment.GetEnvironmentVariable("PHOTOTAG_REQUIRE_EXIFTOOL") == "1")
                Assert.Fail("ExifTool is required (PHOTOTAG_REQUIRE_EXIFTOOL=1) but wasn't found.");
            Assert.Skip("ExifTool isn't installed.");
        }
        return new PhotoMetadataWriter(ExifTool);
    }

    public async ValueTask DisposeAsync()
    {
        if (ExifTool is not null) await ExifTool.DisposeAsync();
    }
}

public sealed class PhotoMetadataWriterTests(ExifToolFixture fixture) : IClassFixture<ExifToolFixture>, IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Keywords_RoundTrip_IncludingNonAscii()
    {
        var writer = fixture.RequireWriter();
        var path = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 100));

        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["Beach", "Café", "Zürich", "北京", "Tom & Jerry"] }, Ct);

        Assert.Equal(["Beach", "Café", "Zürich", "北京", "Tom & Jerry"], PhotoMetadata.Read(path).Keywords);
    }

    [Fact]
    public async Task Keywords_ReplaceAndClear()
    {
        var writer = fixture.RequireWriter();
        var path = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 100, xmpKeywords: ["Old", "Stale"]));

        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["New"] }, Ct);
        Assert.Equal(["New"], PhotoMetadata.Read(path).Keywords);

        await writer.WriteAsync(path, new MetadataChanges { Keywords = [] }, Ct);
        Assert.Empty(PhotoMetadata.Read(path).Keywords);
    }

    [Fact]
    public async Task TitleDescriptionAndRating_RoundTrip_AndClear()
    {
        var writer = fixture.RequireWriter();
        var path = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 100));

        await writer.WriteAsync(path, new MetadataChanges
        {
            Title = "Sunset <at> St Ives",
            Description = "First line\nSecond line & more",
            Rating = 3,
        }, Ct);

        var written = PhotoMetadata.Read(path);
        Assert.Equal("Sunset <at> St Ives", written.Title);
        Assert.Equal("First line\nSecond line & more", written.Description);
        Assert.Equal(3, written.Rating);

        await writer.WriteAsync(path, new MetadataChanges { Title = "", Description = "", Rating = 0 }, Ct);

        var cleared = PhotoMetadata.Read(path);
        Assert.Null(cleared.Title);
        Assert.Null(cleared.Description);
        Assert.Null(cleared.Rating);
    }

    [Fact]
    public async Task OnlyRequestedFieldsChange()
    {
        var writer = fixture.RequireWriter();
        var path = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 100));
        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["Keep"], Title = "Keep me" }, Ct);

        await writer.WriteAsync(path, new MetadataChanges { Rating = 5 }, Ct);

        var metadata = PhotoMetadata.Read(path);
        Assert.Equal(["Keep"], metadata.Keywords);
        Assert.Equal("Keep me", metadata.Title);
        Assert.Equal(5, metadata.Rating);
    }

    [Fact]
    public async Task PreservesPixels_ExistingExif_AndModifiedTime()
    {
        var writer = fixture.RequireWriter();
        var exif = new TestImages.Exif { Make = "Canon", Model = "Canon EOS R6", DateTimeOriginal = "2021:05:01 12:00:00", Orientation = 6 };
        var path = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(300, 200, exif));
        var modified = new DateTime(2021, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);
        using var before = SKBitmap.Decode(path);

        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["Tagged"] }, Ct);

        using var after = SKBitmap.Decode(path);
        Assert.Equal(before.Bytes, after.Bytes);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        var metadata = PhotoMetadata.Read(path);
        Assert.Equal("Canon EOS R6", metadata.CameraModel);
        Assert.Equal(new DateTime(2021, 5, 1, 12, 0, 0), metadata.DateTaken);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*_original"));
    }

    [Fact]
    public async Task HandlesUnicodeFileNamesAndSpaces()
    {
        var writer = fixture.RequireWriter();
        var path = TestImages.Write(_dir.Path, "Été 2019 #1 – Zürich.jpg", TestImages.Jpeg(64, 64));

        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["Summer"] }, Ct);

        Assert.Equal(["Summer"], PhotoMetadata.Read(path).Keywords);
    }

    [Fact]
    public async Task Png_GetsXmpKeywords()
    {
        var writer = fixture.RequireWriter();
        var path = Path.Combine(_dir.Path, "graphic.png");
        using (var bitmap = new SKBitmap(32, 32))
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(path, data.ToArray());

        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["Logo"], Rating = 2 }, Ct);

        var metadata = PhotoMetadata.Read(path);
        Assert.Equal(["Logo"], metadata.Keywords);
        Assert.Equal(2, metadata.Rating);
    }

    [Fact]
    public async Task ConcurrentWrites_AreSerializedSafely()
    {
        var writer = fixture.RequireWriter();
        var paths = Enumerable.Range(0, 12)
            .Select(i => TestImages.Write(_dir.Path, $"p{i}.jpg", TestImages.Jpeg(64, 64)))
            .ToList();

        await Task.WhenAll(paths.Select((p, i) => writer.WriteAsync(p, new MetadataChanges { Keywords = [$"Tag{i}"] }, Ct)));

        for (var i = 0; i < paths.Count; i++)
            Assert.Equal([$"Tag{i}"], PhotoMetadata.Read(paths[i]).Keywords);
    }

    [Fact]
    public async Task CorruptFile_Throws_AndToolKeepsWorking()
    {
        var writer = fixture.RequireWriter();
        var corrupt = TestImages.Write(_dir.Path, "corrupt.jpg", [0xFF, 0xD8, 1, 2, 3]);
        var good = TestImages.Write(_dir.Path, "good.jpg", TestImages.Jpeg(64, 64));

        await Assert.ThrowsAsync<ExifToolException>(
            () => writer.WriteAsync(corrupt, new MetadataChanges { Keywords = ["X"] }, Ct));

        await writer.WriteAsync(good, new MetadataChanges { Keywords = ["Still works"] }, Ct);
        Assert.Equal(["Still works"], PhotoMetadata.Read(good).Keywords);
    }

    [Fact]
    public void BuildArguments_SkipsIptcForNonJpeg_AndEscapesValues()
    {
        var png = PhotoMetadataWriter.BuildArguments("a.png", new MetadataChanges { Keywords = ["A"], Description = "x\ny & z" });
        Assert.DoesNotContain(png, a => a.StartsWith("-IPTC", StringComparison.Ordinal));
        Assert.Contains("-XMP-dc:Description=x&#xa;y &amp; z", png);
        Assert.Equal("a.png", png[^1]);

        var jpg = PhotoMetadataWriter.BuildArguments("a.JPG", new MetadataChanges { Keywords = [] });
        Assert.Contains("-IPTC:Keywords=", jpg);
        Assert.Contains("-XMP-dc:Subject=", jpg);
    }

    [Fact]
    public void NormalizeKeywords_TrimsAndDedupes() =>
        Assert.Equal(["Beach", "Dog"], PhotoMetadataWriter.NormalizeKeywords([" Beach ", "", "beach", "Dog", "  "]));

    public void Dispose() => _dir.Dispose();
}
