using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PhotoTag.App.ViewModels;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>"Look up exact place", with a stand-in for OpenStreetMap: tests never go online.</summary>
public sealed class ExactPlaceTests : UiTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeLookup(ExactPlace? answer, string? failure = null) : IExactPlaceLookup
    {
        public List<(double, double)> Asked { get; } = [];

        public Task<ExactPlace?> LookUpAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
        {
            Asked.Add((latitude, longitude));
            return failure is null ? Task.FromResult(answer) : throw new PlaceLookupException(failure);
        }
    }

    private static readonly ExactPlace Porthcurno = new("Porthcurno Beach", "St Levan", "Cornwall", "United Kingdom");

    private async Task<(Window Window, MainWindowViewModel Vm, PhotoDetailsViewModel Details, string Path)> OpenWithGpsAsync(
        ExifTool exifTool, IExactPlaceLookup? lookup, MetadataChanges? existing = null)
    {
        var path = Photo("a.jpg");
        await exifTool.ExecuteAsync(["-GPSLatitude=50.0421", "-GPSLatitudeRef=N", "-GPSLongitude=5.6543", "-GPSLongitudeRef=W",
            "-overwrite_original", path], Ct);
        var writer = new PhotoMetadataWriter(exifTool);
        if (existing is not null) await writer.WriteAsync(path, existing, Ct);
        var (window, vm) = await OpenAsync(writer, placeLookup: lookup);
        var details = await SelectSingleAsync(window, vm, 0);
        return (window, vm, details, path);
    }

    private static async Task WaitForSaveAsync(PhotoDetailsViewModel details)
    {
        var saves = details.SaveCompletion;
        await WaitForAsync(() => saves.IsCompleted);
        Assert.False(details.SaveFailed, details.SaveStatus);
    }

    [AvaloniaFact]
    public async Task LookUp_ShowsTheAnswerFirst_ThenFillsOnlyEmptyBoxes()
    {
        await using var exifTool = RequireExifTool();
        var lookup = new FakeLookup(Porthcurno);
        var (window, _, details, path) = await OpenWithGpsAsync(exifTool, lookup, new MetadataChanges { City = "St Buryan" });

        Click(window, Find<Button>(window, "LookUpPlaceButton"));
        await WaitForAsync(() => details.CanUseLookup);
        Assert.Equal("OpenStreetMap: Porthcurno Beach, St Levan, Cornwall, United Kingdom", details.LookupText);
        Assert.Equal([(50.0421, -5.6543)], lookup.Asked.Select(a => (Math.Round(a.Item1, 4), Math.Round(a.Item2, 4))));
        Assert.Null(PhotoMetadata.Read(path).Location); // nothing written until asked

        Click(window, Find<Button>(window, "LookupFillButton"));
        await WaitForSaveAsync(details);
        var m = PhotoMetadata.Read(path);
        Assert.Equal(("Porthcurno Beach", "St Buryan", "Cornwall", "United Kingdom"), (m.Location, m.City, m.State, m.Country));
        Assert.False(details.IsShowingLookup);
        Assert.Contains("© OpenStreetMap contributors", details.FillNote);
        window.Close();
    }

    [AvaloniaFact]
    public async Task UseAll_ReplacesWhatsThere()
    {
        await using var exifTool = RequireExifTool();
        var (window, _, details, path) = await OpenWithGpsAsync(exifTool, new FakeLookup(Porthcurno), new MetadataChanges { City = "St Buryan" });

        Click(window, Find<Button>(window, "LookUpPlaceButton"));
        await WaitForAsync(() => details.CanUseLookup);
        Click(window, Find<Button>(window, "LookupReplaceButton"));
        await WaitForSaveAsync(details);

        Assert.Equal("St Levan", PhotoMetadata.Read(path).City);
        Assert.Equal("St Levan", details.City);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Failures_AreExplained_AndNothingIsWritten()
    {
        await using var exifTool = RequireExifTool();
        var (window, _, details, path) = await OpenWithGpsAsync(exifTool, new FakeLookup(null, "Couldn't reach OpenStreetMap: offline"));

        Click(window, Find<Button>(window, "LookUpPlaceButton"));
        await WaitForAsync(() => !details.IsLookingUp && details.LookupText?.StartsWith("Couldn't") == true);
        Assert.False(details.CanUseLookup);
        Assert.False(Find<Button>(window, "LookupFillButton").IsEffectivelyVisible);
        Click(window, Find<Button>(window, "LookupCloseButton"));
        Assert.False(details.IsShowingLookup);
        Assert.Null(PhotoMetadata.Read(path).Location);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutALookup_TheLinkIsHidden()
    {
        await using var exifTool = RequireExifTool();
        var (window, _, details, _) = await OpenWithGpsAsync(exifTool, lookup: null);

        Assert.True(details.HasGps);
        Assert.False(Find<Button>(window, "LookUpPlaceButton").IsEffectivelyVisible);
        window.Close();
    }
}
