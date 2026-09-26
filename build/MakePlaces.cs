#:property PublishAot=false

// Builds the offline place data that "Fill from GPS" uses, from GeoNames (geonames.org, CC BY 4.0):
// every populated place with 500+ people, plus every village and hamlet in the countries listed in
// `detailed`, each with its population, state/province (the county, for the UK) and country.
// Run from the repo root:  dotnet run build/MakePlaces.cs
//
// The output is committed, so builds never depend on geonames.org. Re-run it to refresh the data.
//
// Format (Brotli-compressed, little-endian): "PTPL", version, then a string table and one record per
// place: latitude and longitude in 1e-5 degrees, indexes of its name, state (or -1) and country, and population.
using System.IO.Compression;
using System.Text;

const string Source = "https://download.geonames.org/export/dump/";
var output = Path.Combine("src", "PhotoTag.Core", "Places", "places.bin");

// Countries where even the smallest places are included (the owner's photos are mostly here).
string[] detailed = ["GB"];

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoTag-build (https://github.com/nug-nugent/photo-tag)");

async Task<string[]> Lines(string file)
{
    Console.WriteLine($"Downloading {file}");
    var bytes = await http.GetByteArrayAsync(Source + file);
    if (file.EndsWith(".zip", StringComparison.Ordinal))
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var reader = new StreamReader(zip.Entries.Single(e => e.Name == Path.ChangeExtension(file, ".txt")).Open());
        return (await reader.ReadToEndAsync()).Split('\n');
    }
    return Encoding.UTF8.GetString(bytes).Split('\n');
}

static IEnumerable<string[]> Rows(string[] lines) =>
    lines.Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.TrimEnd('\r').Split('\t'));

var countries = Rows(await Lines("countryInfo.txt")).ToDictionary(r => r[0], r => r[4]);
var admin1 = Rows(await Lines("admin1CodesASCII.txt")).ToDictionary(r => r[0], r => r[1]);
var admin2 = Rows(await Lines("admin2Codes.txt")).ToDictionary(r => r[0], r => r[1]);

// Sections of towns (PPLX) would make "City" a suburb; historical, abandoned and destroyed places are gone.
string[] skip = ["PPLX", "PPLH", "PPLQ", "PPLW", "PPLCH"];

var strings = new List<string>();
var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
int Intern(string s)
{
    if (stringIndex.TryGetValue(s, out var i)) return i;
    stringIndex[s] = strings.Count;
    strings.Add(s);
    return strings.Count - 1;
}

var places = new List<(int Lat, int Lon, int Name, int State, int Country, int Population)>();
var seen = new HashSet<string>(StringComparer.Ordinal); // GeoNames ids: detailed countries repeat cities500's places
var rows = Rows(await Lines("cities500.zip"));
foreach (var code in detailed) rows = rows.Concat(Rows(await Lines(code + ".zip")));
foreach (var r in rows)
{
    if (r[6] != "P" || skip.Contains(r[7]) || !countries.TryGetValue(r[8], out var country) || !seen.Add(r[0])) continue;
    // The UK's first level is England/Scotland/Wales/Northern Ireland; its counties are the second.
    var state = r[8] == "GB" && admin2.TryGetValue($"{r[8]}.{r[10]}.{r[11]}", out var county) ? county
        : admin1.TryGetValue($"{r[8]}.{r[10]}", out var region) ? region
        : null;
    places.Add(((int)Math.Round(double.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture) * 1e5),
        (int)Math.Round(double.Parse(r[5], System.Globalization.CultureInfo.InvariantCulture) * 1e5),
        Intern(r[1]), state is null ? -1 : Intern(state), Intern(country),
        int.TryParse(r[14], out var population) ? population : 0));
}
places.Sort((a, b) => a.Lat != b.Lat ? a.Lat.CompareTo(b.Lat) : a.Lon.CompareTo(b.Lon)); // compresses better

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var file = File.Create(output))
using (var brotli = new BrotliStream(file, CompressionLevel.SmallestSize))
using (var writer = new BinaryWriter(brotli, Encoding.UTF8))
{
    writer.Write("PTPL"u8);
    writer.Write(1);
    writer.Write7BitEncodedInt(strings.Count);
    foreach (var s in strings) writer.Write(s);
    writer.Write7BitEncodedInt(places.Count);
    var previousLat = 0;
    foreach (var p in places)
    {
        writer.Write7BitEncodedInt(p.Lat - previousLat); // sorted, so small and positive
        writer.Write(p.Lon);
        writer.Write7BitEncodedInt(p.Name);
        writer.Write7BitEncodedInt(p.State + 1);
        writer.Write7BitEncodedInt(p.Country);
        writer.Write7BitEncodedInt(p.Population);
        previousLat = p.Lat;
    }
}
Console.WriteLine($"Wrote {places.Count:N0} places ({new FileInfo(output).Length / 1024.0 / 1024:F1} MB) to {output}");
