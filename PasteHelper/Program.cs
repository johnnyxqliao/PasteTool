using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PasteHelper
{
    /// <summary>
    /// Mimics the Python plugin's delayed_paste.py: a tiny external process that sleeps
    /// for a short delay (giving Flow Launcher time to hide and the target window to
    /// regain keyboard focus) and then sends Ctrl+V via SendInput.
    ///
    /// Argv:
    ///   [0] delay_ms (optional, default 80)
    /// </summary>
    internal static class Program
    {
        private const uint INPUT_KEYBOARD = 1;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

        [STAThread]
        private static int Main(string[] args)
        {
            int delayMs = 80;
            if (args.Length > 0 && int.TryParse(args[0], out var d) && d >= 0) delayMs = d;
            Thread.Sleep(delayMs);

            var inputs = new INPUT[4];
            inputs[0] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL } };
            inputs[1] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V } };
            inputs[2] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } };
            inputs[3] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } };

            uint sent = SendInput(4, inputs, Marshal.SizeOf<INPUT>());
            if (sent == 0)
            {
                keybd_event((byte)VK_CONTROL, 0, 0, IntPtr.Zero);
                keybd_event((byte)VK_V, 0, 0, IntPtr.Zero);
                keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
                keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            }
            return 0;
        }
    }
}
