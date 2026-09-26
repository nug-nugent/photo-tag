namespace PhotoTag.Core.Tests;

public sealed class PlaceFinderTests
{
    private static async Task<PlaceFinder> Finder() => await PlaceFinder.LoadAsync();

    [Theory]
    [InlineData(50.2083, -5.4908, "St Ives", "Cornwall", "United Kingdom")]   // UK: the state is the county
    [InlineData(50.0833, -5.5389, "Mousehole", "Cornwall", "United Kingdom")] // UK villages under 500 people are included
    [InlineData(55.9486, -3.1999, "Edinburgh", "City of Edinburgh", "United Kingdom")]
    [InlineData(51.5136, -0.1365, "London", "Greater London", "United Kingdom")] // Soho: the city, not a district
    [InlineData(53.4875, -2.2901, "Salford", "City and Borough of Salford", "United Kingdom")] // not neighbouring Manchester
    [InlineData(48.8584, 2.2945, "Paris", "Île-de-France", "France")]         // the Eiffel Tower, not "Paris 16 Passy"
    [InlineData(-33.8568, 151.2153, "Sydney", "New South Wales", "Australia")]
    public async Task FindsTheTownWithItsStateAndCountry(double lat, double lon, string city, string state, string country)
    {
        var place = (await Finder()).Find(lat, lon);

        Assert.NotNull(place);
        Assert.Equal((city, state, country), (place.City, place.State, place.Country));
        Assert.InRange(place.DistanceKm, 0, 5);
    }

    [Fact]
    public async Task FarFromAnyTown_FindsNothing()
    {
        Assert.Null((await Finder()).Find(40.0, -40.0)); // mid-Atlantic
    }

    [Fact]
    public async Task LoadsOnce_WithTheWholeWorld()
    {
        var finder = await Finder();
        Assert.Same(finder, await PlaceFinder.LoadAsync());
        Assert.InRange(finder.Count, 200_000, 400_000);
    }

    [Fact]
    public void Distance_IsGreatCircle()
    {
        // London to Paris is about 344 km.
        Assert.InRange(PlaceFinder.DistanceKm(51.5074, -0.1278, 48.8566, 2.3522), 340, 348);
    }
}
