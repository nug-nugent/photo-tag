using System.Collections.Specialized;
using Avalonia;
using Avalonia.Layout;
using PhotoTag.App.ViewModels;

namespace PhotoTag.App.Views;

/// <summary>
/// The photo grid's layout: square tiles with a caption underneath, a full-width heading before each
/// day when grouped, and featured tiles (highlighted favourites) at double size, packed densely like a
/// CSS grid with <c>grid-auto-flow: dense</c>. Positions are worked out for every item (cheap
/// arithmetic, even for thousands), but only tiles near the viewport are created: folders can hold
/// thousands of photos.
/// </summary>
public sealed class PhotoGridLayout : VirtualizingLayout
{
    private int _version;
    private State? _last;

    public double MinItemWidth { get; set; } = 148;
    public double ColumnSpacing { get; set; } = 12;
    public double RowSpacing { get; set; } = 14;

    /// <summary>Room under each tile for the file name.</summary>
    public double CaptionHeight { get; set; } = 24;

    public double HeaderHeight { get; set; } = 60;

    /// <summary>Extra space above each day's heading, after the first.</summary>
    public double SectionSpacing { get; set; } = 12;

    private sealed class State
    {
        public Rect[] Rects = [];
        public bool[] IsHeader = [];
        public double Width = double.NaN;
        public int Count = -1;
        public int Version = -1;
        public double Height;
        public Dictionary<int, Layoutable> Realized = [];
    }

    /// <summary>Tiles changed size (a favourite was featured or unfeatured): lay out again.</summary>
    public void Invalidate()
    {
        _version++;
        InvalidateMeasure();
    }

    /// <summary>Where an item is, in the grid's coordinates, as of the last layout.</summary>
    public Rect? GetRect(int index) => _last is { } s && index >= 0 && index < s.Rects.Length ? s.Rects[index] : null;

    /// <summary>
    /// The tile above or below <paramref name="index"/> (the nearest in the next row that has a tile
    /// there, across day headings), for ↑ and ↓. Null at the top or bottom.
    /// </summary>
    public int? FindVertical(int index, bool down)
    {
        if (_last is not { } s || index < 0 || index >= s.Rects.Length) return null;
        var from = s.Rects[index];
        var centre = from.Center.X;
        int? best = null;
        double bestRow = 0, bestDistance = 0;
        for (var i = 0; i < s.Rects.Length; i++)
        {
            if (s.IsHeader[i] || i == index) continue;
            var r = s.Rects[i];
            var ahead = down ? r.Top >= from.Bottom - 1 : r.Bottom <= from.Top + 1;
            if (!ahead) continue;
            var row = down ? r.Top : -r.Bottom;
            var distance = centre >= r.Left && centre <= r.Right ? 0 : Math.Min(Math.Abs(centre - r.Left), Math.Abs(centre - r.Right));
            if (best is null || row < bestRow - 1 || (Math.Abs(row - bestRow) <= 1 && distance < bestDistance))
            {
                best = i;
                bestRow = row;
                bestDistance = distance;
            }
        }
        return best;
    }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context) => context.LayoutState = new State();

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        if (context.LayoutState == _last) _last = null;
        context.LayoutState = null;
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object? source, NotifyCollectionChangedEventArgs args)
    {
        // The grid replaces its items wholesale, and the repeater clears every element on a reset,
        // so there's nothing left to recycle.
        if (context.LayoutState is State state)
        {
            state.Realized.Clear();
            state.Count = -1;
        }
        base.OnItemsChangedCore(context, source, args);
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.LayoutState is not State state) context.LayoutState = state = new State();
        _last = state;
        var width = double.IsInfinity(availableSize.Width) ? MinItemWidth : availableSize.Width;
        if (state.Width != width || state.Count != context.ItemCount || state.Version != _version) Compute(context, state, width);

        var visible = context.RealizationRect;
        var realized = new Dictionary<int, Layoutable>();
        for (var i = 0; i < state.Rects.Length; i++)
        {
            var rect = state.Rects[i];
            if (rect.Bottom < visible.Top || rect.Top > visible.Bottom) continue;
            var element = context.GetOrCreateElementAt(i);
            element.Measure(rect.Size);
            realized[i] = element;
        }
        foreach (var (index, element) in state.Realized)
            if (!realized.ContainsKey(index)) context.RecycleElement(element);
        state.Realized = realized;

        return new Size(width, state.Height);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is State state)
            foreach (var (index, element) in state.Realized)
                element.Arrange(state.Rects[index]);
        return finalSize;
    }

    private void Compute(VirtualizingLayoutContext context, State state, double width)
    {
        var count = context.ItemCount;
        var columns = Math.Max(1, (int)((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing)));
        var cell = Math.Max(1, (width - ColumnSpacing * (columns - 1)) / columns);
        var tileHeight = cell + CaptionHeight;

        var rects = new Rect[count];
        var isHeader = new bool[count];
        var occupied = new List<bool[]>(); // per row of the current day: which columns are taken
        var firstFree = 0;
        var top = 0.0;

        for (var i = 0; i < count; i++)
        {
            var item = context.GetItemAt(i);
            if (item is DayHeaderViewModel)
            {
                var headerTop = i == 0 ? 0 : SectionBottom() + SectionSpacing;
                rects[i] = new Rect(0, headerTop, width, HeaderHeight);
                isHeader[i] = true;
                top = headerTop + HeaderHeight;
                occupied.Clear();
                firstFree = 0;
                continue;
            }

            var span = item is PhotoItemViewModel { IsFeatured: true } && columns >= 2 ? 2 : 1;
            var (row, column) = Place(occupied, ref firstFree, columns, span);
            rects[i] = new Rect(
                column * (cell + ColumnSpacing),
                top + row * (tileHeight + RowSpacing),
                span * cell + (span - 1) * ColumnSpacing,
                span * tileHeight + (span - 1) * RowSpacing);
        }

        state.Rects = rects;
        state.IsHeader = isHeader;
        state.Height = SectionBottom();
        state.Width = width;
        state.Count = count;
        state.Version = _version;

        double SectionBottom() => occupied.Count == 0 ? top : top + occupied.Count * (tileHeight + RowSpacing) - RowSpacing;
    }

    /// <summary>The first free spot for a <paramref name="span"/>×<paramref name="span"/> tile, scanning from the first row with a gap.</summary>
    private static (int Row, int Column) Place(List<bool[]> occupied, ref int firstFree, int columns, int span)
    {
        for (var row = firstFree; ; row++)
        {
            while (occupied.Count < row + span) occupied.Add(new bool[columns]);
            for (var column = 0; column + span <= columns; column++)
            {
                if (!Fits(row, column)) continue;
                for (var r = row; r < row + span; r++)
                for (var c = column; c < column + span; c++)
                    occupied[r][c] = true;
                while (firstFree < occupied.Count && Array.TrueForAll(occupied[firstFree], taken => taken)) firstFree++;
                return (row, column);
            }
        }

        bool Fits(int row, int column)
        {
            for (var r = row; r < row + span; r++)
            for (var c = column; c < column + span; c++)
                if (occupied[r][c]) return false;
            return true;
        }
    }
}
