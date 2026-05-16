using System;
using System.IO;
using System.Text.Json;

namespace Flow.Launcher.Plugin.PasteTool;

internal class Settings
{
    public int KeepDays { get; set; } = 14;
    public int MaxResults { get; set; } = 80;

    private readonly string _path;

    public Settings(string pluginDir)
    {
        var dataDir = Path.Combine(pluginDir, "Data");
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "settings.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("keep_days", out var kd) && kd.TryGetInt32(out var k)) KeepDays = k;
            if (root.TryGetProperty("max_results", out var mr) && mr.TryGetInt32(out var m)) MaxResults = m;
        }
        catch (Exception ex)
        {
            Logger.LogException("settings load failed", ex);
        }
    }

    public void Save()
    {
        try
        {
            var payload = new { keep_days = KeepDays, max_results = MaxResults };
            File.WriteAllText(_path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogException("settings save failed", ex);
        }
    }
}
