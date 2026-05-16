using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Flow.Launcher.Plugin.PasteTool;

public class Main : IPlugin, IContextMenu
{
    private PluginInitContext _context = null!;
    private string _pluginDir = null!;
    private Settings _settings = null!;
    private ClipboardStore _store = null!;
    private ClipboardListener? _listener;
    private string _pasteHelperPath = null!;

    public void Init(PluginInitContext context)
    {
        _context = context;
        _pluginDir = context.CurrentPluginMetadata.PluginDirectory;
        Logger.Init(_pluginDir);
        Logger.Log("=========== plugin Init ===========");

        _settings = new Settings(_pluginDir);
        _store = new ClipboardStore(_pluginDir);
        _pasteHelperPath = Path.Combine(_pluginDir, "PasteHelper.exe");
        if (!File.Exists(_pasteHelperPath))
            Logger.Log($"WARNING: PasteHelper.exe not found at {_pasteHelperPath}");

        // One-shot cleanup on Flow Launcher startup. NOT triggered on every query.
        _store.Cleanup(_settings.KeepDays);

        _listener = new ClipboardListener(OnClipboardUpdate);
        _listener.Start();
    }

    public List<Result> Query(Query query)
    {
        var text = (query.Search ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        if (lower is "clear" or "清空")
        {
            return new List<Result> { CommandResult("清空所有剪贴板历史", "按 Enter 删除所有记录、图片与文件缓存", () =>
            {
                _store.Clear();
                _context.API.ShowMsg("Paste Tool", "已清空剪贴板历史");
            }) };
        }
        if (lower is "settings" or "setting" or "设置") return SettingsResults();
        if (lower == "status") return StatusResults();
        if (lower.StartsWith("keep ")) return KeepResults(lower);

        // No cleanup here — by design, cleanup runs only on Init and after `c keep N`.
        var records = _store.Search(text, _settings.MaxResults);
        if (records.Count == 0)
        {
            return new List<Result> { new()
            {
                Title = "暂无剪贴板历史",
                SubTitle = string.IsNullOrEmpty(text)
                    ? "正在监听剪贴板。复制文本、图片或文件后会显示在这里。"
                    : "没有匹配的剪贴板历史。",
                IcoPath = "Images\\app.png",
                Score = 10,
            } };
        }
        return records.Select((r, i) => BuildResult(r, i)).ToList();
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not ClipboardStore.Record record) return new List<Result>();
        return new List<Result>
        {
            new()
            {
                Title = "只复制到剪贴板",
                SubTitle = "把记录写入剪贴板，不发送 Ctrl+V",
                IcoPath = "Images\\copy.png",
                Action = _ => { CopyOnly(record); return true; }
            },
            new()
            {
                Title = "复制并粘贴",
                SubTitle = "写剪贴板并发送 Ctrl+V 到光标位置",
                IcoPath = "Images\\paste.png",
                Action = _ => { Paste(record); return true; }
            }
        };
    }

    // ---------- result builder ----------
    private Result BuildResult(ClipboardStore.Record record, int index)
    {
        var icon = record.Kind switch
        {
            "image" => string.IsNullOrEmpty(record.PreviewPath) ? "Images\\image.png" : record.PreviewPath,
            "files" => "Images\\file.png",
            _ => "Images\\text.png"
        };
        var kindLabel = record.Kind switch { "image" => "【Image】", "files" => "【File】", _ => "【Text】" };

        // CopyText is what Flow Launcher's built-in Ctrl+C copies. For text records this
        // is the original content; for images/files it's the path(s) as a fallback.
        string copyText = record.Kind switch
        {
            "image" => string.IsNullOrEmpty(record.ImagePath)
                ? string.Empty
                : (Path.IsPathRooted(record.ImagePath) ? record.ImagePath : Path.Combine(_pluginDir, record.ImagePath)),
            "files" => record.Files != null && record.Files.Count > 0
                ? string.Join(Environment.NewLine, record.Files)
                : string.Empty,
            _ => record.Content ?? string.Empty
        };

        return new Result
        {
            Title = $"{kindLabel} {record.Title()}",
            SubTitle = record.Subtitle(),
            IcoPath = icon,
            ContextData = record,
            CopyText = copyText,
            // Big gap so FL's score additions can't reorder. Newest = highest score.
            Score = int.MaxValue - index,
            // Stop FL from bumping frequently-paste results up — we want strict
            // newest-first ordering by created_at, not user-click frecency.
            AddSelectedCount = false,
            Action = _ => { Paste(record); return true; }
        };
    }

    // ---------- actions ----------
    /// <summary>
    /// Write the record to the clipboard, then spawn PasteHelper.exe to send Ctrl+V
    /// after a short delay. This mirrors the Python plugin's delayed_paste.py:
    /// running SendInput in a separate process is the only reliable way to ensure
    /// Flow Launcher has finished hiding and the target window has focus.
    /// </summary>
    private void Paste(ClipboardStore.Record record)
    {
        Logger.Log($"paste action: kind={record.Kind} id={record.Id}");
        if (!WriteToClipboard(record))
        {
            Logger.Log("paste aborted: clipboard write failed");
            return;
        }
        // Ask Flow Launcher to hide its main window so the previous foreground window
        // can regain focus. PasteHelper will sleep briefly before SendInput.
        try { _context.API.HideMainWindow(); } catch (Exception ex) { Logger.LogException("HideMainWindow failed", ex); }
        SpawnPasteHelper();
    }

