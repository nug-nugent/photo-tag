namespace PhotoTag.Core;

/// <summary>Where PhotoTag keeps its own files: settings, the library index, thumbnails and the log.</summary>
public static class AppData
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The installed app's folder: <c>%LocalAppData%\PhotoTag-Data</c> on Windows, where the installer puts the app
    /// itself in <c>%LocalAppData%\PhotoTag</c>: sharing that folder made a fresh install think PhotoTag was already
    /// there, and uninstalling would delete the index. macOS and Linux install the app elsewhere, so there it's the
    /// usual <c>PhotoTag</c> folder.
    /// </summary>
    private static readonly string InstalledFolder =
        Path.Combine(LocalAppData, OperatingSystem.IsWindows() ? "PhotoTag-Data" : "PhotoTag");

    /// <summary>
    /// Where PhotoTag keeps its files. A Debug build (<c>dotnet run</c>) has its own, <c>PhotoTag-Dev</c>, so
    /// trying out a change never touches the installed copy's settings, index or log. The PHOTOTAG_DATA
    /// environment variable overrides either.
    /// </summary>
    public static string Folder { get; } = ChooseFolder(Environment.GetEnvironmentVariable("PHOTOTAG_DATA"), IsDevelopmentBuild);

    /// <summary>Whether this is a Debug build. Released packages are always Release builds.</summary>
    public static bool IsDevelopmentBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    internal static string ChooseFolder(string? configured, bool development) =>
        configured is { Length: > 0 } ? Path.GetFullPath(configured)
        : development ? Path.Combine(LocalAppData, "PhotoTag-Dev")
        : InstalledFolder;

    /// <summary>What PhotoTag keeps in <see cref="Folder"/>, and so what's moved from the old Windows location.</summary>
    internal static readonly string[] Contents =
        ["settings.json", "library.db", "library.db-wal", "library.db-shm", "log.txt", "log.old.txt", "thumbnails"];

    /// <summary>
    /// On Windows, moves PhotoTag's files out of the installer's folder, where versions before 0.1.1 kept them.
    /// Call before anything opens them. Never throws: returns what couldn't be moved (perhaps in use by another
    /// copy of PhotoTag), which is only a cache or settings, rebuilt or defaulted if missing.
    /// </summary>
    public static IReadOnlyList<string> MoveFromOldLocation() =>
        OperatingSystem.IsWindows() && Folder == InstalledFolder ? MoveFiles(Path.Combine(LocalAppData, "PhotoTag"), Folder) : [];

    /// <summary>Moves each of <see cref="Contents"/> from <paramref name="from"/> to <paramref name="to"/>, never replacing anything.</summary>
    internal static IReadOnlyList<string> MoveFiles(string from, string to)
    {
        var problems = new List<string>();
        foreach (var name in Contents)
        {
            var source = Path.Combine(from, name);
            var target = Path.Combine(to, name);
            try
            {
                if (File.Exists(source) && !File.Exists(target))
                {
                    Directory.CreateDirectory(to);
                    File.Move(source, target);
                }
                else if (Directory.Exists(source) && !Directory.Exists(target))
                {
                    Directory.CreateDirectory(to);
                    Directory.Move(source, target);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{source}: {e.Message}");
            }
        }
        return problems;
    }
}
