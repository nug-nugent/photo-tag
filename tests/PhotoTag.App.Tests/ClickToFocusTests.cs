using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoTag.Core;

namespace PhotoTag.App.Tests;

/// <summary>A click anywhere in a text box's box focuses it, not only a click on its placeholder.</summary>
public sealed class ClickToFocusTests : UiTestBase
{
    private static void ClickAt(Window window, Control control, double x, double y)
    {
        Settle();
        var point = control.TranslatePoint(new Point(x, y), window)
                    ?? throw new InvalidOperationException("Control isn't in the window.");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertClickFocuses(Window window, AutoCompleteBox box, Control control, double x, double y)
    {
        Find<TextBox>(window, "TitleBox").Focus();
        ClickAt(window, control, x, y);
        Assert.True(IsFocusWithin(window, box), $"Clicking {control.GetType().Name} at ({x}, {y}) didn't focus {box.Name}.");
    }

    [AvaloniaTheory]
    [InlineData("NewTagBox")]
    [InlineData("NewPersonBox")]
    public async Task TagAndPeopleBoxes_ClickAnywhereInTheBox_FocusesTheTextBox(string name)
    {
        await using var exifTool = RequireExifTool();
        Photo("a.jpg", "Beach");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        await SelectSingleAsync(window, vm, 0);
        var box = Find<AutoCompleteBox>(window, name);
        var border = box.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("box"));

        AssertClickFocuses(window, box, border, border.Bounds.Width - 6, border.Bounds.Height / 2); // beside the text box
        AssertClickFocuses(window, box, border, border.Bounds.Width / 2, 3);                         // the box's top edge
        AssertClickFocuses(window, box, box, box.Bounds.Width - 3, box.Bounds.Height / 2);         // the text box itself
        window.Close();
    }

    [AvaloniaFact]
    public async Task PlaceBoxes_ClickAnywhereInTheBox_FocusesTheTextBox()
    {
        await using var exifTool = RequireExifTool();
        Photo("a.jpg");
        var (window, vm) = await OpenAsync(new PhotoMetadataWriter(exifTool));
        await SelectSingleAsync(window, vm, 0);
        foreach (var name in new[] { "LocationBox", "CityBox", "StateBox", "CountryBox" })
        {
            var box = Find<AutoCompleteBox>(window, name);
            AssertClickFocuses(window, box, box, box.Bounds.Width - 3, box.Bounds.Height / 2);
            AssertClickFocuses(window, box, box, 3, 3);
        }
        window.Close();
    }
}
