// Renders PhotoTag's main window to PNGs for design work: the real window, styles and view
// models, drawn by Skia on the headless platform, so no window appears and the mouse and
// keyboard are never touched.
//
//   dotnet run --project tools/Screenshots -- [output folder]
//
// The sample library is built once from the CC0 RAW samples in tests/.samples (run the tests
// once to download them) and needs ExifTool on PATH. Output defaults to artifacts/screenshots.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using PhotoTag.App;
using PhotoTag.App.ViewModels;
using PhotoTag.App.Views;
using Avalonia.Controls.Primitives;
using PhotoTag.Core;

var repo = FindRepoRoot();
// Never hang forever: if nothing has happened for a while, say where it got to and give up.
var step = "start";
var lastProgress = Stopwatch.StartNew();
_ = Task.Run(async () =>
{
    while (lastProgress.Elapsed < TimeSpan.FromSeconds(90)) await Task.Delay(1000);
    Console.Error.WriteLine($"Stuck at: {step}");
    Environment.Exit(2);
});
var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(repo, "artifacts", "screenshots"));
var work = Path.Combine(repo, "artifacts", "screenshot-library");
Directory.CreateDirectory(output);

AppBuilder.Configure<App>()
    .UseSkia()
    .WithInterFont()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

var exifToolPath = ExifTool.Locate() ?? throw new InvalidOperationException("ExifTool must be on PATH.");
// No top-level awaits: their continuations would be posted to a dispatcher nothing is pumping.
var exifTool = new ExifTool(exifToolPath);
var previewTool = new ExifTool(exifToolPath);
var writer = new PhotoMetadataWriter(exifTool);
var renderer = new PhotoRenderer(new RawPreviewExtractor(previewTool));

// Always stop ExifTool: a leftover process would keep this one's output pipe open, so it seems to hang.
try
{
    var library = Path.Combine(work, "Photos");
    if (!Directory.Exists(library)) WaitFor(BuildLibraryAsync(Path.Combine(repo, "tests", ".samples"), library, renderer, writer, exifTool));

    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
    {
        Application.Current!.RequestedThemeVariant = theme;
        var name = theme == ThemeVariant.Dark ? "dark" : "light";

        // Fresh app data each time, as on first launch.
        var appData = Path.Combine(work, "appdata-" + name);
        if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
        Directory.CreateDirectory(appData);
        using var thumbnails = new ThumbnailCache(Path.Combine(appData, "thumbnails"), renderer);
        using var index = new LibraryIndex(Path.Combine(appData, "library.db"));
        var settings = AppSettings.Load(Path.Combine(appData, "settings.json"));
        var vm = new MainWindowViewModel(thumbnails, settings, writer, index, renderer, placeLookup: new CannedPlaceLookup());
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        step = "show window"; window.Show();

        step = "open root"; vm.OpenRoot(library);
        step = "scan"; WaitFor(vm.Library.ScanCompletion);
        var root = vm.RootFolders.Single();
        WaitFor(root.ChildrenLoading);
        var year = root.Children.Single(c => c.Name == "2024");
        year.IsExpanded = true;
        WaitFor(year.ChildrenLoading);
        var cornwall = year.Children.Single(c => c.Name == "Cornwall");
        vm.SelectedFolder = cornwall;
        step = "photos"; WaitFor(vm.PhotosLoading);
        step = "thumbnails"; WaitForThumbnails(vm);
        step = "capture 1"; Capture(window, $"{name}-1-folder");

        vm.Select(vm.Photos[1]);
        var single = (PhotoDetailsViewModel)vm.Details!;
        WaitUntil(() => single.IsLoaded && single.Preview is not null);
        Capture(window, $"{name}-2-photo");
        CaptureTall(window, $"{name}-2-photo-full");
        WaitFor(single.LookUpExactPlaceCommand.ExecuteAsync(null));
        CaptureTall(window, $"{name}-2-photo-lookup");
        single.CloseLookupCommand.Execute(null);

        vm.SelectAll();
        var bulk = (BulkDetailsViewModel)vm.Details!;
        WaitUntil(() => bulk.IsLoaded);
        Capture(window, $"{name}-3-several");
        WaitFor(bulk.ReviewFillPlacesCommand.ExecuteAsync(null));
        CaptureTall(window, $"{name}-3-several-fill");
        bulk.CancelFillCommand.Execute(null);
        CaptureTall(window, $"{name}-3-several-full");

        vm.ClearSelection();
        ShowFlyout(window, "TagsButton");
        WaitFor(vm.TagManager.Loading);
        Capture(window, $"{name}-4-tags-panel");
        vm.TagManager.Field = ListField.People;
        WaitFor(vm.TagManager.Loading);
        Capture(window, $"{name}-4-people-panel");
        vm.TagManager.Field = ListField.Tags;
        HideFlyouts(window);

        ShowFlyout(window, "SettingsButton");
        Capture(window, $"{name}-5-settings");
        HideFlyouts(window);

        vm.SearchText = "beach";
        vm.SearchCommand.Execute(null);
        WaitFor(vm.PhotosLoading);
        WaitForThumbnails(vm);
        Capture(window, $"{name}-6-search");

        // Every photo, grouped by day, favourites at double size.
        vm.ClearSearchCommand.Execute(null);
        vm.ShowAllPhotos = true;
        WaitFor(vm.PhotosLoading);
        vm.Sort = PhotoSort.Days;
        WaitForThumbnails(vm);
        Capture(window, $"{name}-8-days");
        // Down to the first day with favourites, as "Jump to day" would.
        var grid = window.GetLogicalDescendants().OfType<ItemsRepeater>().Single(r => r.Name == "PhotoGrid");
        var scroller = window.GetLogicalDescendants().OfType<ScrollViewer>().Single(s => s.Name == "GridScroller");
        var top = ((PhotoGridLayout)grid.Layout!).GetRect(vm.Days.First(d => d.FavouriteCount > 0).GridIndex)!.Value.Top;
        scroller.Offset = new Vector(0, top + grid.Margin.Top);
        // And open that day's year and month in "Jump to day".
        var favouriteYear = vm.DateTree.First(n => n.FavouriteCount > 0);
        favouriteYear.IsExpanded = true;
        favouriteYear.Children.First(m => m.FavouriteCount > 0).IsExpanded = true;
        WaitForThumbnails(vm);
        Capture(window, $"{name}-8-days-favourites");

        // The viewer, with a favourite showing.
        vm.Select(vm.Photos.First(p => p.IsFavourite));
        vm.OpenViewer();
        WaitUntil(() => vm.Viewer?.Preview is not null && vm.Details is PhotoDetailsViewModel { IsLoaded: true });
        Capture(window, $"{name}-9-viewer");
        vm.CloseViewer();
        vm.Sort = PhotoSort.FileName;

        // Narrowest the window allows.
        vm.ClearSearchCommand.Execute(null);
        vm.SelectedFolder = cornwall;
        WaitFor(vm.PhotosLoading);
        WaitForThumbnails(vm);
        vm.Select(vm.Photos[0]);
        WaitUntil(() => vm.Details is PhotoDetailsViewModel { IsLoaded: true });
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Capture(window, $"{name}-7-smallest");

        window.Close();
        vm.Dispose();
        WaitFor(vm.Library.ScanCompletion);
    }

}
finally
{
    WaitFor(exifTool.DisposeAsync().AsTask());
    WaitFor(previewTool.DisposeAsync().AsTask());
}

