namespace PhotoTag.Core.Tests;

public sealed class PhotoMetadataTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Read_ExifFields()
    {
        var exif = new TestImages.Exif
        {
            Make = "Canon",
            Model = "Canon EOS R6",
            DateTimeOriginal = "2019:07:14 10:30:05",
            Iso = 400,
        };
        var path = TestImages.Write(_dir.Path, "exif.jpg", TestImages.Jpeg(320, 240, exif));

        var metadata = PhotoMetadata.Read(path);

        Assert.Equal(new DateTime(2019, 7, 14, 10, 30, 5), metadata.DateTaken);
        Assert.Equal("Canon", metadata.CameraMake);
        Assert.Equal("Canon EOS R6", metadata.CameraModel);
        Assert.Equal("Canon EOS R6", metadata.Camera); // make not repeated
        Assert.Equal(400, metadata.Iso);
        Assert.Equal((320, 240), (metadata.Width, metadata.Height));
    }

    [Fact]
    public void Read_XmpKeywordsAndRating_FourStarsIsNotAFavourite()
    {
        var path = TestImages.Write(_dir.Path, "xmp.jpg",
            TestImages.Jpeg(64, 64, xmpKeywords: ["Beach", "Family", "beach", "Cornwall"]));

        var metadata = PhotoMetadata.Read(path);

        Assert.Equal(["Beach", "Family", "Cornwall"], metadata.Keywords);
        Assert.Equal(4, metadata.Rating);
        Assert.False(metadata.IsFavourite);
        Assert.True((metadata with { Rating = 5 }).IsFavourite);
    }

    [Fact]
    public void Read_PhotoWithoutMetadata_ReturnsEmptyValues()
    {
        var path = TestImages.Write(_dir.Path, "plain.jpg", TestImages.Jpeg(64, 48));

        var metadata = PhotoMetadata.Read(path);

        Assert.Null(metadata.DateTaken);
        Assert.Null(metadata.Camera);
        Assert.Empty(metadata.Keywords);
        Assert.Equal((64, 48), (metadata.Width, metadata.Height));
    }

    [Theory]
    [InlineData("Canon", "Canon EOS R6", "Canon EOS R6")]
    [InlineData("FUJIFILM", "X-T4", "FUJIFILM X-T4")]
    [InlineData(null, "X-T4", "X-T4")]
    [InlineData("Apple", null, "Apple")]
    public void Camera_CombinesMakeAndModel(string? make, string? model, string expected) =>
        Assert.Equal(expected, new PhotoMetadata { CameraMake = make, CameraModel = model }.Camera);

    public void Dispose() => _dir.Dispose();
}
