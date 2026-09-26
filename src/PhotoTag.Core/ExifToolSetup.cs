namespace PhotoTag.Core;

public enum ExifToolStatus
{
    Ready,
    NotFound,

    /// <summary>ExifTool is the Perl version (as bundled on macOS and Linux) and there's no Perl to run it.</summary>
    PerlMissing,
}

/// <summary>
/// What <see cref="Find"/> found: ExifTool and, when it's the Perl script rather than the Windows
/// executable, the Perl to run it with.
/// </summary>
public sealed record ExifToolSetup(string? ExecutablePath, bool NeedsPerl, string? PerlPath)
{
    /// <summary>
    /// Where Perl usually is. GUI apps on macOS don't get the shell's PATH, so Homebrew's and
    /// MacPorts' Perls are listed as well as PATH, in case Apple ever stops shipping /usr/bin/perl.
    /// </summary>
    private static readonly string[] UsualPerlDirectories = ["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin", "/opt/local/bin"];

    public ExifToolStatus Status =>
        ExecutablePath is null ? ExifToolStatus.NotFound :
        NeedsPerl && PerlPath is null ? ExifToolStatus.PerlMissing :
        ExifToolStatus.Ready;

    /// <summary>Finds ExifTool as <see cref="ExifTool.Locate"/> does, and Perl if it needs it.</summary>
    public static ExifToolSetup Find(string? appDirectory = null)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] names = OperatingSystem.IsWindows() ? ["perl.exe"] : ["perl"];
        var perlCandidates = UsualPerlDirectories.Take(1).Concat(pathDirs).Concat(UsualPerlDirectories.Skip(1))
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name)));
        return Find(ExifTool.Locate(appDirectory), perlCandidates);
    }

    internal static ExifToolSetup Find(string? exifToolPath, IEnumerable<string> perlCandidates)
    {
        if (exifToolPath is null) return new ExifToolSetup(null, false, null);
        if (!IsPerlScript(exifToolPath)) return new ExifToolSetup(exifToolPath, false, null);
        return new ExifToolSetup(exifToolPath, true, perlCandidates.FirstOrDefault(File.Exists));
    }

    /// <summary>Starts ExifTool (lazily, on its first command), or returns null if it can't run here.</summary>
    public ExifTool? Create() => Status == ExifToolStatus.Ready ? new ExifTool(ExecutablePath!, PerlPath) : null;

    /// <summary>True for a script whose first line is <c>#!…perl…</c>, like the ExifTool distribution's <c>exiftool</c>.</summary>
    private static bool IsPerlScript(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[256];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var firstLine = new string(buffer, 0, read).Split('\n')[0];
            return firstLine.StartsWith("#!", StringComparison.Ordinal) && firstLine.Contains("perl", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable: let starting it report the problem.
            return false;
        }
    }
}
