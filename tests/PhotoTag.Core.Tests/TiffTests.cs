using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace PhotoTag.Core.Tests;

public sealed class TiffTests(ExifToolFixture fixture) : IClassFixture<ExifToolFixture>, IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, TestTiffs.Options> Layouts = new()
    {
        { "strips", new TestTiffs.Options { RowsPerStrip = 7 } }, // the last strip is short
        { "one strip", new TestTiffs.Options { RowsPerStrip = 0, Compression = Compression.LZW } },
        { "tiles", new TestTiffs.Options { TileSize = 16 } }, // partial tiles on the right and bottom
        { "LZW", new TestTiffs.Options { Compression = Compression.LZW } },
        { "Deflate", new TestTiffs.Options { Compression = Compression.DEFLATE } },
        { "16-bit", new TestTiffs.Options { SixteenBit = true } },
        { "greyscale", new TestTiffs.Options { Grey = true } },
        { "alpha", new TestTiffs.Options { Alpha = true, TileSize = 16 } },
    };

    public static TheoryData<string> LayoutNames => new(Layouts.Keys);

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void Render_ShowsEveryLayout_InEveryOrientation(string layout)
    {
        var options = Layouts[layout];
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var path = TestImages.Write(_dir.Path, $"{orientation}.tif", TestTiffs.Tiff(40, 30, options with { Orientation = orientation }));

            using var rendered = SKBitmap.Decode(ImageRenderer.Render(path, 100));
            using var stored = TestTiffs.Quadrants(40, 30);
            using var expected = ImageRenderer.ApplyOrientation(stored, (SKEncodedOrigin)orientation);

            Assert.Equal((expected.Width, expected.Height), (rendered.Width, rendered.Height));
            foreach (var (x, y) in QuadrantCentres(expected))
                Assert.True(Class(expected.GetPixel(x, y), grey: false) == Class(rendered.GetPixel(x, y), options.Grey),
                    $"{layout}, orientation {orientation}: expected {expected.GetPixel(x, y)} at ({x},{y}), got {rendered.GetPixel(x, y)}");
        }
    }

    [Theory]
    [InlineData(1000, 600, 100, 100, 60)]
    [InlineData(1003, 601, 100, 100, 60)] // blocks don't divide evenly
    [InlineData(300, 900, 128, 43, 128)]
    [InlineData(90, 50, 300, 90, 50)]     // never enlarged
    public void Render_FitsWithinMaxSize(int width, int height, int max, int expectedW, int expectedH)
    {
        var path = TestImages.Write(_dir.Path, "big.tiff", TestTiffs.Tiff(width, height, new TestTiffs.Options { RowsPerStrip = 13 }));

        using var rendered = SKBitmap.Decode(ImageRenderer.Render(path, max));

        Assert.Equal((expectedW, expectedH), (rendered.Width, rendered.Height));
        Assert.Equal("red", Class(rendered.GetPixel(rendered.Width / 4, rendered.Height / 4), grey: false));
        Assert.Equal("white", Class(rendered.GetPixel(rendered.Width * 3 / 4, rendered.Height * 3 / 4), grey: false));
    }

    [Fact]
    public void Render_ThrowsForCorruptFile()
    {
        var path = TestImages.Write(_dir.Path, "broken.tif", [0x49, 0x49, 0x2A, 0x00, 1, 2, 3, 4]);

        Assert.Throws<InvalidDataException>(() => ImageRenderer.Render(path, 100));
    }

    [Fact]
    public void Render_ShowsWhatItCan_OfATruncatedFile()
    {
        var bytes = TestTiffs.WithLastStripMissing(TestTiffs.Tiff(40, 30, new TestTiffs.Options { RowsPerStrip = 5 }));
        var path = TestImages.Write(_dir.Path, "cut.tif", bytes);

        using var rendered = SKBitmap.Decode(ImageRenderer.Render(path, 100));

        Assert.Equal((40, 30), (rendered.Width, rendered.Height));
        Assert.Equal("red", Class(rendered.GetPixel(10, 7), grey: false));
        Assert.Equal("white", Class(rendered.GetPixel(30, 22), grey: false));
    }

    [Fact]
    public void Render_ThrowsWhenNothingCanBeRead()
    {
        var bytes = TestTiffs.WithLastStripMissing(TestTiffs.Tiff(40, 30, new TestTiffs.Options { RowsPerStrip = 0, Compression = Compression.LZW }));
        var path = TestImages.Write(_dir.Path, "cut.tif", bytes);

        Assert.Throws<InvalidDataException>(() => ImageRenderer.Render(path, 100));
    }

    [Fact]
    public void Photos_IncludeTiffs()
    {
        foreach (var name in new[] { "scan.tif", "print.TIFF", "raw.dng" }) File.WriteAllBytes(Path.Combine(_dir.Path, name), []);

        var photos = PhotoFiles.EnumeratePhotos(_dir.Path).Select(p => Path.GetFileName(p.Path));

        Assert.Equal(["print.TIFF", "raw.dng", "scan.tif"], photos);
        Assert.True(PhotoFiles.IsTiff("print.TIFF"));
        Assert.False(PhotoFiles.IsTiff("raw.dng")); // TIFF inside, but a RAW: shown from its preview, tagged in a sidecar
    }

    [Fact]
    public void Metadata_ReadsSize()
    {
        var path = TestImages.Write(_dir.Path, "scan.tif", TestTiffs.Tiff(40, 30, new TestTiffs.Options { TileSize = 16 }));

        var metadata = PhotoMetadata.Read(path);

        Assert.Equal((40, 30), (metadata.Width, metadata.Height));
    }

    [Fact]
    public async Task Tags_AreWrittenIntoTheTiff_AndStillShow()
    {
        var writer = fixture.RequireWriter();
        var path = TestImages.Write(_dir.Path, "scan.tif", TestTiffs.Tiff(40, 30, new TestTiffs.Options { Compression = Compression.LZW }));

        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["Scan", "Grandma"], Title = "Wedding", Favourite = true }, Ct);

        var m = PhotoMetadata.Read(path);
        Assert.Equal(["Scan", "Grandma"], m.Keywords);
        Assert.Equal("Wedding", m.Title);
        Assert.True(m.IsFavourite);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.xmp")); // in the file, not a sidecar
        using var rendered = SKBitmap.Decode(ImageRenderer.Render(path, 100));
        Assert.Equal((40, 30), (rendered.Width, rendered.Height));
    }

    [Fact]
    public async Task Tags_RemovedFromATiff_DontComeBackFromIptc()
    {
        // Lightroom and Photoshop also write IPTC and the EXIF description into TIFFs, and PhotoTag reads those.
        var exifTool = fixture.RequireExifTool();
        var writer = new PhotoMetadataWriter(exifTool);
        var path = TestImages.Write(_dir.Path, "scan.tif", TestTiffs.Tiff(40, 30));
        await exifTool.ExecuteAsync(["-overwrite_original", "-IPTC:Keywords=Old", "-IPTC:Keywords=Stale",
            "-EXIF:ImageDescription=Old caption", "-IPTC:City=Old town", path], Ct);
        Assert.Equal(["Old", "Stale"], PhotoMetadata.Read(path).Keywords);

        await writer.WriteAsync(path, new MetadataChanges { Keywords = ["New"], Description = "", City = "" }, Ct);

        var m = PhotoMetadata.Read(path);
        Assert.Equal(["New"], m.Keywords);
        Assert.Null(m.Description);
        Assert.Null(m.City);
    }

    private static IEnumerable<(int X, int Y)> QuadrantCentres(SKBitmap b) =>
        [(b.Width / 4, b.Height / 4), (b.Width * 3 / 4, b.Height / 4), (b.Width / 4, b.Height * 3 / 4), (b.Width * 3 / 4, b.Height * 3 / 4)];

    /// <summary>Which of the four quadrant colours (or grey shades) a pixel is, allowing for JPEG's losses.</summary>
    private static string Class(SKColor c, bool grey)
    {
        if (grey)
        {
            var level = (c.Red + c.Green + c.Blue) / 3; // see TestTiffs.Grey
            return level switch { < 64 => "red", < 106 => "green", < 190 => "blue", _ => "white" };
        }
        return (c.Red > 160, c.Green > 160, c.Blue > 160) switch
        {
            (true, true, true) => "white",
            (true, false, false) => "red",
            (false, true, false) => "green",
            (false, false, true) => "blue",
            _ => c.ToString(),
        };
    }

    public void Dispose() => _dir.Dispose();
}