Console.WriteLine($"Screenshots in {output}");
return 0;

// --- Helpers -------------------------------------------------------------------------------

void Capture(Window window, string file)
{
    Pump(300); // let layout, images and animations settle
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
    frame.Save(Path.Combine(output, file + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    Console.WriteLine(file);
    step = "after " + file;
    lastProgress.Restart();
}

void CaptureTall(Window window, string file)
{
    var height = window.Height;
    window.Height = 1600;
    Capture(window, file);
    window.Height = height;
    Pump(100);
}

static void ShowFlyout(Window window, string buttonName)
{
    var button = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == buttonName);
    button.Flyout!.ShowAt(button);
    Pump(200);
}

static void HideFlyouts(Window window)
{
    foreach (var button in window.GetLogicalDescendants().OfType<Button>())
        button.Flyout?.Hide();
    Pump(100);
}

void WaitForThumbnails(MainWindowViewModel vm)
{
    try
    {
        // Only tiles on screen load, so wait until loading stops rather than for a fixed number.
        var loaded = -1;
        var steady = Stopwatch.StartNew();
        WaitUntil(() =>
        {
            var now = vm.Photos.Count(p => p.Thumbnail is not null || p.LoadFailed);
            if (now != loaded)
            {
                loaded = now;
                steady.Restart();
            }
            return vm.Photos.Count == 0 || (loaded > 0 && steady.ElapsedMilliseconds > 1500);
        });
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine($"Thumbnails: {vm.Photos.Count} photos, {vm.Photos.Count(p => p.Thumbnail is not null)} loaded, {vm.Photos.Count(p => p.LoadFailed)} failed");
        throw;
    }
}

static void Pump(int milliseconds)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.ElapsedMilliseconds < milliseconds)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
}

