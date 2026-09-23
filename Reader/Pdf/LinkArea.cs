using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace AryanEbookLibrary.Reader.Pdf;

/// <summary>The clickable box of a link on a page, with the hand pointer. A new cursor for each: WinUI must never be
/// given one InputCursor instance twice (Ayaan PDF's lesson).</summary>
internal sealed partial class LinkArea : Grid
{
    public LinkArea() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
}
