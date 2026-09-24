using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoTag.App.ViewModels;

namespace PhotoTag.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>Ctrl on Windows and Linux, ⌘ on macOS.</summary>
    private static KeyModifiers CommandModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

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

    // --- Tags panel ------------------------------------------------------------------------

    private void TagsFlyout_Opened(object? sender, EventArgs e) => ViewModel?.TagManager.Open();

    private void TagsFlyout_Closed(object? sender, EventArgs e) => ViewModel?.TagManager.Close();

    // Clicking the pencil shows the rename box: put the cursor in it, with the old name selected.
    private void TagNewName_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || e.NewValue is not true || sender is not TextBox box) return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    // --- Selection -------------------------------------------------------------------------

    private void Tile_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control { DataContext: PhotoItemViewModel photo }) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var command = e.KeyModifiers.HasFlag(CommandModifier);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        vm.Select(photo, (command, shift) switch
        {
            (true, true) => SelectionGesture.AddRange,
            (true, false) => SelectionGesture.Toggle,
            (false, true) => SelectionGesture.Range,
            _ => SelectionGesture.Replace,
        });

        GridScroller.Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    private void Grid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Left: vm.MoveCurrent(-1, shift); break;
            case Key.Right: vm.MoveCurrent(1, shift); break;
            case Key.Up: vm.MoveCurrent(-ColumnCount(), shift); break;
            case Key.Down: vm.MoveCurrent(ColumnCount(), shift); break;
            case Key.Home: vm.MoveCurrent(-vm.Photos.Count, shift); break;
            case Key.End: vm.MoveCurrent(vm.Photos.Count, shift); break;
            case Key.A when e.KeyModifiers.HasFlag(CommandModifier): vm.SelectAll(); break;
            case Key.Escape: vm.ClearSelection(); break;
            default: return;
        }

        if (vm.CurrentPhoto is { } current && e.Key != Key.A && e.Key != Key.Escape)
            PhotoGrid.GetOrCreateElement(current.Index).BringIntoView();
        e.Handled = true;
    }

    /// <summary>How many tiles fit across, matching UniformGridLayout's own calculation.</summary>
    private int ColumnCount()
    {
        if (PhotoGrid.Layout is not UniformGridLayout layout) return 1;
        var width = PhotoGrid.Bounds.Width;
        return Math.Max(1, (int)((width + layout.MinColumnSpacing) / (layout.MinItemWidth + layout.MinColumnSpacing)));
    }

    // --- Thumbnails ------------------------------------------------------------------------

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
