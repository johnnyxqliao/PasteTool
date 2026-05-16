using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Flow.Launcher.Plugin.PasteTool;

/// <summary>
/// Listens for Windows clipboard updates via AddClipboardFormatListener and a hidden HwndSource.
/// Event-driven — no polling.
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
    private readonly Action _onClipboardUpdate;

    public ClipboardListener(Action onClipboardUpdate)
    {
        _onClipboardUpdate = onClipboardUpdate;
    }

    public void Start()
    {
        // Must run on a UI thread that pumps messages
        if (Application.Current?.Dispatcher == null)
        {
            Logger.Log("ClipboardListener: no application dispatcher available");
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            var parameters = new HwndSourceParameters("PasteToolClipboardListener")
            {
                ParentWindow = HWND_MESSAGE, // message-only window
                WindowStyle = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            if (!AddClipboardFormatListener(_source.Handle))
            {
                Logger.Log($"AddClipboardFormatListener failed err={Marshal.GetLastWin32Error()}");
            }
            else
            {
                Logger.Log($"ClipboardListener registered hwnd={_source.Handle}");
            }
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try { _onClipboardUpdate(); }
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
