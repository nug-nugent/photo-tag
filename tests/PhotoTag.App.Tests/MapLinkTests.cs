using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PhotoTag.App.ViewModels;

namespace PhotoTag.App.Tests;

public sealed class MapLinkTests : UiTestBase
{
    [AvaloniaFact]
    public async Task PhotosWithGps_LinkToOpenStreetMap_OthersDont()
    {
        await using var exifTool = RequireExifTool();
        var withGps = Photo("a.jpg");
        Photo("b.jpg");
        await exifTool.ExecuteAsync(["-GPSLatitude=50.0421", "-GPSLatitudeRef=N", "-GPSLongitude=5.6543", "-GPSLongitudeRef=W",
            "-overwrite_original", withGps], TestContext.Current.CancellationToken);

        // A comma decimal separator mustn't leak into the address.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var (window, vm) = await OpenAsync(writer: null);
            var details = await SelectSingleAsync(window, vm, 0, waitForEditable: false);
            await WaitForAsync(() => details.IsLoaded);

            Assert.Equal("50.04210, -5.65430", details.Coordinates);
            Assert.Equal(new Uri("https://www.openstreetmap.org/?mlat=50.042100&mlon=-5.654300#map=16/50.042100/-5.654300"),
                details.MapUri);
            var link = Find<HyperlinkButton>(window, "OpenMapLink");
            Assert.True(link.IsEffectivelyVisible);
            Assert.Equal(details.MapUri, link.NavigateUri);

            var other = await SelectSingleAsync(window, vm, 1, waitForEditable: false);
            await WaitForAsync(() => other.IsLoaded);
            Assert.Null(other.MapUri);
            Assert.False(Find<HyperlinkButton>(window, "OpenMapLink").IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
