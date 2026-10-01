using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;

namespace PhotoTag.App.Tests;

public sealed class SubfolderTests : UiTestBase
{
    [AvaloniaFact]
    public async Task AFolderWithNoPhotosOfItsOwn_ShowsItsSubfolders()
    {
        Photo(@"2008/Wedding/b.jpg", new DateTime(2008, 4, 12, 14, 0, 0));
        Photo(@"2008/Wedding/a.jpg", new DateTime(2008, 4, 12, 15, 0, 0));
        Photo(@"2008/Skiing/Day 1/c.jpg", new DateTime(2008, 2, 3, 10, 0, 0));
        Photo(@"2009/d.jpg", new DateTime(2009, 1, 1, 9, 0, 0));
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        await vm.SummariesLoading;

        // The library's top folder has none of its own either: everything, in the order of the folder tree.
        Assert.True(vm.ShowsSubfolders);
        Assert.False(vm.IncludeSubfolders);
        Assert.Equal(["c.jpg", "a.jpg", "b.jpg", "d.jpg"], vm.Photos.Select(p => p.FileName));

        var root = vm.RootFolders.Single();
        await root.ChildrenLoading;
        vm.SelectedFolder = root.Children.Single(c => c.Name == "2008");
        await vm.PhotosLoading;
        await vm.SummariesLoading;
        Assert.Equal(["c.jpg", "a.jpg", "b.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal("0 of 3 tagged · 0 favourites · from 2 folders", vm.Subheading);
        Assert.StartsWith("3 photos in 2008 and its subfolders", vm.StatusText);

        // By day works across them: newest first, a heading per day.
        vm.Sort = PhotoSort.Days;
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal(2, vm.Days.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task IncludeSubfolders_AddsThemToAFolderWithPhotos_AndIsRemembered()
    {
        Photo("2008/top.jpg");
        Photo("2008/Wedding/a.jpg");
        Photo("2008/.hidden/x.jpg"); // made hidden below on Windows; a dot is enough elsewhere
        if (OperatingSystem.IsWindows())
            File.SetAttributes(Path.Combine(DirPath, "2008", ".hidden"), FileAttributes.Directory | FileAttributes.Hidden);
        var (window, vm) = await OpenAsync(writer: null);
        var root = vm.RootFolders.Single();
        await root.ChildrenLoading;
        vm.SelectedFolder = root.Children.Single();
        await vm.PhotosLoading;
        await vm.SummariesLoading;
        Assert.Equal(["top.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.False(vm.ShowsSubfolders);
        Assert.Equal("0 of 1 tagged · 0 favourites", vm.Subheading);

        Click(window, Find<ToggleButton>(window, "IncludeSubfoldersButton"));
        Assert.True(vm.IncludeSubfolders);
        await vm.PhotosLoading;
        await vm.SummariesLoading;
        Assert.True(vm.ShowsSubfolders);
        Assert.Equal(["top.jpg", "a.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.Equal("0 of 2 tagged · 0 favourites · from 2 folders", vm.Subheading);
        window.Close();

        // A new window starts with it on.
        var (again, vm2) = await OpenAsync(writer: null);
        Assert.True(vm2.IncludeSubfolders);
        Assert.Equal(["top.jpg", "a.jpg"], vm2.Photos.Select(p => p.FileName));
        again.Close();
    }

    [AvaloniaFact]
    public async Task PhotosAddedToASubfolder_AppearWhileItsParentIsShown()
    {
        Photo("2008/Wedding/a.jpg");
        var (window, vm) = await OpenAsync(writer: null);
        await vm.Library.ScanCompletion;
        Assert.True(vm.ShowsSubfolders);

        Photo("2008/Wedding/b.jpg"); // two folders down
        await WaitForAsync(() => vm.Photos.Count == 2);
        await vm.FilesChangedHandling;
        Photo("2008/Skiing/c.jpg"); // in a new folder
        await WaitForAsync(() => vm.Photos.Count == 3);
        await vm.FilesChangedHandling;
        Assert.Equal(["c.jpg", "a.jpg", "b.jpg"], vm.Photos.Select(p => p.FileName));

        // One of its own: now it shows just that.
        Photo("top.jpg");
        await WaitForAsync(() => vm.Photos.Count == 1);
        await vm.FilesChangedHandling;
        Assert.Equal(["top.jpg"], vm.Photos.Select(p => p.FileName));
        Assert.False(vm.ShowsSubfolders);
        window.Close();
    }
}
