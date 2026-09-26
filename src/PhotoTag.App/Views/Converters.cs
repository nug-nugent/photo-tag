using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PhotoTag.App.Views;

public static class Converters
{
    /// <summary>True for a count above zero, e.g. to show a list only when it has something in it.</summary>
    public static readonly IValueConverter IsPositive = new FuncValueConverter<int, bool>(n => n > 0);

    /// <summary>Semi-bold for the folder being shown.</summary>
    public static readonly IValueConverter SemiBoldIfTrue =
        new FuncValueConverter<bool, FontWeight>(on => on ? FontWeight.SemiBold : FontWeight.Normal);
}
