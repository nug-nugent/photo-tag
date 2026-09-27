using Avalonia;
using PhotoTag.Core;
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

        var log = new LogFile(LogFile.DefaultPath);
        Log.Start(log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("PhotoTag crashed", e.ExceptionObject as Exception);
            log.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("A background task failed", e.Exception);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.Info("Closed");
            log.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
