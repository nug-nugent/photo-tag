namespace PhotoTag.Core.Tests;

public sealed class ExifToolSetupTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_dir.Path, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void NoExifTool_IsNotFound()
    {
        var setup = ExifToolSetup.Find(null, [WriteFile("perl", "")]);

        Assert.Equal(ExifToolStatus.NotFound, setup.Status);
        Assert.Null(setup.Create());
    }

    [Fact]
    public void AnExecutable_RunsWithoutPerl()
    {
        var exe = WriteFile("exiftool.exe", "MZ\0\0 the Windows build");

        var setup = ExifToolSetup.Find(exe, [WriteFile("perl", "")]);

        Assert.Equal(ExifToolStatus.Ready, setup.Status);
        Assert.False(setup.NeedsPerl);
        Assert.Null(setup.PerlPath);
    }

    [Theory]
    [InlineData("#!/usr/bin/perl -w\n")]
    [InlineData("#!/usr/bin/env perl\r\n")]
    public void ThePerlScript_RunsWithTheFirstPerlFound(string shebang)
    {
        var script = WriteFile("exiftool", shebang + "use strict;\n");
        var perl = WriteFile("perl", "");

        var setup = ExifToolSetup.Find(script, [Path.Combine(_dir.Path, "missing", "perl"), perl]);

        Assert.Equal(ExifToolStatus.Ready, setup.Status);
        Assert.True(setup.NeedsPerl);
        Assert.Equal(perl, setup.PerlPath);
        Assert.Equal(perl, setup.Create()?.PerlPath);
    }

    [Fact]
    public void ThePerlScript_WithoutPerl_SaysPerlIsMissing()
    {
        var script = WriteFile("exiftool", "#!/usr/bin/perl -w\nuse strict;\n");

        var setup = ExifToolSetup.Find(script, [Path.Combine(_dir.Path, "missing", "perl")]);

        Assert.Equal(ExifToolStatus.PerlMissing, setup.Status);
        Assert.Null(setup.Create());
    }

    [Fact]
    public async Task OnMacAndLinux_TheInstalledExifToolRunsThroughPerl()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Windows' ExifTool is an executable.");
        var setup = ExifToolSetup.Find();
        if (setup.Status == ExifToolStatus.NotFound) Assert.Skip("ExifTool isn't installed.");

        Assert.Equal(ExifToolStatus.Ready, setup.Status);
        Assert.True(setup.NeedsPerl);
        await using var exifTool = setup.Create()!;
        Assert.Matches(@"^\d+\.\d+", (await exifTool.ExecuteAsync(["-ver"], TestContext.Current.CancellationToken)).Trim());
    }

    public void Dispose() => _dir.Dispose();
}
