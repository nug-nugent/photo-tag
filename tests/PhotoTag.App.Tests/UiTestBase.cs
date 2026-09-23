using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;
using PhotoTag.App.Views;
using PhotoTag.Core;
using PhotoTag.Core.Tests;

namespace PhotoTag.App.Tests;

/// <summary>Helpers for driving the real main window headlessly.</summary>
public abstract class UiTestBase : IDisposable
{
    private readonly TempDir _dir = new();

    protected string DirPath => _dir.Path;

    /// <summary>Ctrl, or ⌘ where the platform uses it, as a raw input modifier.</summary>
    protected static RawInputModifiers CommandKey =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers == KeyModifiers.Meta
            ? RawInputModifiers.Meta
            : RawInputModifiers.Control;

    protected string Photo(string name, params string[] keywords) =>
        TestImages.Write(DirPath, name, TestImages.Jpeg(120, 90, xmpKeywords: keywords.Length > 0 ? keywords : null));

    protected static ExifTool RequireExifTool()
    {
        var path = ExifTool.Locate();
        if (path is null)
        {
            if (Environment.GetEnvironmentVariable("PHOTOTAG_REQUIRE_EXIFTOOL") == "1")
                Assert.Fail("ExifTool is required (PHOTOTAG_REQUIRE_EXIFTOOL=1) but wasn't found.");
            Assert.Skip("ExifTool isn't installed.");
        }
        return new ExifTool(path);
    }

    protected async Task<(MainWindow Window, MainWindowViewModel Vm)> OpenAsync(PhotoMetadataWriter? writer)
    {
        var settings = AppSettings.Load(Path.Combine(DirPath, "settings.json"));
        var thumbnails = new ThumbnailCache(Path.Combine(DirPath, ".cache"));
        var vm = new MainWindowViewModel(thumbnails, settings, writer);
        // Tall enough that the whole details panel and all test tiles are on screen.
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 2400 };
        window.Show();
        vm.OpenRoot(DirPath);
        await WaitForAsync(() => vm.Photos.Count > 0);
        await WaitForAsync(() => Tiles(window).Count >= Math.Min(vm.Photos.Count, 12));
        return (window, vm);
    }

    /// <summary>The on-screen tile for each photo, in folder order.</summary>
    protected static IReadOnlyList<Border> Tiles(Window window) =>
        [.. FindAll<Border>(window).Where(b => b.Classes.Contains("tile"))
            .OrderBy(b => ((PhotoItemViewModel)b.DataContext!).Index)];

    protected static void ClickTile(Window window, int index, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Click(window, Tiles(window)[index], modifiers);

    protected static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    protected static async Task<PhotoDetailsViewModel> SelectSingleAsync(Window window, MainWindowViewModel vm, int index,
        bool waitForEditable = true)
    {
        ClickTile(window, index);
        var details = Assert.IsType<PhotoDetailsViewModel>(vm.Details);
        if (waitForEditable) await WaitForAsync(() => details.CanEdit);
        return details;
    }

    protected static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    protected static void Click(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Dispatcher.UIThread.RunJobs();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException("Control isn't in the window.");
        window.MouseDown(centre, MouseButton.Left, modifiers);
        window.MouseUp(centre, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    protected static T Find<T>(Window window, string name) where T : Control =>
        FindAll<T>(window).FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    protected static IReadOnlyList<T> FindAll<T>(Window window) where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        return [.. window.GetVisualDescendants().OfType<T>()];
    }

    protected static bool IsFocusWithin(Window window, Control control) =>
        window.FocusManager?.GetFocusedElement() is Visual focused
        && (focused == control || focused.GetVisualAncestors().Contains(control));

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }
}
