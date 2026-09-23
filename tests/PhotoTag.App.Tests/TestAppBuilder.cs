using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(PhotoTag.App.Tests.TestAppBuilder))]

namespace PhotoTag.App.Tests;

public static class TestAppBuilder
{
    // The real App (theme, styles), on the headless platform: no window appears.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
