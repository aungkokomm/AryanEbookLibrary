using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AryanEbookLibrary.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AryanEbookLibrary;

/// <summary>
/// One Aryan per library. A second start (a double click too many) brings the open window forward instead of opening
/// another that would scan and write the same database and sidecars. Copies with their own data folders still run side
/// by side. Replaces the generated Main (DISABLE_XAML_GENERATED_MAIN) so this happens before any window exists.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main()
    {
        StartupTimes.Mark("main");
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppPaths.Init();
        var instance = AppInstance.FindOrRegisterForKey(KeyFor(AppPaths.DataDir));
        if (!instance.IsCurrent)
        {
            Log.Write("app: already open, brought forward");
            BringForward(instance);
            return 0;
        }

        StartupTimes.Mark("one copy");
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>The same library folder, however it is spelled, gives the same key; string.GetHashCode differs per process.</summary>
    private static string KeyFor(string dataDir) =>
        "Aryan-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\').ToUpperInvariant())))[..16];

    /// <summary>
    /// Hands this start to the open copy and shows its window. The wait must pump COM (CoWaitForMultipleObjects), as
    /// Microsoft's sample does: blocking the STA thread on the redirect can deadlock.
    /// </summary>
    private static void BringForward(AppInstance open)
    {
        try
        {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            var done = CreateEvent(IntPtr.Zero, true, false, null);
            _ = Task.Run(() =>
            {
                open.RedirectActivationToAsync(args).AsTask().Wait();
                SetEvent(done);
            });
            _ = CoWaitForMultipleObjects(0, 10_000, 1, [done], out _);

            var window = Process.GetProcessById((int)open.ProcessId).MainWindowHandle;
            if (window == IntPtr.Zero) return;
            if (IsIconic(window)) ShowWindow(window, 9);   // SW_RESTORE
            SetForegroundWindow(window);
        }
        catch (Exception ex)
        {
            Log.Write("app: could not bring the open window forward: " + ex.Message);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
}
