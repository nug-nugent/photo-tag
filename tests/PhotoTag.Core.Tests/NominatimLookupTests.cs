using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PhotoTag.Core.Tests;

/// <summary>Never goes online: replies are canned, in Nominatim's jsonv2 shape.</summary>
public sealed class NominatimLookupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Porthcurno = """
        {"place_id":1,"lat":"50.0421","lon":"-5.6543","category":"natural","type":"beach","name":"Porthcurno Beach",
         "display_name":"Porthcurno Beach, Porthcurno, St Levan, Cornwall, England, TR19 6JX, United Kingdom",
         "address":{"natural":"Porthcurno Beach","hamlet":"Porthcurno","village":"St Levan","county":"Cornwall",
                    "state":"England","postcode":"TR19 6JX","country":"United Kingdom","country_code":"gb"}}
        """;

    private const string EiffelTower = """
        {"place_id":2,"category":"tourism","type":"attraction","name":"Tour Eiffel",
         "address":{"tourism":"Tour Eiffel","road":"Avenue Gustave Eiffel","suburb":"Paris 7e Arrondissement","city":"Paris",
                    "county":"Paris","state":"Île-de-France","country":"France","country_code":"fr"}}
        """;

    private const string VillageStreet = """
        {"place_id":3,"category":"highway","type":"residential","name":"",
         "address":{"road":"Fore Street","town":"St Ives","county":"Cornwall","state":"England","country":"United Kingdom","country_code":"gb"}}
        """;

    [Fact]
    public void Parse_TakesTheNamedPlace_TheTown_TheCountyForTheUk_AndTheCountry()
    {
        Assert.Equal(new ExactPlace("Porthcurno Beach", "St Levan", "Cornwall", "United Kingdom"), NominatimLookup.Parse(Porthcurno));
        Assert.Equal(new ExactPlace("Tour Eiffel", "Paris", "Île-de-France", "France"), NominatimLookup.Parse(EiffelTower));
        Assert.Equal(new ExactPlace("Fore Street", "St Ives", "Cornwall", "United Kingdom"), NominatimLookup.Parse(VillageStreet));
        Assert.Null(NominatimLookup.Parse("""{"error":"Unable to geocode"}"""));
    }

    [Fact]
    public void ToChanges_FillsOnlyEmptyFields_OrReplacesWhenAsked()
    {
        var place = NominatimLookup.Parse(Porthcurno)!;
        var current = new PhotoMetadata { City = "St Buryan", State = "Cornwall" };

        var fill = place.ToChanges(onlyWhereEmpty: true, current);
        Assert.Equal(("Porthcurno Beach", null, null, "United Kingdom"), (fill.Location, fill.City, fill.State, fill.Country));

        var replace = place.ToChanges(onlyWhereEmpty: false, current);
        Assert.Equal(("Porthcurno Beach", "St Levan", null, "United Kingdom"), (replace.Location, replace.City, replace.State, replace.Country));
    }

    [Fact]
    public async Task Requests_AreIdentified_UseDotDecimals_AreCached_AndSpacedOut()
    {
        var handler = new CannedHandler(Porthcurno);
        using var lookup = new NominatimLookup(handler);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var first = await lookup.LookUpAsync(50.0421, -5.6543, Ct);
            var again = await lookup.LookUpAsync(50.042100001, -5.6543, Ct); // same spot: from the cache
            var stopwatch = Stopwatch.StartNew();
            await lookup.LookUpAsync(51.5, -0.12, Ct);

            Assert.Equal("Porthcurno Beach", first?.Location);
            Assert.Same(first, again);
            Assert.Equal(2, handler.Requests.Count);
            Assert.InRange(stopwatch.ElapsedMilliseconds, 900, 5000); // at most one request a second
            var request = handler.Requests[0];
            Assert.StartsWith("https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat=50.042100&lon=-5.654300", request.RequestUri);
            Assert.StartsWith("PhotoTag/", request.UserAgent);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task BusyOrUnreachable_IsAPlaceLookupException()
    {
        using var busy = new NominatimLookup(new CannedHandler("", HttpStatusCode.TooManyRequests));
        var e = await Assert.ThrowsAsync<PlaceLookupException>(() => busy.LookUpAsync(50, -5, Ct));
        Assert.Contains("busy", e.Message);

        using var offline = new NominatimLookup(new CannedHandler(null));
        e = await Assert.ThrowsAsync<PlaceLookupException>(() => offline.LookUpAsync(50, -5, Ct));
        Assert.StartsWith("Couldn't reach OpenStreetMap", e.Message);
    }

    /// <summary>Answers every request with <paramref name="body"/>, or fails like a missing network when it's null.</summary>
    private sealed class CannedHandler(string? body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string RequestUri, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), string.Join(" ", request.Headers.UserAgent)));
            if (body is null) throw new HttpRequestException("No such host is known.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
