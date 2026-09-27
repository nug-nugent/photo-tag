using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PhotoTag.Core;
using PhotoTag.Core.Tests;

namespace PhotoTag.App.Tests;

/// <summary>Opening photos PhotoTag indexed at another path: moved to a NAS, or the same share reached another way.</summary>
public sealed class MovedLibraryTests : UiTestBase
{
    private readonly TempDir _nas = new();

    /// <summary>Copies the library to the "NAS" as a file copy would: same files, same modified times.</summary>
    private string CopyToNas()
    {
        foreach (var file in Directory.EnumerateFiles(DirPath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_nas.Path, Path.GetRelativePath(DirPath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
        return _nas.Path;
    }

    private async Task<(Window Window, ViewModels.MainWindowViewModel Vm, Border Notice)> OpenCopyAsync()
    {
        Photo("a.jpg", "Beach");
        Photo(Path.Combine("2020", "b.jpg"), "Dog");
        Photo(Path.Combine("2020", "c.jpg"));
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;

        vm.OpenRoot(CopyToNas());
        var notice = Find<Border>(window, "MovedNotice");
        await WaitForAsync(() => notice.IsEffectivelyVisible);
        return (window, vm, notice);
    }

    [AvaloniaFact]
    public async Task Accepting_MovesTheIndex_WithItsTags()
    {
        var (window, vm, notice) = await OpenCopyAsync();
        Assert.Contains("3 photos", vm.Library.MovedText);
        Assert.Contains(DirPath, vm.Library.MovedText);

        Click(window, Find<Button>(window, "AcceptMoveButton"));
        await vm.Library.ScanCompletion;

        Assert.False(notice.IsEffectivelyVisible);
        Assert.Equal(new FolderCounts(3, 2), await vm.Library.Index.GetFolderCountsAsync(_nas.Path));
        Assert.Equal(new FolderCounts(0, 0), await vm.Library.Index.GetFolderCountsAsync(DirPath));
        await WaitForAsync(() => vm.RootFolders[0].CountText == "2 / 3");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Declining_IndexesThemAfresh_AndLeavesTheOldEntries()
    {
        var (window, vm, notice) = await OpenCopyAsync();

        Click(window, Find<Button>(window, "DeclineMoveButton"));
        await vm.Library.ScanCompletion;

        Assert.False(notice.IsEffectivelyVisible);
        Assert.Equal(new FolderCounts(3, 2), await vm.Library.Index.GetFolderCountsAsync(_nas.Path));
        Assert.Equal(new FolderCounts(3, 2), await vm.Library.Index.GetFolderCountsAsync(DirPath));
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningAnotherFolder_WhileAsking_DropsTheQuestion()
    {
        var (window, vm, notice) = await OpenCopyAsync();

        vm.OpenRoot(DirPath);
        await vm.Library.ScanCompletion;

        Assert.False(notice.IsEffectivelyVisible);
        Assert.Equal(new FolderCounts(0, 0), await vm.Library.Index.GetFolderCountsAsync(_nas.Path));
        window.Close();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _nas.Dispose();
    }
}
