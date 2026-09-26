using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoTag.App.ViewModels;

namespace PhotoTag.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _subscribed;
    private ViewerViewModel? _viewer;

    public MainWindow()
    {
        InitializeComponent();
        // Tunnelling, so shortcuts work wherever focus is (the viewer has no single focused control).
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        SearchShortcutText.Text = CommandModifier == KeyModifiers.Meta ? "⌘ K" : "Ctrl K";
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private PhotoGridLayout? GridLayout => PhotoGrid.Layout as PhotoGridLayout;

    /// <summary>Ctrl on Windows and Linux, ⌘ on macOS.</summary>
    private static KeyModifiers CommandModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.GridLayoutChanged -= OnGridLayoutChanged;
            _subscribed.PropertyChanged -= OnViewModelChanged;
        }
        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.GridLayoutChanged += OnGridLayoutChanged;
            _subscribed.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnGridLayoutChanged(object? sender, EventArgs e) => GridLayout?.Invalidate();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.Viewer)) return;

        if (_viewer is not null) _viewer.PropertyChanged -= OnViewerChanged;
        _viewer = ViewModel?.Viewer;
        if (_viewer is not null)
        {
            _viewer.PropertyChanged += OnViewerChanged;
            Dispatcher.UIThread.Post(() =>
            {
                ViewerPanel.Focus();
                ScrollFilmstrip();
            }, DispatcherPriority.Loaded);
        }
        else
        {
            // Back in the grid, where the viewer left off.
            GridScroller.Focus();
            if (ViewModel?.CurrentPhoto is { } current) Dispatcher.UIThread.Post(() => ScrollIntoView(current.GridIndex), DispatcherPriority.Loaded);
        }
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.Current)) ScrollFilmstrip();
    }

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

    // --- Keyboard --------------------------------------------------------------------------

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        if (e.Key == Key.K && e.KeyModifiers == CommandModifier)
        {
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        if (vm.Viewer is not { } viewer) return;

        // Typing a tag: only Esc is ours, to get back to the photo.
        if (FocusManager?.GetFocusedElement() is Visual focused && (focused is TextBox || focused.FindAncestorOfType<TextBox>() is not null))
        {
            if (e.Key != Key.Escape) return;
            ViewerPanel.Focus();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;

        switch (e.Key)
        {
            case Key.Left: viewer.Move(-1); break;
            case Key.Right: viewer.Move(1); break;
            case Key.Home: viewer.Move(-viewer.Photos.Count); break;
            case Key.End: viewer.Move(viewer.Photos.Count); break;
            case Key.F: _ = vm.ToggleSelectedFavouritesAsync(); break;
            case Key.T:
                // After the key's own text input has gone by, or the box would start with a "t".
                Dispatcher.UIThread.Post(() => FindNamed("ViewerTagBox")?.Focus(), DispatcherPriority.Background);
                break;
            case Key.Escape: vm.CloseViewer(); break;
            default: return;
        }
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
            case Key.Up: MoveVertically(vm, down: false, shift); break;
            case Key.Down: MoveVertically(vm, down: true, shift); break;
            case Key.Home: vm.MoveCurrent(-vm.Photos.Count, shift); break;
            case Key.End: vm.MoveCurrent(vm.Photos.Count, shift); break;
            case Key.A when e.KeyModifiers.HasFlag(CommandModifier): vm.SelectAll(); break;
            case Key.Z when e.KeyModifiers.HasFlag(CommandModifier):
                vm.UndoCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.F when e.KeyModifiers == KeyModifiers.None:
                _ = vm.ToggleSelectedFavouritesAsync();
                e.Handled = true;
                return;
            case Key.Space or Key.Enter when e.KeyModifiers == KeyModifiers.None:
                vm.OpenViewer();
                e.Handled = true;
                return;
            case Key.Escape: vm.ClearSelection(); break;
            default: return;
        }

        if (vm.CurrentPhoto is { } current && e.Key != Key.A && e.Key != Key.Escape) ScrollIntoView(current.GridIndex);
        e.Handled = true;
    }

    /// <summary>↑ and ↓ go to the tile above or below, which the layout knows (tiles can be double size, days have headings).</summary>
    private void MoveVertically(MainWindowViewModel vm, bool down, bool extend)
    {
        if (vm.CurrentPhoto is not { } current)
        {
            vm.MoveCurrent(down ? 1 : -1, extend);
            return;
        }
        if (GridLayout?.FindVertical(current.GridIndex, down) is { } index && vm.GridItems[index] is PhotoItemViewModel target)
            vm.MoveTo(target, extend);
    }

    /// <summary>Scrolls the grid just enough to show an item.</summary>
    private void ScrollIntoView(int gridIndex)
    {
        if (GridLayout?.GetRect(gridIndex) is not { } rect)
        {
            if (gridIndex >= 0 && gridIndex < ViewModel?.GridItems.Count) PhotoGrid.GetOrCreateElement(gridIndex).BringIntoView();
            return;
        }
        var top = rect.Top + PhotoGrid.Margin.Top - 8;
        var bottom = rect.Bottom + PhotoGrid.Margin.Top + 8;
        var offset = GridScroller.Offset.Y;
        var height = GridScroller.Viewport.Height;
        if (top < offset) GridScroller.Offset = new Vector(0, top);
        else if (bottom > offset + height) GridScroller.Offset = new Vector(0, bottom - height);
    }

    private Control? FindNamed(string name) => this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);

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
        // A double-click's first click selects just this tile; two quick clicks either side of Ctrl/⌘+A, say, aren't one.
        var wasOnlySelection = vm.SelectedCount == 1 && photo.IsSelected;
        vm.Select(photo, (command, shift) switch
        {
            (true, true) => SelectionGesture.AddRange,
            (true, false) => SelectionGesture.Toggle,
            (false, true) => SelectionGesture.Range,
            _ => SelectionGesture.Replace,
        });

        GridScroller.Focus(NavigationMethod.Pointer);
        if (e.ClickCount == 2 && !command && !shift && wasOnlySelection) vm.OpenViewer(photo);
        e.Handled = true;
    }

    // Clicking the preview in the details panel opens that photo in the viewer.
    private void DetailsPreview_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ViewModel is { } vm && sender is Control { DataContext: PhotoDetailsViewModel details }) vm.OpenViewer(details.Photo);
        e.Handled = true;
    }

    private void TileHeart_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Control { DataContext: PhotoItemViewModel photo }) _ = vm.ToggleFavouriteAsync(photo);
        e.Handled = true;
    }

    private void JumpToDay_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DayHeaderViewModel day } || GridLayout?.GetRect(day.GridIndex) is not { } rect) return;
        GridScroller.Offset = new Vector(0, rect.Top + PhotoGrid.Margin.Top);
    }

    // --- The command bar, with several photos selected --------------------------------------

    private void CommandBarTags_Click(object? sender, RoutedEventArgs e) => FocusDetails("BulkTagBox");

    private void CommandBarTitle_Click(object? sender, RoutedEventArgs e) => FocusDetails("BulkTitleBox");

    private void CommandBarPlace_Click(object? sender, RoutedEventArgs e) => FocusDetails("BulkLocationBox");

    private void CommandBarViewer_Click(object? sender, RoutedEventArgs e) => ViewModel?.OpenViewer();

    private void CommandBarClear_Click(object? sender, RoutedEventArgs e) => ViewModel?.ClearSelection();

    private void FocusDetails(string name)
    {
        if (FindNamed(name) is not { } box) return;
        box.BringIntoView();
        box.Focus();
    }

    // --- Viewer ----------------------------------------------------------------------------

    private void FilmstripItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PhotoItemViewModel photo }) ViewModel?.Viewer?.Show(photo);
        ViewerPanel.Focus();
        e.Handled = true;
    }

    private void ScrollFilmstrip()
    {
        if (ViewModel?.Viewer is { } viewer && viewer.Photos.Count > 0)
            Filmstrip.GetOrCreateElement(viewer.CurrentIndex).BringIntoView();
    }

    // --- Thumbnails ------------------------------------------------------------------------

    // The grid tells us which tiles are on screen; thumbnails are loaded and freed to match.
    private void PhotoGrid_ElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (PhotoGrid.ItemsSourceView?.GetAt(e.Index) is PhotoItemViewModel photo) photo.Realize();
    }

    private void Filmstrip_ElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (Filmstrip.ItemsSourceView?.GetAt(e.Index) is PhotoItemViewModel photo) photo.Realize();
    }

    private void PhotoGrid_ElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (e.Element.DataContext is PhotoItemViewModel photo) photo.Release();
    }
}