static void WaitUntil(Func<bool> done)
{
    var stopwatch = Stopwatch.StartNew();
    while (!done())
    {
        if (stopwatch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Gave up waiting.");
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); // layout (and so the grid's tiles) happens on render ticks
        Thread.Sleep(15);
    }
    Dispatcher.UIThread.RunJobs();
}

static void WaitFor(Task task) => WaitUntil(() => task.IsCompleted);

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(dir, "PhotoTag.slnx"))) dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("Run from inside the repo.");
    return dir;
}

// A small library that looks like a real one: RAWs, JPEGs, subfolders, tags, places, favourites.
static async Task BuildLibraryAsync(string samples, string library, PhotoRenderer renderer, PhotoMetadataWriter writer, ExifTool exifTool)
{
    var raws = Directory.GetFiles(samples).Order().ToList();
    if (raws.Count == 0) throw new InvalidOperationException($"No RAW samples in {samples}: run the tests once to download them.");

    var cornwall = Directory.CreateDirectory(Path.Combine(library, "2024", "Cornwall")).FullName;
    var scotland = Directory.CreateDirectory(Path.Combine(library, "2025", "Scotland")).FullName;
    Directory.CreateDirectory(Path.Combine(library, "2023"));

    var n = 0;
    foreach (var raw in raws)
    {
        var jpeg = await renderer.RenderAsync(raw, 2000);
        for (var copy = 0; copy < 3; copy++)
        {
            var folder = copy == 2 ? scotland : cornwall;
            await File.WriteAllBytesAsync(Path.Combine(folder, $"IMG_{1000 + n++:D4}.jpg"), jpeg);
        }
        File.Copy(raw, Path.Combine(cornwall, "RAW_" + Path.GetFileName(raw)));
    }

    var photos = Directory.GetFiles(cornwall, "*.jpg").Order().ToList();
    string[][] tags = [["Beach", "Family"], ["Beach", "Sunset"], ["Harbour"], ["Beach", "Dog", "Family"], ["Cliffs"]];
    for (var i = 0; i < photos.Count; i++)
    {
        await writer.WriteAsync(photos[i], new MetadataChanges
        {
            Keywords = tags[i % tags.Length],
            People = i % 3 == 1 ? ["Mary Smith", "Dad"] : i % 3 == 2 ? ["Mary Smith"] : null,
            Favourite = i % 4 == 1 ? true : null,
            Title = i == 1 ? "Evening light over Porthcurno" : null,
            Description = i == 1 ? "The tide was just going out, and the last of the sun caught the cliffs." : null,
            Location = i % 3 == 1 ? "Porthcurno beach" : null,
            City = i % 3 == 1 ? "St Levan" : i % 3 == 2 ? "St Ives" : null,
            State = i % 3 != 0 ? "Cornwall" : null,
            Country = i % 3 != 0 ? "United Kingdom" : null,
        });
    }

    // Three days in Cornwall and a weekend in Scotland, for the grid grouped by day.
    var dated = photos.Select((p, i) => (Photo: p, Date: new DateTime(2024, 8, 12, 9, 0, 0).AddDays(i * 3 / photos.Count).AddMinutes(i * 37)))
        .Concat(Directory.GetFiles(scotland, "*.jpg").Order().Select((p, i) => (Photo: p, Date: new DateTime(2025, 5, 3, 10, 0, 0).AddDays(i / 4).AddMinutes(i * 23))));
    foreach (var (photo, date) in dated)
        await exifTool.ExecuteAsync([$"-DateTimeOriginal={date:yyyy:MM:dd HH:mm:ss}", "-overwrite_original", photo]);

    // GPS: Porthcurno on the photo the details panel shows, and St Ives and Mousehole on photos with no
    // place yet, for "Fill from GPS".
    foreach (var (photo, lat, lon) in new[] { (photos[1], 50.0421, 5.6543), (photos[0], 50.2083, 5.4908), (photos[3], 50.0833, 5.5389) })
        await exifTool.ExecuteAsync([$"-GPSLatitude={lat}", "-GPSLatitudeRef=N", $"-GPSLongitude={lon}", "-GPSLongitudeRef=W",
            "-overwrite_original", photo]);
}

/// <summary>Stands in for OpenStreetMap, so rendering never goes online.</summary>
sealed class CannedPlaceLookup : IExactPlaceLookup
{
    public Task<ExactPlace?> LookUpAsync(double latitude, double longitude, CancellationToken cancellationToken = default) =>
        Task.FromResult<ExactPlace?>(new ExactPlace("Porthcurno Beach", "St Levan", "Cornwall", "United Kingdom"));
}
