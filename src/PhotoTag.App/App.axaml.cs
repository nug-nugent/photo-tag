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
            // Tag editing needs ExifTool. Without it the app still browses, read-only.
            var exifToolPath = ExifTool.Locate(AppContext.BaseDirectory);
            var exifTool = exifToolPath is null ? null : new ExifTool(exifToolPath);
            var writer = exifTool is null ? null : new PhotoMetadataWriter(exifTool);
            // A second ExifTool for RAW previews, so loading thumbnails never waits behind saving tags.
            var previewTool = exifToolPath is null ? null : new ExifTool(exifToolPath);
            var renderer = new PhotoRenderer(previewTool is null ? null : new RawPreviewExtractor(previewTool));
            var thumbnails = new ThumbnailCache(ThumbnailCache.DefaultDirectory, renderer);
            var index = new LibraryIndex(LibraryIndex.DefaultPath);
            var placeLookup = new NominatimLookup();
            var viewModel = new MainWindowViewModel(thumbnails, settings, writer, index, renderer, new GitHubReleasesUpdater(), placeLookup);

            // Optional: a folder passed on the command line wins over the last-used one.
            var startFolder = desktop.Args is [var arg, ..] ? arg : settings.LastFolder;
            if (startFolder is not null && Directory.Exists(startFolder)) viewModel.OpenRoot(startFolder);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow.Opened += (_, _) => _ = viewModel.Updates.CheckOnStartupAsync();
            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                thumbnails.Dispose();
                index.Dispose();
                placeLookup.Dispose();
                // Waits for any in-flight write to finish, then stops ExifTool.
                exifTool?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
                previewTool?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
