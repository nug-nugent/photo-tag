using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PhotoTag.Core;

/// <summary>What's at a GPS position, from an online map: the named place there (a beach, park, venue…) and its address.</summary>
public sealed record ExactPlace(string? Location, string? City, string? State, string? Country)
{
    /// <summary>The fields as <see cref="MetadataChanges"/>, for the ones that have a value.</summary>
    public MetadataChanges ToChanges(bool onlyWhereEmpty, PhotoMetadata current)
    {
        var changes = new MetadataChanges();
        foreach (var (field, value) in new[] { (TextField.Location, Location), (TextField.City, City), (TextField.State, State), (TextField.Country, Country) })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (onlyWhereEmpty && !string.IsNullOrWhiteSpace(current.Get(field))) continue;
            if (value == current.Get(field)) continue;
            changes = changes.With(field, value);
        }
        return changes;
    }

    public override string ToString() => string.Join(", ", new[] { Location, City, State, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Looks up the exact place at a GPS position, online. One request per call.</summary>
public interface IExactPlaceLookup
{
    /// <summary>The place, or null if there's nothing there (at sea, say).</summary>
    /// <exception cref="PlaceLookupException">The service couldn't be reached or refused the request.</exception>
    Task<ExactPlace?> LookUpAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}

public sealed class PlaceLookupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// OpenStreetMap's Nominatim reverse geocoder. Its usage policy
/// (https://operations.osmfoundation.org/policies/nominatim/) asks for at most one request a second,
/// an identifying User-Agent, cached results and no bulk jobs, so PhotoTag only calls it when someone
/// clicks "Look up exact place", one photo at a time. Its data is © OpenStreetMap contributors (ODbL).
/// </summary>
public sealed class NominatimLookup : IExactPlaceLookup, IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(long, long), ExactPlace?> _cache = new();
    private DateTime _lastRequest = DateTime.MinValue;

    public NominatimLookup(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoTag/1.0 (+https://github.com/nug-nugent/photo-tag)");
    }

    public async Task<ExactPlace?> LookUpAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        // About a metre apart counts as the same spot.
        var key = ((long)Math.Round(latitude * 1e5), (long)Math.Round(longitude * 1e5));
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastRequest + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={latitude:F6}&lon={longitude:F6}&zoom=18&addressdetails=1");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.AcceptLanguage.ParseAdd(CultureInfo.CurrentUICulture.Name is { Length: > 0 } language ? language : "en");
            _lastRequest = DateTime.UtcNow;

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                throw new PlaceLookupException("OpenStreetMap is busy or refused the request. Try again in a minute.");
            response.EnsureSuccessStatusCode();
            var place = Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            _cache[key] = place;
            return place;
        }
        catch (HttpRequestException e)
        {
            throw new PlaceLookupException($"Couldn't reach OpenStreetMap: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlaceLookupException("OpenStreetMap didn't answer in time.", e);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Maps a Nominatim jsonv2 reply to place fields. Location is the named feature at the spot (a beach,
    /// park, venue), else its road; the state is the county for the UK, as in <see cref="PlaceFinder"/>.
    /// </summary>
    internal static ExactPlace? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _) || !root.TryGetProperty("address", out var address)) return null;

        string? Get(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;
        string? First(params string[] names) => names.Select(n => Get(address, n)).FirstOrDefault(v => v is not null);

        var city = First("city", "town", "village", "hamlet", "municipality");
        var location = Get(root, "name") ?? First("tourism", "leisure", "natural", "amenity", "building", "road");
        if (location == city) location = null; // the spot is the town itself
        var isUk = Get(address, "country_code") == "gb";
        var state = isUk ? First("county", "state_district", "state") : First("state", "county");
        return new ExactPlace(location, city, state, First("country"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
