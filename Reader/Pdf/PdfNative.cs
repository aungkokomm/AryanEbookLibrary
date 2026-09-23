using System.Runtime.InteropServices;

namespace AryanEbookLibrary.Reader.Pdf;

/// <summary>
/// The C functions of native\reader_core (reader_core.dll beside the exe, which loads pdfium.dll from the same
/// folder). The same signatures as Ayaan PDF's render_core for every function that came across.
/// </summary>
internal static class PdfNative
{
    private const string Dll = "reader_core.dll";

    public const int StatusOk = 0;
    public const int StatusInvalidInput = 1;
    public const int StatusPanic = 2;
    public const int StatusNeedsPassword = 5;

    public const uint LinkUri = 0;
    public const uint LinkInternal = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenResult
    {
        public ulong Handle;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderResult
    {
        public int Width;
        public int Height;
        public IntPtr Buffer;
        public UIntPtr Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PageSize
    {
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PageSizeArray
    {
        public IntPtr Sizes;
        public UIntPtr Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CharInfo
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
        public uint Codepoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CharInfoArray
    {
        public IntPtr Chars;
        public UIntPtr Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByteBuffer
    {
        public IntPtr Data;
        public UIntPtr Len;
        public int Status;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern OpenResult open_document_protected(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void close_document(ulong docHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int get_page_count(ulong docHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern PageSizeArray get_page_sizes(ulong docHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_page_size_array(PageSizeArray array);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RenderResult render_tile(ulong docHandle, int pageIndex, int level, int col, int row);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern RenderResult render_page_width(ulong docHandle, int pageIndex, int width);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_render_result(RenderResult result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern CharInfoArray get_page_chars(ulong docHandle, int pageIndex, int targetWidth);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_char_info_array(CharInfoArray array);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_bookmarks(ulong docHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ByteBuffer get_page_links(ulong docHandle, int pageIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free_byte_buffer(ByteBuffer buffer);
}
