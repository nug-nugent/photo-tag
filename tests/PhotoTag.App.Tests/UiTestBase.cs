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
public abstract class UiTestBase : IAsyncDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _appData = new(); // index, settings and thumbnails live outside the library
    private readonly List<(MainWindowViewModel Vm, LibraryIndex Index)> _opened = [];

    protected string DirPath => _dir.Path;

    protected string SettingsPath => Path.Combine(_appData.Path, "settings.json");

    /// <summary>Ctrl, or ⌘ where the platform uses it, as a raw input modifier.</summary>
    protected static RawInputModifiers CommandKey =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers == KeyModifiers.Meta
            ? RawInputModifiers.Meta
            : RawInputModifiers.Control;

    /// <summary>Creates a test photo; <paramref name="name"/> may include subfolders.</summary>
    protected string Photo(string name, params string[] keywords)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(DirPath, name))!);
        return TestImages.Write(DirPath, name, TestImages.Jpeg(120, 90, xmpKeywords: keywords.Length > 0 ? keywords : null));
    }

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

    protected async Task<(MainWindow Window, MainWindowViewModel Vm)> OpenAsync(PhotoMetadataWriter? writer, PhotoRenderer? renderer = null,
        IAppUpdater? updater = null)
    {
        var settings = AppSettings.Load(SettingsPath);
        var thumbnails = new ThumbnailCache(Path.Combine(_appData.Path, $"thumbnails{_opened.Count}"), renderer);
        var index = new LibraryIndex(Path.Combine(_appData.Path, $"library{_opened.Count}.db"));
        var vm = new MainWindowViewModel(thumbnails, settings, writer, index, renderer, updater);
        _opened.Add((vm, index));
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

    /// <summary>
    /// Runs queued work and a render tick. Headless layout only happens on render ticks, so without one
    /// a list that was just rebuilt may have no rows yet (seen on slower CI machines).
    /// </summary>
    protected static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    protected static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
            Settle();
            await Task.Delay(20);
        }
        Settle();
    }

    protected static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 15000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
            Settle();
            await Task.Delay(20);
        }
        Settle();
    }

    /// <summary>Waits for a control to be on screen with a size, e.g. a row in a list that was just rebuilt.</summary>
    protected static async Task<T> WaitForControlAsync<T>(Func<T?> find) where T : Control
    {
        T? found = null;
        await WaitForAsync(() => (found = find()) is { IsEffectivelyVisible: true, Bounds.Width: > 0 });
        return found!;
    }

    protected static void Click(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Settle();
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
        Settle();
        return [.. window.GetVisualDescendants().OfType<T>()];
    }

    protected static bool IsFocusWithin(Window window, Control control) =>
        window.FocusManager?.GetFocusedElement() is Visual focused
        && (focused == control || focused.GetVisualAncestors().Contains(control));

    public async ValueTask DisposeAsync()
    {
        foreach (var (vm, index) in _opened)
        {
            vm.Dispose(); // cancels any scan still running
            await vm.Library.ScanCompletion;
            index.Dispose();
        }
        _dir.Dispose();
        _appData.Dispose();
        GC.SuppressFinalize(this);
    }
}
