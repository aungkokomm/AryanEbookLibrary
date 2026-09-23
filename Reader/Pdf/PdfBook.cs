using System.Runtime.InteropServices;
using System.Text;

namespace AryanEbookLibrary.Reader.Pdf;

/// <summary>One entry of a PDF's own outline, flat and in reading order. Page is -1 when it goes nowhere here.</summary>
public sealed record PdfOutlineEntry(int Depth, int Page, string Title);

/// <summary>A link on a page. The box is top-left origin with BOTH axes as fractions of the page's WIDTH.</summary>
public sealed record PdfLink(double Left, double Top, double Right, double Bottom, int TargetPage, string Uri);

public enum PdfOpenStatus { Ok, NeedsPassword, Failed }

/// <summary>
/// An open PDF, read through native\reader_core. Every method may be called from any thread: the native side
/// serializes every PDFium call. After Dispose every method answers "nothing" instead of failing, so a render
/// still in flight when the window closes just comes back empty.
/// </summary>
public sealed class PdfBook : IDisposable
{
    private ulong _handle;

    public int PageCount { get; }

    /// <summary>Every page's size in PDF points, read once when the book opens.</summary>
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; }

    private PdfBook(ulong handle, int pageCount, IReadOnlyList<(double, double)> sizes)
    {
        _handle = handle;
        PageCount = pageCount;
        PageSizes = sizes;
    }

    public static (PdfBook? Book, PdfOpenStatus Status) Open(string path, string? password = null)
    {
        PdfNative.OpenResult opened;
        try
        {
            opened = PdfNative.open_document_protected(path, string.IsNullOrEmpty(password) ? null : password);
        }
        catch (DllNotFoundException)
        {
            return (null, PdfOpenStatus.Failed);
        }
        if (opened.Status == PdfNative.StatusNeedsPassword) return (null, PdfOpenStatus.NeedsPassword);
        if (opened.Status != PdfNative.StatusOk || opened.Handle == 0) return (null, PdfOpenStatus.Failed);

        var array = PdfNative.get_page_sizes(opened.Handle);
        var sizes = new List<(double, double)>();
        try
        {
            if (array.Status == PdfNative.StatusOk && array.Sizes != IntPtr.Zero)
            {
                int size = Marshal.SizeOf<PdfNative.PageSize>();
                for (int i = 0; i < (int)array.Len; i++)
                {
                    var s = Marshal.PtrToStructure<PdfNative.PageSize>(array.Sizes + i * size);
                    sizes.Add((s.Width, s.Height));
                }
            }
        }
        finally
        {
            PdfNative.free_page_size_array(array);
        }

        if (sizes.Count == 0)
        {
            PdfNative.close_document(opened.Handle);
            return (null, PdfOpenStatus.Failed);
        }

        // A page PDFium could not measure keeps its place in the stack at the size of its neighbours.
        for (int i = 0; i < sizes.Count; i++)
        {
            if (sizes[i].Item1 > 0 && sizes[i].Item2 > 0) continue;
            var near = sizes.FirstOrDefault(s => s.Item1 > 0 && s.Item2 > 0);
            sizes[i] = near.Item1 > 0 ? near : (612, 792);
        }

        return (new PdfBook(opened.Handle, sizes.Count, sizes), PdfOpenStatus.Ok);
    }

    /// <summary>One tile of a page's pyramid as BGRA bytes (see TileGrid), or null.</summary>
    public (int Width, int Height, byte[] Bgra)? RenderTile(int page, int level, int col, int row)
    {
        var handle = _handle;
        return handle == 0 ? null : Take(PdfNative.render_tile(handle, page, level, col, row));
    }

    /// <summary>The whole page <paramref name="width"/> pixels wide as BGRA bytes, or null.</summary>
    public (int Width, int Height, byte[] Bgra)? RenderPage(int page, int width)
    {
        var handle = _handle;
        return handle == 0 ? null : Take(PdfNative.render_page_width(handle, page, width));
    }

    private static (int Width, int Height, byte[] Bgra)? Take(PdfNative.RenderResult result)
    {
        try
        {
            if (result.Status != PdfNative.StatusOk || result.Buffer == IntPtr.Zero) return null;
            var bytes = new byte[(int)result.Len];
            Marshal.Copy(result.Buffer, bytes, 0, bytes.Length);
            return (result.Width, result.Height, bytes);
        }
        finally
        {
            PdfNative.free_render_result(result);
        }
    }

    /// <summary>The page's characters with their boxes in pixels of a <paramref name="width"/>-wide page, or null.</summary>
    public PageTextLayer? ReadText(int page, int width)
    {
        var handle = _handle;
        if (handle == 0 || width <= 0) return null;
        var array = PdfNative.get_page_chars(handle, page, width);
        try
        {
            if (array.Status != PdfNative.StatusOk) return null;
            var glyphs = new List<CharGlyph>((int)array.Len);
            if (array.Chars != IntPtr.Zero)
            {
                int size = Marshal.SizeOf<PdfNative.CharInfo>();
                for (int i = 0; i < (int)array.Len; i++)
                {
                    var c = Marshal.PtrToStructure<PdfNative.CharInfo>(array.Chars + i * size);
                    // A codepoint outside the BMP does not fit one UTF-16 char: replaced, not truncated into
                    // some unrelated character.
                    var ch = c.Codepoint <= 0xFFFF ? (char)c.Codepoint : (char)0xFFFD;
                    glyphs.Add(new CharGlyph(c.Left, c.Top, c.Right, c.Bottom, ch));
                }
            }
            return new PageTextLayer(glyphs);
        }
        finally
        {
            PdfNative.free_char_info_array(array);
        }
    }

    public IReadOnlyList<PdfOutlineEntry> ReadOutline()
    {
        var bytes = Take(_handle == 0 ? default : PdfNative.get_bookmarks(_handle));
        var list = new List<PdfOutlineEntry>();
        if (bytes is null || bytes.Length < 4) return list;
        int count = BitConverter.ToInt32(bytes, 0), p = 4;
        for (int i = 0; i < count && p + 12 <= bytes.Length; i++)
        {
            int depth = BitConverter.ToInt32(bytes, p);
            int page = BitConverter.ToInt32(bytes, p + 4);
            int len = BitConverter.ToInt32(bytes, p + 8);
            if (p + 12 + len > bytes.Length) break;
            var title = Encoding.UTF8.GetString(bytes, p + 12, len).Trim();
            p += 12 + len;
            list.Add(new PdfOutlineEntry(depth, page < PageCount ? page : -1, title.Length > 0 ? title : "(untitled)"));
        }
        return list;
    }

    public IReadOnlyList<PdfLink> ReadLinks(int page)
    {
        var bytes = Take(_handle == 0 ? default : PdfNative.get_page_links(_handle, page));
        var list = new List<PdfLink>();
        if (bytes is null || bytes.Length < 4) return list;
        int count = BitConverter.ToInt32(bytes, 0), p = 4;
        for (int i = 0; i < count && p + 32 <= bytes.Length; i++)
        {
            uint kind = BitConverter.ToUInt32(bytes, p + 4);
            float left = BitConverter.ToSingle(bytes, p + 8), top = BitConverter.ToSingle(bytes, p + 12);
            float right = BitConverter.ToSingle(bytes, p + 16), bottom = BitConverter.ToSingle(bytes, p + 20);
            int target = BitConverter.ToInt32(bytes, p + 24);
            int len = BitConverter.ToInt32(bytes, p + 28);
            if (p + 32 + len > bytes.Length) break;
            var uri = Encoding.UTF8.GetString(bytes, p + 32, len);
            p += 32 + len;

            if (kind == PdfNative.LinkUri && uri.Length > 0)
                list.Add(new PdfLink(left, top, right, bottom, -1, uri));
            else if (kind == PdfNative.LinkInternal && target >= 0 && target < PageCount)
                list.Add(new PdfLink(left, top, right, bottom, target, ""));
        }
        return list;
    }

    private static byte[]? Take(PdfNative.ByteBuffer buffer)
    {
        try
        {
            if (buffer.Status != PdfNative.StatusOk || buffer.Data == IntPtr.Zero) return null;
            var bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (buffer.Data != IntPtr.Zero) PdfNative.free_byte_buffer(buffer);
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) PdfNative.close_document(handle);
    }
}
