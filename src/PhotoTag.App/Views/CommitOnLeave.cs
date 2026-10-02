using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PhotoTag.App.Views;

/// <summary>
/// Leaving a tag or person box (Tab, or a click elsewhere) runs its <c>Command</c>, as Enter does, so what's been
/// typed is added rather than left waiting. Moving between the box's own text and its suggestions isn't leaving it.
/// </summary>
public static class CommitOnLeave
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<AutoCompleteBox, ICommand?>("Command", typeof(CommitOnLeave));

    static CommitOnLeave() => CommandProperty.Changed.AddClassHandler<AutoCompleteBox>((box, e) =>
    {
        box.LostFocus -= OnLostFocus;
        if (e.NewValue is not null) box.LostFocus += OnLostFocus;
    });

    public static ICommand? GetCommand(AutoCompleteBox box) => box.GetValue(CommandProperty);

    public static void SetCommand(AutoCompleteBox box, ICommand? value) => box.SetValue(CommandProperty, value);

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not AutoCompleteBox box) return;
        // The command of the photo the text was typed for, before anything else changes. It reads what was typed
        // from that photo's panel, and does nothing if it's empty (or already added).
        var command = GetCommand(box);
        // Once focus has settled: a click on a suggestion moves it into the list and back.
        Dispatcher.UIThread.Post(() =>
        {
            if (box.IsKeyboardFocusWithin || box.IsDropDownOpen) return;
            if (command?.CanExecute(null) == true) command.Execute(null);
        });
    }
}