    private void CopyOnly(ClipboardStore.Record record)
    {
        Logger.Log($"copy_only action: kind={record.Kind} id={record.Id}");
        WriteToClipboard(record);
    }

    /// <summary>
    /// Writes the record back to the system clipboard using P/Invoke (CF_UNICODETEXT,
    /// CF_DIB, or CF_HDROP) — 1:1 with the Python plugin's set_record_to_clipboard.
    /// </summary>
    private bool WriteToClipboard(ClipboardStore.Record record)
    {
        // Suppress our own clipboard listener for 800ms so this write doesn't get
        // re-captured as a new history entry.
        ClipboardListener.SuppressFor(800);

        switch (record.Kind)
        {
            case "text":
                return Win32Clipboard.WriteText(record.Content ?? string.Empty);

            case "image":
                if (string.IsNullOrEmpty(record.ImagePath)) return false;
                var imgAbs = Path.IsPathRooted(record.ImagePath)
                    ? record.ImagePath
                    : Path.Combine(_pluginDir, record.ImagePath);
                return Win32Clipboard.WriteImage(imgAbs);

            case "files":
                if (record.Files == null || record.Files.Count == 0) return false;
                // Prefer cached copy if it still exists; fall back to the original path.
                var paths = new List<string>(record.Files.Count);
                for (int i = 0; i < record.Files.Count; i++)
                {
                    string? cached = record.CachedFiles != null && i < record.CachedFiles.Count
                        ? record.CachedFiles[i]
                        : null;
                    paths.Add(!string.IsNullOrEmpty(cached) && File.Exists(cached) ? cached : record.Files[i]);
                }
                return Win32Clipboard.WriteFiles(paths);

            default:
                return false;
        }
    }

    private void SpawnPasteHelper()
    {
        try
        {
            if (!File.Exists(_pasteHelperPath))
            {
                Logger.Log($"PasteHelper.exe missing: {_pasteHelperPath}");
                return;
            }
            var psi = new ProcessStartInfo
            {
                FileName = _pasteHelperPath,
                Arguments = _settings.PasteDelayMs.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _pluginDir
            };
            var proc = Process.Start(psi);
            Logger.Log($"PasteHelper spawned pid={proc?.Id} delay_ms={_settings.PasteDelayMs}");
        }
        catch (Exception ex)
        {
            Logger.LogException("spawn PasteHelper failed", ex);
        }
    }

    // ---------- listener callback ----------
    private void OnClipboardUpdate()
    {
        var snapshot = Win32Clipboard.Read(_pluginDir, _settings.MaxCachedFileSizeMB);
        if (snapshot == null)
        {
            Logger.Log("clipboard update with no supported snapshot");
            return;
        }
        var source = Win32Clipboard.ForegroundProcessName();
        Logger.Log($"captured kind={snapshot.Kind} source={source}");
        switch (snapshot.Kind)
        {
            case Win32Clipboard.SnapshotKind.Text:
                _store.AddText(snapshot.Text ?? "", source, snapshot.Hash); break;
            case Win32Clipboard.SnapshotKind.Image:
                _store.AddImage(snapshot.ImagePath!, snapshot.PreviewPath!, source, snapshot.Hash); break;
            case Win32Clipboard.SnapshotKind.Files:
                _store.AddFiles(snapshot.Paths!, snapshot.CachedPaths, source, snapshot.Hash); break;
        }
    }

    // ---------- settings commands ----------
    private List<Result> SettingsResults() => new()
    {
        CommandResult($"当前保留时间：{_settings.KeepDays} 天", "输入 c keep N 设置任意天数（如 c keep 4 / c keep 21）", () => { }),
        CommandResult("保留 7 天",  "按 Enter 应用", () => SetKeepDays(7)),
        CommandResult("保留 14 天", "按 Enter 应用", () => SetKeepDays(14)),
        CommandResult("保留 21 天", "按 Enter 应用", () => SetKeepDays(21)),
    };

    private List<Result> KeepResults(string lower)
    {
        var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var d) && d > 0)
        {
            return new List<Result> { CommandResult($"设置保留 {d} 天", "按 Enter 应用并清理过期记录", () => SetKeepDays(d)) };
        }
        return SettingsResults();
    }

    private List<Result> StatusResults()
    {
        var stats = _store.Stats();
        var running = _listener != null;
        var helperOk = File.Exists(_pasteHelperPath);
        return new List<Result> { CommandResult(
            running ? "Listener running" : "Listener not running",
            $"records={stats.total} · latest={(string.IsNullOrEmpty(stats.latest) ? "none" : stats.latest)} · helper={(helperOk ? "ok" : "MISSING")}",
            () => { }) };
    }

    private void SetKeepDays(int days)
    {
        _settings.KeepDays = days;
        _settings.Save();
        _store.Cleanup(days);
        _context.API.ShowMsg("Paste Tool", $"已设置保留 {days} 天");
    }

    private static Result CommandResult(string title, string subtitle, Action action) => new()
    {
        Title = title,
        SubTitle = subtitle,
        IcoPath = "Images\\app.png",
        Score = 1000,
        Action = _ => { action(); return true; }
    };
}
