using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AryanEbookLibrary.Helpers;

/// <summary>
/// Lays its children out in a row and starts a new row when the next one does not fit, so a row of chips or
/// buttons never runs off the side of a narrow window. WinUI 3 has no wrap panel of its own.
/// </summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, rowHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) continue;   // collapsed
            if (x > 0 && x + size.Width > availableSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }
            x += size.Width + HorizontalSpacing;
            width = Math.Max(width, x - HorizontalSpacing);
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        var row = new List<UIElement>();
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                PlaceRow(row, y, rowHeight);
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
                row.Clear();
            }
            row.Add(child);
            x += size.Width + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        PlaceRow(row, y, rowHeight);
        return finalSize;
    }

    /// <summary>Each child of a row is centred on the row's height, so a short chip lines up with a tall button.</summary>
    private void PlaceRow(List<UIElement> row, double y, double height)
    {
        double x = 0;
        foreach (var child in row)
        {
            var size = child.DesiredSize;
            child.Arrange(new Rect(x, y + (height - size.Height) / 2, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
        }
    }
}
