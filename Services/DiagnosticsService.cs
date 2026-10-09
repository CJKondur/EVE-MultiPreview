using System;
using System.Diagnostics;
using System.IO;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public static class DiagnosticsService
{
    /// <summary>The Logs folder in the settings folder (IDEAS #57): the debug logs and error_log.txt.</summary>
    private static string LogDir => AppPaths.LogsDir;

    public static AppSettings? GlobalSettings { get; set; }

    public static void Initialize()
    {
        try { Directory.CreateDirectory(LogDir); } catch { }
    }

    private static readonly object _writeLock = new();

    /// <summary>Categories that have already had this run's header written.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _headered = new();

    /// <summary>App version, resolved once. Stamped at the top of every log file so a
    /// submitted log always identifies the build that produced it — without this, a log
    /// missing an expected diagnostic is ambiguous between "old build" and "the code
    /// path never ran", which is exactly the ambiguity that stalled #95.</summary>
    private static readonly string AppVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

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
                // One header per category per run. Logs are appended across sessions and
                // across app updates, so without this a file can silently mix builds.
                if (_headered.Add(category))
                {
                    File.AppendAllText(path,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===== EVE MultiPreview v{AppVersion} — {category} log started =====\n");
                }
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

    public static void OpenLogsFolder() => OpenFolder(LogDir);

    /// <summary>Opens error_log.txt in the program Windows uses for text files (IDEAS #55), or the logs folder while
    /// there is no error log yet.</summary>
    public static void OpenErrorLog()
    {
        var path = ErrorLog.Shared.FilePath;
        if (File.Exists(path))
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return; }
            catch (Exception ex) { Debug.WriteLine($"[Diag] ❌ Open error log failed: {ex.Message}"); }
        }
        OpenLogsFolder();
    }

    /// <summary>Opens a folder in Explorer, creating it first. The path goes as one argument, spaces and all.</summary>
    public static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir } });
        }
        catch (Exception ex) { Debug.WriteLine($"[Diag] ❌ Open folder failed: {ex.Message}"); }
    }
}
