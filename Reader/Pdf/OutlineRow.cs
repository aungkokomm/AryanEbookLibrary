using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

namespace AryanEbookLibrary.Reader.Pdf;

/// <summary>One line of the contents pane: an outline entry, indented by its depth.</summary>
public sealed class OutlineRow
{
    public OutlineRow(PdfOutlineEntry entry)
    {
        Title = entry.Title;
        Page = entry.Page;
        Indent = new Thickness(Math.Min(entry.Depth, 6) * 14, 0, 0, 0);
        Weight = entry.Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }

    public string Title { get; }
    public int Page { get; }
    public Thickness Indent { get; }
    public Windows.UI.Text.FontWeight Weight { get; }
    public string PageText => Page >= 0 ? (Page + 1).ToString("N0") : "";
}
