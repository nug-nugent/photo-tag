using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;
using PhotoTag.App.Views;
using PhotoTag.Core;
using PhotoTag.Core.Tests;

namespace PhotoTag.App.Tests;

/// <summary>
/// Drives the real main window headlessly: typing, clicking and focus changes go through
/// Avalonia's input pipeline, and edits are written to real files by real ExifTool.
/// </summary>
public sealed class TagEditingTests : IDisposable
{
    private readonly TempDir _dir = new();

    [AvaloniaFact]
    public async Task EditsInThePanel_AreWrittenToTheFile()
    {
        await using var exifTool = RequireExifTool();
        var photo = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(400, 300, xmpKeywords: ["Garden"]));
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectOnlyPhotoAsync(vm);

        // Type two tags into the tag box, comma-separated, and press Enter.
        var tagBox = Find<AutoCompleteBox>(window, "NewTagBox");
        tagBox.Focus();
        window.KeyTextInput("Beach, Sunset");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitForSaveAsync(details);
        Assert.Equal(["Garden", "Beach", "Sunset"], PhotoMetadata.Read(photo).Keywords);
        Assert.Equal("", details.NewKeyword);
        Assert.True(IsFocusWithin(window, tagBox), "focus should stay in the tag box so more tags can be typed");

        // Remove "Garden" with its ✕ button.
        Click(window, FindAll<Button>(window).First(b => b.Classes.Contains("chipRemove")));
        await WaitForSaveAsync(details);
        Assert.Equal(["Beach", "Sunset"], PhotoMetadata.Read(photo).Keywords);

        // Click the third star.
        Click(window, FindAll<Button>(window).Where(b => b.Classes.Contains("star")).ElementAt(2));
        await WaitForSaveAsync(details);
        Assert.Equal(3, PhotoMetadata.Read(photo).Rating);

        // Title and description save when focus leaves the box.
        var titleBox = Find<TextBox>(window, "TitleBox");
        var descriptionBox = Find<TextBox>(window, "DescriptionBox");
        titleBox.Focus();
        window.KeyTextInput("Christmas morning");
        descriptionBox.Focus();
        await WaitForSaveAsync(details);
        Assert.Equal("Christmas morning", PhotoMetadata.Read(photo).Title);

        window.KeyTextInput("Line one");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput("Line two & more");
        titleBox.Focus();
        await WaitForSaveAsync(details);
        Assert.Equal("Line one\nLine two & more", PhotoMetadata.Read(photo).Description); // stored with \n on every OS
        Assert.Equal("Christmas morning", PhotoMetadata.Read(photo).Title); // unchanged by the description save

        window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingTheCurrentRating_ClearsIt()
    {
        await using var exifTool = RequireExifTool();
        var photo = TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 100, xmpKeywords: ["Rated"])); // xmp:Rating=4
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        var details = await SelectOnlyPhotoAsync(vm);
        Assert.Equal(4, details.Rating);

        Click(window, FindAll<Button>(window).Where(b => b.Classes.Contains("star")).ElementAt(3));
        await WaitForSaveAsync(details);

        Assert.Equal(0, details.Rating);
        Assert.Null(PhotoMetadata.Read(photo).Rating);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutExifTool_PanelIsReadOnly_AndExplainsWhy()
    {
        TestImages.Write(_dir.Path, "photo.jpg", TestImages.Jpeg(200, 100, xmpKeywords: ["Existing"]));
        var (window, vm) = await OpenAsync(writer: null);
        var details = await SelectOnlyPhotoAsync(vm, waitForEditable: false);
        await WaitForAsync(() => details.Keywords.Count == 1);

        Assert.False(details.CanEdit);
        Assert.False(Find<AutoCompleteBox>(window, "NewTagBox").IsEffectivelyEnabled);
        Assert.False(Find<TextBox>(window, "TitleBox").IsEffectivelyEnabled);
        Assert.Contains(FindAll<TextBlock>(window), t => t.IsEffectivelyVisible && t.Text?.Contains("needs ExifTool") == true);
        window.Close();
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static ExifTool RequireExifTool()
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

    private async Task<(MainWindow, MainWindowViewModel)> OpenAsync(PhotoMetadataWriter? writer)
    {
        var settings = AppSettings.Load(Path.Combine(_dir.Path, "settings.json"));
        var thumbnails = new ThumbnailCache(Path.Combine(_dir.Path, ".cache"));
        var vm = new MainWindowViewModel(thumbnails, settings, writer);
        // Tall enough that the whole details panel is on screen without scrolling.
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 2400 };
        window.Show();
        vm.OpenRoot(_dir.Path);
        await WaitForAsync(() => vm.Photos.Count > 0);
        return (window, vm);
    }

    private static async Task<PhotoDetailsViewModel> SelectOnlyPhotoAsync(MainWindowViewModel vm, bool waitForEditable = true)
    {
        vm.SelectPhotoCommand.Execute(vm.Photos.Single());
        var details = vm.Details!;
        if (waitForEditable) await WaitForAsync(() => details.CanEdit);
        return details;
    }

    private static async Task WaitForSaveAsync(PhotoDetailsViewModel details)
    {
        var saves = details.SaveCompletion;
        await WaitForAsync(() => saves.IsCompleted);
        Assert.False(details.SaveFailed, details.SaveStatus);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
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

    private static void Click(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new InvalidOperationException("Control isn't in the window.");
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        FindAll<T>(window).FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static IEnumerable<T> FindAll<T>(Window window) where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<T>().ToList();
    }

    private static bool IsFocusWithin(Window window, Control control) =>
        window.FocusManager?.GetFocusedElement() is Visual focused
        && (focused == control || focused.GetVisualAncestors().Contains(control));

    public void Dispose() => _dir.Dispose();
}
