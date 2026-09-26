using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoTag.Core;
using SkiaSharp;

namespace PhotoTag.App;

/// <summary>
/// <c>PhotoTag --self-check report.json</c>: checks that a packaged build's moving parts work on
/// this machine (the bundled ExifTool, SQLite and image decoding, which rely on native code or
/// Perl), writes a JSON report, and exits 0 if everything passed. The release build runs it on
/// every package before publishing.
/// </summary>
internal static class SelfCheck
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var results = new Dictionary<string, string>();
        var passed = true;

        async Task Check(string name, Func<Task<string>> check)
        {
            try
            {
                results[name] = await check();
            }
            catch (Exception e)
            {
                results[name] = $"FAILED: {e.GetType().Name}: {e.Message}";
                passed = false;
            }
        }

        await Check("exiftool", async () =>
        {
            var setup = ExifToolSetup.Find(AppContext.BaseDirectory);
            if (setup.Status == ExifToolStatus.NotFound) throw new FileNotFoundException("No ExifTool found.");
            if (setup.Status == ExifToolStatus.PerlMissing)
                throw new FileNotFoundException($"Perl is missing: {setup.ExecutablePath} is the Perl version of ExifTool and there's no perl to run it.");
            await using var exifTool = setup.Create()!;
            var version = (await exifTool.ExecuteAsync(["-ver"])).Trim();
            var path = setup.ExecutablePath!;
            var bundled = path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
            var perl = setup.PerlPath is null ? "" : $", run by {setup.PerlPath}";
            return $"{version} at {path}{(bundled ? " (bundled)" : " (NOT bundled)")}{perl}";
        });

        await Check("sqlite", () =>
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            return Task.FromResult((string)command.ExecuteScalar()!);
        });

        await Check("images", () =>
        {
            using var bitmap = new SKBitmap(64, 48);
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
            var rendered = ImageRenderer.Render(data.ToArray(), 32);
            using var decoded = SKBitmap.Decode(rendered);
            return Task.FromResult($"decoded {decoded.Width}x{decoded.Height} with SkiaSharp");
        });

        results["runtime"] = $"{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier} / .NET {Environment.Version}";
        results["passed"] = passed.ToString();
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(results, AppJsonContext.Default.DictionaryStringString));
        return passed ? 0 : 1;
    }
}
