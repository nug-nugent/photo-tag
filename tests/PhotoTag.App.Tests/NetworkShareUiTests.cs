using Avalonia.Headless.XUnit;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>Folders on network shares, and ones that can't be reached.</summary>
public sealed class NetworkShareUiTests : UiTestBase
{
    [AvaloniaFact]
    public async Task AFolderOnAShare_Opens_IsPolled_AndTagsSave()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows' administrative shares only.");
        var full = Path.GetFullPath(DirPath);
        var share = $@"\\localhost\{full[0]}${full[2..]}";
        if (!Directory.Exists(share)) Assert.Skip($"{share} isn't reachable (the account may not be an administrator).");
        await using var exifTool = RequireExifTool();
        Photo("a.jpg", "Beach");
        Photo(Path.Combine("2020", "b.jpg"));

        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool), root: share);
        await vm.Library.ScanCompletion;

        Assert.Equal(share, vm.RootPath);
        Assert.True(vm.Library.IsPolling);
        await WaitForAsync(() => vm.RootFolders[0].CountText == "1 / 2");
        var details = await SelectSingleAsync(window, vm, 0);
        details.NewKeyword = "Sea";
        await details.AddKeywordCommand.ExecuteAsync(null);
        await details.SaveCompletion;
        Assert.False(details.SaveFailed, details.SaveStatus);
        Assert.Equal(["Beach", "Sea"], PhotoMetadata.Read(Path.Combine(share, "a.jpg")).Keywords);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AFolderThatIsntThere_SaysSo_AndKeepsWhatWasOpen()
    {
        Photo("a.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        var missing = Path.Combine(DirPath, "Gone", "Photos");

        await vm.OpenRootAsync(missing);

        Assert.Equal(DirPath, vm.RootPath);
        Assert.Equal($"Folder not found: {missing}", vm.StatusText);
        Assert.False(vm.Library.IsPolling);
        window.Close();
    }
}
