using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Flow.Launcher.Plugin.PasteTool;

/// <summary>
/// Listens for clipboard updates via AddClipboardFormatListener on a hidden
/// message-only window. Event-driven, zero polling.
///
/// Self-capture suppression: when the plugin itself writes to the clipboard
/// (paste / copy_only actions), <see cref="SuppressFor"/> is called with a
/// short window; any WM_CLIPBOARDUPDATE within that window is ignored to
/// prevent re-recording our own writes as new history entries.
/// </summary>
internal class ClipboardListener : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private HwndSource? _source;
    private readonly Action _onUpdate;
    private static long _suppressUntilTicks;

    public static void SuppressFor(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        if (deadline > _suppressUntilTicks) _suppressUntilTicks = deadline;
    }

    public static bool ShouldSuppress() => Environment.TickCount64 < _suppressUntilTicks;

    public ClipboardListener(Action onUpdate)
    {
        _onUpdate = onUpdate;
    }

    public void Start()
    {
        if (Application.Current?.Dispatcher == null)
        {
            Logger.Log("ClipboardListener: no application dispatcher");
            return;
        }
        Application.Current.Dispatcher.Invoke(() =>
        {
            var p = new HwndSourceParameters("PasteToolClipboardListener")
            {
                ParentWindow = HWND_MESSAGE,
                WindowStyle = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
            if (!AddClipboardFormatListener(_source.Handle))
                Logger.Log($"AddClipboardFormatListener failed err={Marshal.GetLastWin32Error()}");
            else
                Logger.Log($"ClipboardListener registered hwnd={_source.Handle}");
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            if (ShouldSuppress())
            {
                Logger.Log("clipboard update suppressed (self-write)");
                return IntPtr.Zero;
            }
            try { _onUpdate(); }
            catch (Exception ex) { Logger.LogException("clipboard update handler failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source == null) return;
        try
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch { }
        _source = null;
    }
}
