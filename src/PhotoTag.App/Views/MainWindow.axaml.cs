using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PhotoTag.App.ViewModels;

namespace PhotoTag.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder of photos",
            AllowMultiple = false,
        });

        if (folders is [var folder, ..] && folder.TryGetLocalPath() is { } path)
            ViewModel?.OpenRoot(path);
    }

    // The grid tells us which tiles are on screen; thumbnails are loaded and freed to match.
    private void PhotoGrid_ElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (PhotoGrid.ItemsSourceView?.GetAt(e.Index) is PhotoItemViewModel photo) photo.Realize();
    }

    private void PhotoGrid_ElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (e.Element.DataContext is PhotoItemViewModel photo) photo.Release();
    }
}
