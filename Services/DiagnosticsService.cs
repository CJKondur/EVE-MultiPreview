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

    private static void AppendLog(string category, string message)
    {
        try
        {
            string fileName = $"debug_{category}.log";
            string path = Path.Combine(LogDir, fileName);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            Debug.WriteLine($"[{category}] {message}");
        }
        catch { }
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
