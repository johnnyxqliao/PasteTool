using System;
using System.IO;

namespace Flow.Launcher.Plugin.PasteTool;

internal static class Logger
{
    private static string? _logFile;
    private static readonly object _lock = new();

    public static void Init(string pluginDir)
    {
        var dataDir = Path.Combine(pluginDir, "Data");
        Directory.CreateDirectory(dataDir);
        _logFile = Path.Combine(dataDir, "paste_tool.log");
    }

    public static void Log(string message)
    {
        if (_logFile == null) return;
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void LogException(string message, Exception ex)
    {
        Log($"{message}\n{ex}");
    }
}
