using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;

namespace Flow.Launcher.Plugin.PasteTool;

/// <summary>
/// 1:1 port of the Python clipboard_bridge.py logic to C# P/Invoke.
/// - Read priority: CF_HDROP > (CF_UNICODETEXT only if no CF_DIB) > CF_DIB
/// - Image write: PNG → BMP → strip 14-byte BITMAPFILEHEADER → CF_DIB
/// - Files write: build DROPFILES struct + UTF-16LE path list → CF_HDROP
/// </summary>
internal static class Win32Clipboard
{
    // ---- Win32 constants ----
    private const uint CF_TEXT = 1;
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- Foreground app name (for source_app) ----
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

    // ---- Open clipboard with retry (Python: 12 × 40ms = 480ms total) ----
    private static bool OpenWithRetry(int retries = 12, int delayMs = 40)
    {
        for (int i = 0; i < retries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(delayMs);
        }
        return false;
    }

    public enum SnapshotKind { Text, Image, Files }

    public record Snapshot(
        SnapshotKind Kind,
        string Hash,
        string? Text,
        string? ImagePath,
        string? PreviewPath,
        List<string>? Paths,
        List<string?>? CachedPaths);

    /// <summary>
    /// Read whatever is on the clipboard. Priority matches Python:
    /// 1. CF_HDROP (files)
    /// 2. CF_UNICODETEXT only if CF_DIB is NOT also present (Excel/web copy puts both — prefer image)
    /// 3. CF_DIB (image)
    /// </summary>
    public static Snapshot? Read(string pluginDir, int maxCachedFileSizeMB)
    {
        if (!OpenWithRetry())
        {
            Logger.Log("read clipboard failed: could not open");
            return null;
        }
        try
        {
            var formats = AvailableFormats();
            Logger.Log($"read clipboard available_formats=[{string.Join(",", formats)}]");

            // 1. Files
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var paths = ReadHDrop();
                if (paths.Count > 0)
                {
                    var joined = string.Join("\n", paths);
                    var hash = HashText(joined);
                    var cached = CacheFiles(paths, hash, pluginDir, maxCachedFileSizeMB);
                    return new Snapshot(SnapshotKind.Files, hash, null, null, null, paths, cached);
                }
            }

            bool hasDib = IsClipboardFormatAvailable(CF_DIB);

            // 2. Text (only if no image — mimics Python `if text and not has_dib`)
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadUnicodeText();
                if (!string.IsNullOrEmpty(text) && !hasDib)
                {
                    return new Snapshot(SnapshotKind.Text, HashText(text), text, null, null, null, null);
                }
            }

            // 3. Image
            if (hasDib)
            {
                var dibBytes = ReadGlobal(CF_DIB);
                if (dibBytes != null && dibBytes.Length > 40)
                {
                    return SaveImageFromDib(dibBytes, pluginDir);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("read clipboard exception", ex);
        }
        finally
        {
            CloseClipboard();
        }
        return null;
    }

    private static List<uint> AvailableFormats()
    {
        var list = new List<uint>();
        uint cur = 0;
        while ((cur = EnumClipboardFormats(cur)) != 0) list.Add(cur);
        return list;
    }

