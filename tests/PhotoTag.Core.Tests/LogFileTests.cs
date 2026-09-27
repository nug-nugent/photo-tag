namespace PhotoTag.Core.Tests;

public sealed class LogFileTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // In a folder that doesn't exist yet, as on first launch.
    private string LogPath => Path.Combine(_dir.Path, "PhotoTag", "log.txt");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task WritesEachMessage_WithTimeAndLevel()
    {
        await using var log = new LogFile(LogPath);
        log.Write(LogLevel.Info, @"Opening C:\Photos");
        log.Write(LogLevel.Warn, "Couldn't save a.jpg", new IOException("The file is locked."));
        log.Write(LogLevel.Error, "Something broke", Thrown());
        log.Write(LogLevel.Warn, "ExifTool said:\nError: one\r\nError: two\n");
        await log.FlushAsync();

        var lines = File.ReadAllLines(LogPath);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} INFO  Opening C:\\Photos$", lines[0]);
        Assert.EndsWith(" WARN  Couldn't save a.jpg: IOException: The file is locked.", lines[1]);

        // Unexpected errors come with their stack trace, indented under the message.
        Assert.EndsWith(" ERROR Something broke", lines[2]);
        Assert.Equal("    System.InvalidOperationException: Broken", lines[3]);
        Assert.StartsWith("       at ", lines[4]);
        Assert.Contains(nameof(Thrown), lines[4]);

        var exifTool = Array.FindIndex(lines, l => l.EndsWith(" WARN  ExifTool said:", StringComparison.Ordinal));
        Assert.Equal(["    Error: one", "    Error: two"], lines[(exifTool + 1)..]);
    }

    [Fact]
    public async Task RollsOver_KeepingOneOldLog()
    {
        await using var log = new LogFile(LogPath, maxSize: 1000);
        for (var i = 0; i < 100; i++)
        {
            log.Write(LogLevel.Info, $"Message {i:000} {new string('x', 40)}");
            await log.FlushAsync();
        }

        Assert.Equal(["log.old.txt", "log.txt"], Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Select(Path.GetFileName).Order());
        Assert.InRange(new FileInfo(LogPath).Length, 1, 1000);
        Assert.InRange(new FileInfo(log.OldPath).Length, 1, 1000);

        // Between them, the most recent messages, none missing.
        var numbers = File.ReadAllLines(log.OldPath).Concat(File.ReadAllLines(LogPath))
            .Select(l => int.Parse(l.Split("Message ")[1][..3], System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal(Enumerable.Range(100 - numbers.Count, numbers.Count), numbers);
    }

    [Fact]
    public async Task Disposing_WritesEverythingQueued()
    {
        var log = new LogFile(LogPath);
        for (var i = 0; i < 1000; i++) log.Write(LogLevel.Info, $"Message {i}");
        await log.DisposeAsync();
        Assert.Equal(1000, File.ReadAllLines(LogPath).Length);

        log.Write(LogLevel.Info, "Too late"); // ignored, not thrown
        await log.FlushAsync();
        Assert.Equal(1000, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public async Task ALogThatCantBeWritten_IsIgnored()
    {
        Directory.CreateDirectory(LogPath); // a folder where the file should be
        await using var log = new LogFile(LogPath);
        log.Write(LogLevel.Warn, "Nowhere to go");
        await log.FlushAsync();
        Assert.True(Directory.Exists(LogPath));
    }

    [Fact]
    public async Task Log_WritesToTheStartedFile_AndNothingOnceStopped()
    {
        await using var log = new LogFile(LogPath);
        var broken = Path.Combine(_dir.Path, "Photos", "broken.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllBytes(broken, [0xFF, 0xD8, 1, 2, 3]);
        using var index = new LibraryIndex(Path.Combine(_dir.Path, "library.db"));

        Log.Start(log);
        try
        {
            await index.ScanAsync(Path.GetDirectoryName(broken)!, cancellationToken: Ct);
        }
        finally
        {
            Log.Stop();
        }
        Log.Warn("After stopping");
        await log.FlushAsync();

        var text = File.ReadAllText(LogPath);
        Assert.Contains($"Couldn't read {broken}; indexed as untagged", text);
        Assert.DoesNotContain("After stopping", text);
    }

    private static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("Broken");
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }
}
