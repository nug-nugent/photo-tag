using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PhotoTag.App.ViewModels;

namespace PhotoTag.App.Views;

/// <summary>Settings, opened from ⚙ in the header. Changes apply and are saved straight away; Done or Esc closes it.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => _ = (DataContext as SettingsViewModel)?.MeasureThumbnailsAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        Close();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