    private static string ReadUnicodeText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return string.Empty;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUni(ptr) ?? string.Empty; }
        finally { GlobalUnlock(handle); }
    }

    private static List<string> ReadHDrop()
    {
        var result = new List<string>();
        var handle = GetClipboardData(CF_HDROP);
        if (handle == IntPtr.Zero) return result;
        uint count = DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            uint len = DragQueryFileW(handle, i, null, 0);
            var sb = new StringBuilder((int)len + 1);
            DragQueryFileW(handle, i, sb, len + 1);
            result.Add(sb.ToString());
        }
        return result;
    }

    private static byte[]? ReadGlobal(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = GlobalSize(handle);
        if (size == UIntPtr.Zero) return null;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            var buf = new byte[(int)size];
            Marshal.Copy(ptr, buf, 0, buf.Length);
            return buf;
        }
        finally { GlobalUnlock(handle); }
    }

    // ---- Image: DIB → PNG file on disk (mimics Python's ImageGrab + save PNG) ----
    private static Snapshot? SaveImageFromDib(byte[] dib, string pluginDir)
    {
        // Reconstruct BMP file = 14-byte BITMAPFILEHEADER + DIB (header + pixels)
        // BITMAPINFOHEADER size at offset 0 of DIB tells us where pixels start.
        var infoHeaderSize = BitConverter.ToInt32(dib, 0);
        // Compute pixel offset for the BMP file header
        int colorCount = BitConverter.ToInt32(dib, 32);          // biClrUsed
        ushort bitCount = BitConverter.ToUInt16(dib, 14);        // biBitCount
        int paletteSize;
        if (bitCount <= 8)
        {
            int count = colorCount == 0 ? (1 << bitCount) : colorCount;
            paletteSize = count * 4;
        }
        else
        {
            // For 16/24/32-bit, masks may follow header (BI_BITFIELDS) — colorCount tells us
            paletteSize = colorCount * 4;
        }
        int pixelOffset = 14 + infoHeaderSize + paletteSize;
        int fileSize = 14 + dib.Length;

        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        // bmp[6..10] = reserved (0)
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);

        try
        {
            using var ms = new MemoryStream(bmp);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // Hash the PNG bytes for stable dedup
            using var pngMs = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            enc.Save(pngMs);
            var pngBytes = pngMs.ToArray();
            var hash = HashBytes(pngBytes);

            var imagesDir = Path.Combine(pluginDir, "Data", "images");
            Directory.CreateDirectory(imagesDir);
            var relImage = Path.Combine("Data", "images", $"{hash}.png");
            var relPreview = Path.Combine("Data", "images", $"{hash}_thumb.png");
            var imagePath = Path.Combine(pluginDir, relImage);
            var previewPath = Path.Combine(pluginDir, relPreview);
            if (!File.Exists(imagePath))
            {
                File.WriteAllBytes(imagePath, pngBytes);
            }
            if (!File.Exists(previewPath))
            {
                SaveThumbnail(frame, previewPath, 128);
            }
            return new Snapshot(SnapshotKind.Image, hash, null, relImage, relPreview, null, null);
        }
        catch (Exception ex)
        {
            Logger.LogException("save image from DIB failed", ex);
            return null;
        }
    }

    private static void SaveThumbnail(BitmapSource src, string path, int maxSize)
    {
        var scale = Math.Min((double)maxSize / src.PixelWidth, (double)maxSize / src.PixelHeight);
        BitmapSource toSave = scale < 1.0
            ? new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale))
            : src;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(toSave));
        using var fs = new FileStream(path, FileMode.Create);
        enc.Save(fs);
    }

    // ============================================================
    //                         WRITE
    // ============================================================

    public static bool WriteText(string text)
    {
        return WriteHGlobal(CF_UNICODETEXT, TextToBytes(text));
    }

    public static bool WriteImage(string pngAbsPath)
    {
        if (!File.Exists(pngAbsPath)) return false;
        try
        {
            // Decode PNG → re-encode as BMP → strip 14-byte file header → CF_DIB
            using var fs = new FileStream(pngAbsPath, FileMode.Open, FileAccess.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // Convert to BGR24 (no alpha) so older apps can paste cleanly — matches Python's convert("RGB")
            var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr24, null, 0);

            using var ms = new MemoryStream();
            var enc = new BmpBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(converted));
            enc.Save(ms);
            var bmp = ms.ToArray();
            if (bmp.Length <= 14) return false;
            var dib = new byte[bmp.Length - 14];
            Buffer.BlockCopy(bmp, 14, dib, 0, dib.Length);
            return WriteHGlobal(CF_DIB, dib);
        }
        catch (Exception ex)
        {
            Logger.LogException($"WriteImage failed path={pngAbsPath}", ex);
            return false;
        }
    }

    public static bool WriteFiles(IEnumerable<string> paths)
    {
        var bytes = BuildHDrop(paths);
        return WriteHGlobal(CF_HDROP, bytes);
    }

    private static byte[] BuildHDrop(IEnumerable<string> paths)
    {
        // DROPFILES struct (20 bytes) + UTF-16LE path list, paths separated by \0, terminated by \0\0
        var joined = string.Join("\0", paths) + "\0\0";
        var encoded = Encoding.Unicode.GetBytes(joined);
        var result = new byte[20 + encoded.Length];
        BitConverter.GetBytes((uint)20).CopyTo(result, 0);   // pFiles offset
        // pt.x=0 pt.y=0 fNC=0 at offsets 4..16 (already zero)
        BitConverter.GetBytes((uint)1).CopyTo(result, 16);   // fWide = 1
        Buffer.BlockCopy(encoded, 0, result, 20, encoded.Length);
        return result;
    }

    private static byte[] TextToBytes(string text)
    {
        return Encoding.Unicode.GetBytes(text + "\0");
    }

    private static bool WriteHGlobal(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return false;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) { GlobalFree(handle); return false; }
        try { Marshal.Copy(data, 0, ptr, data.Length); }
        finally { GlobalUnlock(handle); }

        if (!OpenWithRetry()) { GlobalFree(handle); return false; }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(format, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            // Ownership transferred to clipboard — do NOT GlobalFree
            return true;
        }
        finally { CloseClipboard(); }
    }

    // ---- File caching (preserves cached file bytes so paste still works if source is moved) ----
    private static List<string?> CacheFiles(List<string> originals, string hash, string pluginDir, int maxCachedFileSizeMB)
    {
        var maxBytes = (long)maxCachedFileSizeMB * 1024 * 1024;
        var cacheDir = Path.Combine(pluginDir, "Data", "files", hash);
        var result = new List<string?>(originals.Count);
        try { Directory.CreateDirectory(cacheDir); }
        catch (Exception ex)
        {
            Logger.LogException($"create cache dir failed {cacheDir}", ex);
            for (int i = 0; i < originals.Count; i++) result.Add(null);
            return result;
        }

        for (int i = 0; i < originals.Count; i++)
        {
            string orig = originals[i];
            try
            {
                if (!File.Exists(orig)) { result.Add(null); continue; }
                var info = new FileInfo(orig);
                if (info.Length > maxBytes)
                {
                    Logger.Log($"skip caching large file size={info.Length} path={orig}");
                    result.Add(null);
                    continue;
                }
                var dest = Path.Combine(cacheDir, $"{i:000}_{Path.GetFileName(orig)}");
                if (!File.Exists(dest)) File.Copy(orig, dest, overwrite: false);
                result.Add(dest);
            }
            catch (Exception ex)
            {
                Logger.LogException($"cache file failed src={orig}", ex);
                result.Add(null);
            }
        }
        return result;
    }

    private static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));
    private static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
