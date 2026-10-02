using SkiaSharp;

namespace PhotoTag.Core.Tests;

public sealed class EmbeddedThumbnailTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_ReturnsTheThumbnail(bool bigEndian)
    {
        var path = Photo(400, 300, new TestImages.Exif { Thumbnail = TestImages.Thumbnail(160, 120), BigEndian = bigEndian });

        using var thumbnail = Decode(EmbeddedThumbnail.Read(path));

        Assert.Equal((160, 120), (thumbnail.Width, thumbnail.Height));
        Assert.True(IsRed(thumbnail.GetPixel(5, 5)), "expected red at top-left");
        Assert.False(IsRed(thumbnail.GetPixel(150, 110)), "expected blue at bottom-right");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_TurnsTheThumbnailUpright(bool bigEndian)
    {
        // Orientation 6: stored landscape, shown turned 90° clockwise. The thumbnail is stored the same way.
        var path = Photo(400, 300, new TestImages.Exif { Orientation = 6, Thumbnail = TestImages.Thumbnail(160, 120), BigEndian = bigEndian });

        using var thumbnail = Decode(EmbeddedThumbnail.Read(path));

        Assert.Equal((120, 160), (thumbnail.Width, thumbnail.Height));
        Assert.True(IsRed(thumbnail.GetPixel(thumbnail.Width - 5, 5)), "expected red at top-right");
    }

    [Theory]
    [InlineData(600, 400, 160, 107)] // 3:2 in 4:3, with bars above and below
    [InlineData(400, 600, 80, 120)]  // 2:3 in 4:3, with bars at the sides
    public void Read_CutsOffBlackBars(int width, int height, int expectedW, int expectedH)
    {
        var path = Photo(width, height, new TestImages.Exif { Thumbnail = TestImages.Thumbnail(160, 120, new SKSizeI(expectedW, expectedH)) });

        using var thumbnail = Decode(EmbeddedThumbnail.Read(path));

        Assert.Equal((expectedW, expectedH), (thumbnail.Width, thumbnail.Height));
        Assert.True(IsRed(thumbnail.GetPixel(2, 2)), "expected the picture's red corner, not a bar");
    }

    [Fact]
    public void Read_CutsOffBlackBars_ThatTheCameraSizedItsOwnWay()
    {
        // As a Fujifilm X-S10 does for a 3:2 photo: 104 rows of picture, not 107, with 8 rows of bar above.
        var path = Photo(600, 400, new TestImages.Exif
        {
            Thumbnail = TestImages.Thumbnail(160, 120, new SKSizeI(160, 104), new SKPointI(0, 8)),
        });

        using var thumbnail = Decode(EmbeddedThumbnail.Read(path));

        Assert.Equal((160, 104), (thumbnail.Width, thumbnail.Height));
        Assert.True(IsRed(thumbnail.GetPixel(2, 0)), "expected the picture's red corner in the top row");
        Assert.False(IsDark(thumbnail.GetPixel(80, thumbnail.Height - 1)), "expected picture in the bottom row");
    }

    [Fact]
    public void Read_PutsAStretchedThumbnailBackInShape()
    {
        var path = Photo(600, 400, new TestImages.Exif { Thumbnail = TestImages.Thumbnail(160, 120) });

        using var thumbnail = Decode(EmbeddedThumbnail.Read(path));

        Assert.Equal((160, 107), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void Read_FindsAThumbnailThatEndsPastTheFirst64KB_UsingTheSizeInExif()
    {
        // A comment before the EXIF block, and a long description in it, push the thumbnail (and the
        // frame header, which gives the photo's size) past 64 KB.
        var exif = new TestImages.Exif
        {
            Description = new string('x', 40_000),
            PixelWidth = 600,
            PixelHeight = 400,
            Thumbnail = TestImages.Thumbnail(160, 120, new SKSizeI(160, 107)),
        };
        var jpeg = TestImages.Jpeg(600, 400, exif);
        byte[] comment = [0xFF, 0xFE, 0x75, 0x32, .. new byte[30_000]]; // 0x7532 = 30,002, the length with itself
        var path = TestImages.Write(_dir.Path, "photo.jpg", [.. jpeg[..2], .. comment, .. jpeg[2..]]);
        var thumbnailEnd = jpeg.AsSpan().IndexOf(exif.Thumbnail) + comment.Length + exif.Thumbnail.Length;
        Assert.InRange(thumbnailEnd, 64 * 1024 + 1, 70 * 1024);

        using var thumbnail = Decode(EmbeddedThumbnail.Read(path));

        Assert.Equal((160, 107), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void Read_ReturnsNull_WhenThereIsNothingToShow()
    {
        Assert.Null(EmbeddedThumbnail.Read(TestImages.Write(_dir.Path, "plain.jpg", TestImages.Jpeg(400, 300))));
        Assert.Null(EmbeddedThumbnail.Read(Photo(400, 300, new TestImages.Exif { Make = "Canon" }, "no-thumbnail.jpg")));
        Assert.Null(EmbeddedThumbnail.Read(TestImages.Write(_dir.Path, "tiny.jpg", [0xFF, 0xD8])));

        // A panorama with a 4:3 thumbnail: an editor must have changed the photo and left the old one.
        Assert.Null(EmbeddedThumbnail.Read(Photo(1200, 300, new TestImages.Exif { Thumbnail = TestImages.Thumbnail(160, 120) }, "panorama.jpg")));

        // Only JPEGs, by name: anything else isn't read at all.
        var jpeg = TestImages.Jpeg(400, 300, new TestImages.Exif { Thumbnail = TestImages.Thumbnail(160, 120) });
        Assert.Null(EmbeddedThumbnail.Read(TestImages.Write(_dir.Path, "photo.png", jpeg)));
    }

    [Fact]
    public void FromHead_ShrugsOffDamage()
    {
        var jpeg = TestImages.Jpeg(400, 300, new TestImages.Exif
        {
            Make = "Canon", Orientation = 6, PixelWidth = 400, PixelHeight = 300, Thumbnail = TestImages.Thumbnail(160, 120),
        });
        var thumbnailStart = jpeg.AsSpan().IndexOf(TestImages.Thumbnail(160, 120));

        // Every byte of the segment headers and IFDs, set to each of a few values.
        for (var at = 0; at < thumbnailStart + 4; at++)
            foreach (var value in new byte[] { 0x00, 0x01, 0x7F, 0xFF })
            {
                var damaged = (byte[])jpeg.Clone();
                damaged[at] = value;
                EmbeddedThumbnail.FromHead(damaged); // returns something or null, but doesn't throw
            }

        for (var length = 0; length < thumbnailStart + 200; length += 7)
            EmbeddedThumbnail.FromHead(jpeg[..length]);
    }

    private string Photo(int width, int height, TestImages.Exif exif, string name = "photo.jpg") =>
        TestImages.Write(_dir.Path, name, TestImages.Jpeg(width, height, exif));

    private static SKBitmap Decode(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        return SKBitmap.Decode(bytes);
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Blue < 80;

    private static bool IsDark(SKColor c) => c is { Red: < 32, Green: < 32, Blue: < 32 };

    public void Dispose() => _dir.Dispose();
}
