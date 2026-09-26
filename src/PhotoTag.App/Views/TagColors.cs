using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace PhotoTag.App.Views;

/// <summary>
/// Each tag gets its own hue, the same everywhere (chip, suggestion, the dots on a tile), so tags can
/// be told apart at a glance. Bound with the theme as the second value, so colours follow light and dark.
/// </summary>
public sealed class TagColors : IMultiValueConverter
{
    public static TagColors Instance { get; } = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [string tag, ..]) return null;
        var dark = values is [_, ThemeVariant theme, ..] && theme == ThemeVariant.Dark;
        return new SolidColorBrush(For(tag, parameter as string ?? "Dot", dark));
    }

    /// <summary>A stable hue: string.GetHashCode differs between runs.</summary>
    internal static double Hue(string tag)
    {
        var sum = 0;
        foreach (var c in tag.ToLowerInvariant()) sum = unchecked(sum * 31 + c);
        return (sum & 0x7fffffff) % 360;
    }

    internal static Color For(string tag, string part, bool dark)
    {
        var hue = Hue(tag);
        var (s, l) = (part, dark) switch
        {
            ("Background", false) => (0.55, 0.90),
            ("Foreground", false) => (0.60, 0.28),
            ("Background", true) => (0.30, 0.27),
            ("Foreground", true) => (0.65, 0.86),
            (_, false) => (0.62, 0.50),
            (_, true) => (0.70, 0.64),
        };
        return new HslColor(1, hue, s, l).ToRgb();
    }
}
