using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PhotoTag.App.ViewModels;
using PhotoTag.App.Views;
using PhotoTag.Core;

namespace PhotoTag.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var thumbnails = new ThumbnailCache(ThumbnailCache.DefaultDirectory);
            var viewModel = new MainWindowViewModel(thumbnails, settings);

            // Optional: a folder passed on the command line wins over the last-used one.
            var startFolder = desktop.Args is [var arg, ..] ? arg : settings.LastFolder;
            if (startFolder is not null && Directory.Exists(startFolder)) viewModel.OpenRoot(startFolder);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                thumbnails.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
