using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Flow.Launcher.Plugin.PasteTool;

public class Main : IPlugin, IContextMenu
{
    private PluginInitContext _context = null!;
    private string _pluginDir = null!;
    private Settings _settings = null!;
    private ClipboardStore _store = null!;
    private ClipboardListener? _listener;
    private long _lastCaptureTicks;

    public void Init(PluginInitContext context)
    {
        _context = context;
        _pluginDir = context.CurrentPluginMetadata.PluginDirectory;
        Logger.Init(_pluginDir);
        Logger.Log("plugin Init");

        _settings = new Settings(_pluginDir);
        _store = new ClipboardStore(_pluginDir);
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
            return new List<Result> { CommandResult("清空所有剪贴板历史", "按 Enter 删除文本记录、图片缓存和文件记录", () =>
            {
                _store.Clear();
                _context.API.ShowMsg("Paste Tool", "已清空剪贴板历史");
            }) };
        }

        if (lower is "settings" or "setting" or "设置") return SettingsResults();
        if (lower == "status") return StatusResults();
        if (lower.StartsWith("keep ")) return KeepResults(lower);

        _store.Cleanup(_settings.KeepDays);
        var records = _store.Search(text, _settings.MaxResults);
        if (records.Count == 0)
        {
            return new List<Result> { new()
            {
                Title = "暂无剪贴板历史",
                SubTitle = string.IsNullOrEmpty(text) ? "正在记录剪贴板。复制文本、图片或文件后会显示在这里。" : "没有匹配的剪贴板历史。",
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
                SubTitle = "不执行粘贴动作",
                IcoPath = "Images\\copy.png",
                Action = _ =>
                {
                    ClipboardHelper.Write(record, _pluginDir);
                    return true;
                }
            },
            new()
            {
                Title = "复制并粘贴",
                SubTitle = "恢复这条记录并发送 Ctrl+V",
                IcoPath = "Images\\paste.png",
                Action = _ =>
                {
                    ClipboardHelper.PasteRecord(record, _pluginDir);
                    return true;
                }
            }
        };
    }

    private Result BuildResult(ClipboardStore.Record record, int index)
    {
        var icon = record.Kind switch
        {
            "image" => string.IsNullOrEmpty(record.PreviewPath) ? "Images\\image.png" : record.PreviewPath,
            "files" => "Images\\file.png",
            _ => "Images\\text.png"
        };
        var kindLabel = record.Kind switch { "image" => "【Image】", "files" => "【File】", _ => "【Text】" };
        return new Result
        {
            Title = $"{kindLabel} {record.Title()}",
            SubTitle = record.Subtitle(),
            IcoPath = icon,
            ContextData = record,
            Score = Math.Max(1, 1000 - index),
            Action = _ =>
            {
                ClipboardHelper.PasteRecord(record, _pluginDir);
                return true;
            }
        };
    }

    private void OnClipboardUpdate()
    {
        // Debounce — listener can fire multiple times for one logical change
        var now = Environment.TickCount64;
        if (now - _lastCaptureTicks < 50) return;
        _lastCaptureTicks = now;

        var snapshot = ClipboardHelper.Read(_pluginDir);
        if (snapshot == null)
        {
            Logger.Log("clipboard update with no supported snapshot");
            return;
        }
        var source = ClipboardHelper.ForegroundProcessName();
        Logger.Log($"captured kind={snapshot.Kind} source={source}");
        switch (snapshot.Kind)
        {
            case ClipboardHelper.SnapshotKind.Text:
                _store.AddText(snapshot.Text ?? "", source, snapshot.Hash); break;
            case ClipboardHelper.SnapshotKind.Image:
                _store.AddImage(snapshot.ImagePath!, snapshot.PreviewPath!, source, snapshot.Hash); break;
            case ClipboardHelper.SnapshotKind.Files:
                _store.AddFiles(snapshot.Paths!, source, snapshot.Hash); break;
        }
    }

    // ---- command results ----
    private List<Result> SettingsResults() => new()
    {
        CommandResult($"当前保留时间：{_settings.KeepDays} 天", "输入 c keep 7 / c keep 14 / c keep 30 修改", () => { }),
        CommandResult("保留 7 天",  "按 Enter 应用", () => SetKeepDays(7)),
        CommandResult("保留 14 天", "按 Enter 应用", () => SetKeepDays(14)),
        CommandResult("保留 30 天", "按 Enter 应用", () => SetKeepDays(30)),
    };

    private List<Result> KeepResults(string lower)
    {
        var parts = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var d) && (d == 7 || d == 14 || d == 30))
        {
            return new List<Result> { CommandResult($"设置保留 {d} 天", "按 Enter 应用并清理过期记录", () => SetKeepDays(d)) };
        }
        return SettingsResults();
    }

    private List<Result> StatusResults()
    {
        var stats = _store.Stats();
        var running = _listener != null;
        return new List<Result> { CommandResult(
            running ? "Listener running" : "Listener not running",
            $"records={stats.total} · latest={(string.IsNullOrEmpty(stats.latest) ? "none" : stats.latest)}",
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
