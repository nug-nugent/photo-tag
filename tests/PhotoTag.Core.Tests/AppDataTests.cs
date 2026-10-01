namespace PhotoTag.Core.Tests;

/// <summary>Moving PhotoTag's own files out of the Windows installer's folder, where versions before 0.1.1 kept them.</summary>
public sealed class AppDataTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string Old => Path.Combine(_dir.Path, "PhotoTag");
    private string New => Path.Combine(_dir.Path, "PhotoTag Data");

    private string Write(string folder, string relativePath, string text = "x")
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void PhotoTagsFiles_Move_AndTheInstallersStay()
    {
        foreach (var name in new[] { "settings.json", "library.db", "library.db-wal", "log.txt", "log.old.txt", Path.Combine("thumbnails", "ab", "abc.thumb") })
            Write(Old, name, name);
        var installer = new[] { Write(Old, "Update.exe"), Write(Old, Path.Combine("current", "PhotoTag.exe")), Write(Old, Path.Combine("packages", "x.nupkg")) };

        Assert.Empty(AppData.MoveFiles(Old, New));

        Assert.Equal("library.db", File.ReadAllText(Path.Combine(New, "library.db")));
        Assert.Equal(Path.Combine("thumbnails", "ab", "abc.thumb"), File.ReadAllText(Path.Combine(New, "thumbnails", "ab", "abc.thumb")));
        // Only the installer's own files are left behind.
        Assert.Equal(["Update.exe", "current", "packages"],
            Directory.EnumerateFileSystemEntries(Old).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(installer, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void NothingAlreadyThere_IsReplaced()
    {
        Write(Old, "settings.json", "old");
        Write(Old, "library.db", "old index");
        Write(New, "settings.json", "new");

        Assert.Empty(AppData.MoveFiles(Old, New));

        Assert.Equal("new", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Old, "settings.json")));
        Assert.Equal("old index", File.ReadAllText(Path.Combine(New, "library.db")));
    }

    [Fact]
    public void WithNothingToMove_NoFolderIsMade()
    {
        Assert.Empty(AppData.MoveFiles(Old, New));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void AFileInUse_IsReported_AndTheRestStillMove()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Only Windows stops an open file being moved.");
        var index = Write(Old, "library.db");
        Write(Old, "settings.json");

        using (new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var problem = Assert.Single(AppData.MoveFiles(Old, New));
            Assert.StartsWith(index, problem);
        }

        Assert.True(File.Exists(Path.Combine(New, "settings.json")));
    }

    public void Dispose() => _dir.Dispose();
}
