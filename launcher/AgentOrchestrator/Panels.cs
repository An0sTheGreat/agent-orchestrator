using System.Windows;
using System.Windows.Controls;

namespace AgentOrchestrator;

// Lays cards out left to right in at most MaxColumns columns. Each card is at least MinItemWidth and at most
// MaxItemWidth wide, so extra pane width first adds a column (up to the limit) and then leaves empty space.
public sealed class CardGridPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = Register(nameof(MinItemWidth), 300.0);
    public static readonly DependencyProperty MaxItemWidthProperty = Register(nameof(MaxItemWidth), 440.0);
    public static readonly DependencyProperty MaxColumnsProperty = Register(nameof(MaxColumns), 2);
    public static readonly DependencyProperty SpacingProperty = Register(nameof(Spacing), 10.0);
    public static readonly DependencyProperty SquareProperty = Register(nameof(Square), false);

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double MaxItemWidth { get => (double)GetValue(MaxItemWidthProperty); set => SetValue(MaxItemWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public bool Square { get => (bool)GetValue(SquareProperty); set => SetValue(SquareProperty, value); }

    private int _columns = 1;
    private double _itemWidth;

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(CardGridPanel),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsMeasure));

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MaxItemWidth : available.Width;
        _columns = Math.Clamp((int)((width + Spacing) / (MinItemWidth + Spacing)), 1, Math.Max(1, MaxColumns));
        _itemWidth = Math.Max(1, Math.Min(MaxItemWidth, (width - Spacing * (_columns - 1)) / _columns));

        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(_itemWidth, Square ? _itemWidth : double.PositiveInfinity));

        var used = Math.Min(InternalChildren.Count, _columns);
        return new Size(used == 0 ? 0 : used * _itemWidth + Spacing * (used - 1), TotalHeight());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var column = i % _columns;
            if (column == 0 && i > 0)
            {
                y += rowHeight + Spacing;
                rowHeight = 0;
            }
            var child = InternalChildren[i];
            var height = HeightOf(child);
            child.Arrange(new Rect(column * (_itemWidth + Spacing), y, _itemWidth, height));
            rowHeight = Math.Max(rowHeight, height);
        }
        return finalSize;
    }

    private double HeightOf(UIElement child) => Square ? _itemWidth : child.DesiredSize.Height;

    // Rows are as tall as their tallest card.
    private double TotalHeight()
    {
        double total = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (i % _columns == 0 && i > 0)
            {
                total += rowHeight + Spacing;
                rowHeight = 0;
            }
            rowHeight = Math.Max(rowHeight, HeightOf(InternalChildren[i]));
        }
        return total + rowHeight;
    }
}
