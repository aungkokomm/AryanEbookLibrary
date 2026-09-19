using System.Runtime.InteropServices;

namespace AryanEbookLibrary.Services;

/// <summary>
/// Zero-poll drive connect/disconnect notifications via WM_DEVICECHANGE (same as CineLibrary).
/// Windows already sends WM_DEVICECHANGE to the main window whenever a volume is added or removed, so
/// nothing has to wake the disks on a timer. The HWND is subclassed with SetWindowSubclass (comctl32),
/// so the existing WndProc stays in place. The callback runs on the UI thread.
/// </summary>
public sealed class DeviceChangeWatcher : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    private delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // Keep the delegate alive: if it were collected while Windows still holds the subclass,
    // the next WM_DEVICECHANGE would crash the app.
    private readonly SUBCLASSPROC _proc;
    private readonly IntPtr _hwnd;
    private readonly UIntPtr _id = (UIntPtr)0xA7A4B00C;
    private readonly Action _onChange;
    private bool _disposed;

    public DeviceChangeWatcher(IntPtr hwnd, Action onDeviceChange)
    {
        _hwnd = hwnd;
        _onChange = onDeviceChange;
        _proc = SubclassProc;
        SetWindowSubclass(hwnd, _proc, _id, UIntPtr.Zero);
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_DEVICECHANGE)
        {
            var evt = wParam.ToInt32();
            if ((evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE) && lParam != IntPtr.Zero)
            {
                var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                if (hdr.dbch_devicetype == DBT_DEVTYP_VOLUME)
                {
                    try { _onChange(); } catch { /* a notification must never crash the WndProc */ }
                }
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { RemoveWindowSubclass(_hwnd, _proc, _id); } catch { }
    }
}
