using System.IO.Compression;
using System.Text;

namespace PhotoTag.Core;

/// <summary>The nearest town or village to a GPS position, with its state/province and country.</summary>
public sealed record FoundPlace(string City, string? State, string Country, double DistanceKm);

/// <summary>
/// Turns GPS positions into places, offline, from GeoNames data bundled with PhotoTag (every
/// populated place with 500+ people, and every village and hamlet in the UK; see build/MakePlaces.cs).
/// For the UK, the state is the county.
/// </summary>
/// <remarks>
/// Each place counts as covering a circle that grows with its population (about 15 km for London,
/// 1 km for a village). A position inside one or more circles gets the place it's most central to,
/// so the Eiffel Tower is in Paris rather than a district of it, while Salford stays Salford next to
/// Manchester. Outside every circle it gets the nearest place, so near a boundary it can pick the
/// neighbouring village.
/// </remarks>
public sealed class PlaceFinder
{
    /// <summary>Further than this from any town or village (at sea, say), nothing is found.</summary>
    public const double MaxDistanceKm = 30;

    private const double CellDegrees = 0.5;
    private const double MinRadiusKm = 1;
    private const double KmPerSqrtPerson = 0.005;
    private const double EarthRadiusKm = 6371;
    private static readonly Lazy<Task<PlaceFinder>> Bundled = new(() => Task.Run(Load));

    private readonly string[] _strings;
    private readonly float[] _latitudes;
    private readonly float[] _longitudes;
    private readonly int[] _names;
    private readonly int[] _states;
    private readonly int[] _countries;
    private readonly float[] _radiiKm;
    private readonly Dictionary<(int, int), List<int>> _cells = [];

    private PlaceFinder(string[] strings, float[] latitudes, float[] longitudes, int[] names, int[] states, int[] countries,
        int[] populations)
    {
        (_strings, _latitudes, _longitudes, _names, _states, _countries) = (strings, latitudes, longitudes, names, states, countries);
        _radiiKm = new float[latitudes.Length];
        for (var i = 0; i < latitudes.Length; i++)
        {
            _radiiKm[i] = (float)Math.Max(MinRadiusKm, KmPerSqrtPerson * Math.Sqrt(populations[i]));
            var cell = Cell(latitudes[i], longitudes[i]);
            if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = [];
            list.Add(i);
        }
    }

    public int Count => _latitudes.Length;

    /// <summary>The bundled data, loaded once in the background (about a quarter of a second).</summary>
    public static Task<PlaceFinder> LoadAsync() => Bundled.Value;

    public FoundPlace? Find(double latitude, double longitude)
    {
        // Half a degree of latitude is 55 km, so the cells around this one cover everything within 30 km
        // (except near the poles, where cells narrow and there are few towns to miss).
        var (row, column) = Cell(latitude, longitude);
        int nearest = -1, central = -1;
        double nearestKm = double.MaxValue, centralKm = 0, centrality = double.MaxValue;
        for (var dr = -1; dr <= 1; dr++)
        for (var dc = -1; dc <= 1; dc++)
        {
            if (!_cells.TryGetValue((row + dr, column + dc), out var list)) continue;
            foreach (var i in list)
            {
                var km = DistanceKm(latitude, longitude, _latitudes[i], _longitudes[i]);
                if (km < nearestKm) (nearest, nearestKm) = (i, km);
                var fraction = km / _radiiKm[i]; // under 1 inside the place's circle
                if (fraction <= 1 && fraction < centrality) (central, centralKm, centrality) = (i, km, fraction);
            }
        }
        var (best, bestKm) = central >= 0 ? (central, centralKm) : (nearest, nearestKm);
        if (best < 0 || bestKm > MaxDistanceKm) return null;
        return new FoundPlace(_strings[_names[best]], _states[best] < 0 ? null : _strings[_states[best]],
            _strings[_countries[best]], bestKm);
    }

    private static (int, int) Cell(double latitude, double longitude) =>
        ((int)Math.Floor(latitude / CellDegrees), (int)Math.Floor(longitude / CellDegrees));

    /// <summary>Great-circle distance (haversine).</summary>
    internal static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static PlaceFinder Load()
    {
        using var resource = typeof(PlaceFinder).Assembly.GetManifestResourceStream("PhotoTag.Core.Places.places.bin")
                             ?? throw new InvalidOperationException("The bundled place data is missing.");
        using var brotli = new BrotliStream(resource, CompressionMode.Decompress);
        using var reader = new BinaryReader(brotli, Encoding.UTF8);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("PTPL"u8) || reader.ReadInt32() != 1)
            throw new InvalidDataException("The bundled place data isn't in a format this version understands.");

        var strings = new string[reader.Read7BitEncodedInt()];
        for (var i = 0; i < strings.Length; i++) strings[i] = reader.ReadString();

        var count = reader.Read7BitEncodedInt();
        var (latitudes, longitudes) = (new float[count], new float[count]);
        var (names, states, countries, populations) = (new int[count], new int[count], new int[count], new int[count]);
        var lat = 0;
        for (var i = 0; i < count; i++)
        {
            lat += reader.Read7BitEncodedInt();
            latitudes[i] = lat / 1e5f;
            longitudes[i] = reader.ReadInt32() / 1e5f;
            names[i] = reader.Read7BitEncodedInt();
            states[i] = reader.Read7BitEncodedInt() - 1;
            countries[i] = reader.Read7BitEncodedInt();
            populations[i] = reader.Read7BitEncodedInt();
        }
        return new PlaceFinder(strings, latitudes, longitudes, names, states, countries, populations);
    }
}
