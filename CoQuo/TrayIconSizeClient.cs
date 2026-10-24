using System.Reflection;
using System.Runtime.InteropServices;

namespace CoQuo;

/// <summary>
/// Optional larger tray icons through the TrayIconSize component (TrayIconSizeClient.dll next to the exe).
/// Calls run in order on a background task: the first one may start TrayIconSizeHost.exe.
/// </summary>
internal static class TrayIconSizeClient
{
    public const int StandardSize = 16;

    [DllImport("TrayIconSizeClient.dll", ExactSpelling = true)]
    private static extern int TrayIconSize_SetIconSize(IntPtr hwnd, uint uid, IntPtr guid, uint sizeDip);

    [DllImport("TrayIconSizeClient.dll", ExactSpelling = true)]
    private static extern int TrayIconSize_RemoveIcon(IntPtr hwnd, uint uid, IntPtr guid);

    private static readonly object Gate = new();
    private static Task _queue = Task.CompletedTask;

    public static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "TrayIconSizeClient.dll"));

    /// <summary>Registers (size &gt; 16) or drops the icon's size; reports the outcome on the caller's context.</summary>
    public static void Apply(NotifyIcon icon, int size, Action<string?> completed)
    {
        // WinForms keeps the Shell_NotifyIcon identity private; read it before leaving the UI thread.
        if (!TryGetIdentity(icon, out var hwnd, out var uid))
        {
            completed("tray icon identity unavailable");
            return;
        }
        var context = SynchronizationContext.Current;
        lock (Gate)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                string? error = null;
                try
                {
                    int hr = size > StandardSize
                        ? TrayIconSize_SetIconSize(hwnd, uid, IntPtr.Zero, (uint)size)
                        : TrayIconSize_RemoveIcon(hwnd, uid, IntPtr.Zero);
                    if (hr < 0) error = Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}";
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    error = "TrayIconSize component missing";
                }
                if (context is null) completed(error);
                else context.Post(_ => completed(error), null);
            }, TaskScheduler.Default);
        }
    }

    private static bool TryGetIdentity(NotifyIcon icon, out IntPtr hwnd, out uint uid)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        hwnd = IntPtr.Zero;
        uid = 0;
        var window = typeof(NotifyIcon).GetField("_window", flags)?.GetValue(icon) as NativeWindow;
        // _id is uint on .NET 8; accept int too in case the field type changes.
        var id = typeof(NotifyIcon).GetField("_id", flags)?.GetValue(icon);
        if (window is null || window.Handle == IntPtr.Zero || id is not (uint or int)) return false;
        hwnd = window.Handle;
        uid = Convert.ToUInt32(id);
        return true;
    }
}
