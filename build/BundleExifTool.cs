#:property PublishAot=false

// Downloads the ExifTool pinned in build/exiftool.json, checks its SHA-256, and puts it in
// <publish dir>/exiftool, where PhotoTag looks for a bundled copy (ExifTool.Locate).
//
//   dotnet run build/BundleExifTool.cs -- <runtime id> <publish dir>
//
// Windows builds get the official standalone Windows build (it runs under emulation on ARM64).
// macOS and Linux builds get the Perl distribution, which uses the Perl those systems ship with.
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: BundleExifTool <runtime id> <publish dir>");
    return 2;
}

var (rid, publishDir) = (args[0], args[1]);
var config = JsonDocument.Parse(File.ReadAllText(Path.Combine("build", "exiftool.json"))).RootElement;
var isWindows = rid.StartsWith("win-", StringComparison.Ordinal);
var package = config.GetProperty(isWindows ? "windows" : "perl");
var file = package.GetProperty("file").GetString()!;
var expected = package.GetProperty("sha256").GetString()!;
var url = config.GetProperty("mirror").GetString()!.Replace("{file}", file);

var cache = Path.Combine(Path.GetTempPath(), "phototag-exiftool");
Directory.CreateDirectory(cache);
var archive = Path.Combine(cache, file);
if (!File.Exists(archive) || Sha256(archive) != expected)
{
    Console.WriteLine($"Downloading {url}");
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    File.WriteAllBytes(archive, await http.GetByteArrayAsync(url));
}
if (Sha256(archive) != expected)
{
    Console.Error.WriteLine($"{file}: checksum mismatch; refusing to bundle it.");
    return 1;
}

var target = Path.Combine(publishDir, "exiftool");
if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
var staging = Path.Combine(cache, "extract-" + Guid.NewGuid().ToString("N"));

if (isWindows)
{
    // exiftool-13.59_64/exiftool(-k).exe + exiftool_files/  ->  exiftool/exiftool.exe + exiftool/exiftool_files/
    ZipFile.ExtractToDirectory(archive, staging);
    var root = Directory.GetDirectories(staging).Single();
    Directory.Move(root, target);
    File.Move(Path.Combine(target, "exiftool(-k).exe"), Path.Combine(target, "exiftool.exe"));
}
else
{
    // Image-ExifTool-13.59/exiftool + lib/  ->  exiftool/exiftool + exiftool/lib/
    Directory.CreateDirectory(staging); // unlike ZipFile, TarFile needs the destination to exist
    await using (var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
        await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: true);
    var root = Directory.GetDirectories(staging).Single();
    Directory.CreateDirectory(target);
    File.Move(Path.Combine(root, "exiftool"), Path.Combine(target, "exiftool"));
    Directory.Move(Path.Combine(root, "lib"), Path.Combine(target, "lib"));
    // The script must be executable. (Only possible when bundling on macOS/Linux, as the release build does.)
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(Path.Combine(target, "exiftool"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    foreach (var name in new[] { "README", "Changes" })
        if (File.Exists(Path.Combine(root, name))) File.Move(Path.Combine(root, name), Path.Combine(target, name));
}
Directory.Delete(staging, recursive: true);

File.WriteAllText(Path.Combine(target, "ABOUT.txt"), $"""
    ExifTool {config.GetProperty("version").GetString()} by Phil Harvey, https://exiftool.org
    Bundled with PhotoTag, which runs it to read and write photo metadata.
    ExifTool is free software; you can redistribute it and/or modify it under the same terms as Perl itself
    (the Artistic License or the GNU General Public License).

    """);
Console.WriteLine($"Bundled ExifTool into {target}");
return 0;

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}
