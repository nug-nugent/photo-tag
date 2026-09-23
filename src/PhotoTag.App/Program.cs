using Avalonia;
using Velopack;

namespace PhotoTag.App;

internal sealed class Program
{
    // Don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant code
    // before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles install, update and uninstall hooks, then returns.
        VelopackApp.Build().Run();

        if (args is ["--self-check", var report]) return SelfCheck.RunAsync(report).GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
