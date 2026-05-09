using System;
using System.Diagnostics;
using System.IO;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public static class DiagnosticsService
{
    private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    
    public static AppSettings? GlobalSettings { get; set; }

    public static void Initialize()
    {
        if (!Directory.Exists(LogDir))
        {
            try { Directory.CreateDirectory(LogDir); } catch { }
        }
    }

    private static readonly object _writeLock = new();

    private static void AppendLog(string category, string message)
    {
        // Serialize writes so concurrent calls from many chars firing at once
        // can't lose entries to file-lock contention. File.AppendAllText opens
        // the file with exclusive write each call, so two parallel calls would
        // race; previously the loser was silently dropped.
        try
        {
            string fileName = $"debug_{category}.log";
            string path = Path.Combine(LogDir, fileName);
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
            lock (_writeLock)
            {
                File.AppendAllText(path, line);
            }
            Debug.WriteLine($"[{category}] {message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Diag:{category}] ❌ Log write failed: {ex.Message}");
        }
    }

    public static void LogInjection(string message)
    {
        if (GlobalSettings?.EnableDebugLogging_Injection == true)
        {
            AppendLog("injection", message);
        }
    }

    public static void LogCycling(string message)
    {
        if (GlobalSettings?.EnableDebugLogging_Cycling == true)
        {
            AppendLog("cycling", message);
        }
    }

    public static void LogWindowHook(string message)
    {
        if (GlobalSettings?.EnableDebugLogging_WindowHooks == true)
        {
            AppendLog("window_hooks", message);
        }
    }

    public static void LogDwm(string message)
    {
        if (GlobalSettings?.EnableDebugLogging_DWM == true)
        {
            AppendLog("dwm", message);
        }
    }

    public static void LogAlerts(string message)
    {
        if (GlobalSettings?.EnableDebugLogging_Alerts == true)
        {
            AppendLog("alerts", message);
        }
    }

    public static void OpenLogsFolder()
    {
        if (Directory.Exists(LogDir))
        {
            Process.Start("explorer.exe", LogDir);
        }
        else
        {
            Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
