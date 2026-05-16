using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.PasteTool;

internal static class ClipboardHelper
{
    public enum SnapshotKind { Text, Image, Files }

    public record Snapshot(SnapshotKind Kind, string Hash, string? Text, string? ImagePath, string? PreviewPath, List<string>? Paths);

    public static Snapshot? Read(string pluginDir)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count > 0)
                {
                    var joined = string.Join("\n", files);
                    return new Snapshot(SnapshotKind.Files, HashText(joined), null, null, null, files);
                }
            }

            // Prefer text over image — but only if image is NOT present (matches old behavior)
            var hasImage = Clipboard.ContainsImage();
            if (Clipboard.ContainsText() && !hasImage)
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    return new Snapshot(SnapshotKind.Text, HashText(text), text, null, null, null);
                }
            }

            if (hasImage)
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    var imagesDir = Path.Combine(pluginDir, "Data", "images");
                    Directory.CreateDirectory(imagesDir);

                    var hash = HashBitmap(img);
                    var relImage = Path.Combine("Data", "images", $"{hash}.png");
                    var relPreview = Path.Combine("Data", "images", $"{hash}_thumb.png");
                    var imagePath = Path.Combine(pluginDir, relImage);
                    var previewPath = Path.Combine(pluginDir, relPreview);

                    if (!File.Exists(imagePath))
                    {
                        SaveBitmap(img, imagePath);
                    }
                    if (!File.Exists(previewPath))
                    {
                        SaveBitmap(img, previewPath, 128);
                    }
                    return new Snapshot(SnapshotKind.Image, hash, null, relImage, relPreview, null);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("read clipboard failed", ex);
        }
        return null;
    }

    public static bool Write(ClipboardStore.Record record, string pluginDir)
    {
        try
        {
            switch (record.Kind)
            {
                case "text":
                    Clipboard.SetText(record.Content ?? string.Empty);
                    return true;
                case "image":
                    var path = record.ImagePath;
                    if (string.IsNullOrEmpty(path)) return false;
                    var abs = Path.IsPathRooted(path) ? path : Path.Combine(pluginDir, path);
                    if (!File.Exists(abs)) return false;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(abs);
                    bmp.EndInit();
                    bmp.Freeze();
                    Clipboard.SetImage(bmp);
                    return true;
                case "files":
                    if (record.Files == null || record.Files.Count == 0) return false;
                    var collection = new System.Collections.Specialized.StringCollection();
                    foreach (var f in record.Files) collection.Add(f);
                    Clipboard.SetFileDropList(collection);
                    return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException($"write clipboard failed kind={record.Kind}", ex);
        }
        return false;
    }

    public static void PasteRecord(ClipboardStore.Record record, string pluginDir)
    {
        if (!Write(record, pluginDir))
        {
            Logger.Log("paste skipped because clipboard write failed");
            return;
        }
        // Delay slightly so Flow Launcher has time to hide and refocus the previous window
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(80);
            try { SendCtrlV(); }
            catch (Exception ex) { Logger.LogException("SendInput failed", ex); }
        });
    }

    public static string ForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch { return string.Empty; }
    }

    private static void SaveBitmap(BitmapSource src, string path, int maxSize = 0)
    {
        BitmapSource toSave = src;
        if (maxSize > 0)
        {
            var scale = Math.Min((double)maxSize / src.PixelWidth, (double)maxSize / src.PixelHeight);
            if (scale < 1.0)
            {
                toSave = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
            }
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(toSave));
        using var fs = new FileStream(path, FileMode.Create);
        encoder.Save(fs);
    }

    private static string HashText(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string HashBitmap(BitmapSource src)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        encoder.Save(ms);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(ms.ToArray())).ToLowerInvariant();
    }

    // ---- SendInput ----
    private const int INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        inputs[0] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL } };
        inputs[1] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V } };
        inputs[2] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } };
        inputs[3] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } };
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }
}
