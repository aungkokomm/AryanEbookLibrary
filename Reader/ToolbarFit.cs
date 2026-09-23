using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace AryanEbookLibrary.Reader;

/// <summary>
/// Fits a reader's toolbar to the width it has by putting its least-needed parts away, in order, until what is left
/// fits. Measured, not guessed from widths: Windows' own text size setting makes every label wider, and a toolbar
/// too wide for its window is laid out at its own width and cut, so its own size never says it is too wide.
/// </summary>
internal static class ToolbarFit
{
    /// <param name="reset">Shows everything at its full size.</param>
    /// <param name="steps">Each makes the toolbar narrower, least needed first.</param>
    public static void Fit(FrameworkElement bar, double width, Action reset, params Action[] steps)
    {
        reset();
        foreach (var step in steps)
        {
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (bar.DesiredSize.Width <= width) return;
            step();
        }
    }
}
