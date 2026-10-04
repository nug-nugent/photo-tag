using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace PhotoTag.App.Views;

/// <summary>
/// A click anywhere in a box that holds a text box (the tag and people boxes: chips, then room to type) focuses
/// that text box, not just a click on the text box itself. Clicks the chips' own buttons handle are left alone.
/// </summary>
public static class ClickToFocus
{
    public static readonly AttachedProperty<InputElement?> TargetProperty =
        AvaloniaProperty.RegisterAttached<Control, InputElement?>("Target", typeof(ClickToFocus));

    static ClickToFocus() => TargetProperty.Changed.AddClassHandler<Control>((control, e) =>
    {
        control.PointerPressed -= OnPointerPressed;
        if (e.NewValue is not null) control.PointerPressed += OnPointerPressed;
    });

    public static InputElement? GetTarget(Control control) => control.GetValue(TargetProperty);

    public static void SetTarget(Control control, InputElement? value) => control.SetValue(TargetProperty, value);

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || sender is not Control control || GetTarget(control) is not { } target) return;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed || !target.IsEffectivelyEnabled) return;
        if (target.IsKeyboardFocusWithin) return;
        target.Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }
}
