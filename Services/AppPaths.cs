using System;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>Where MultiPreview keeps its files (IDEAS #57). The settings folder holds the settings file,
/// layout_presets.json, Backups\, Logs\ (the debug logs and error_log.txt) and the run marker. It is
/// %APPDATA%\EVE MultiPreview, or the exe folder when portable.txt sits beside the exe. App sets it once at startup
/// (SettingsHome.Resolve), before anything writes a file; until then it is the exe folder, where everything lived
/// before.</summary>
public static class AppPaths
{
    public const string SettingsFileName = "EVE MultiPreview.json";
    /// <summary>A file with this name beside the exe keeps every file there (portable mode). Its content doesn't matter,
    /// and portable.txt.txt counts too (SettingsHome.IsPortable).</summary>
    public const string PortableMarkerName = "portable.txt";
    /// <summary>Left beside the exe when the settings move, saying where they went and how to stay portable.</summary>
    public const string MovedNoteName = "Where are my settings.txt";
    public const string AppDataFolderName = "EVE MultiPreview";

    /// <summary>The folder the exe is in. Environment.ProcessPath, not AppContext.BaseDirectory, which a single-file app
    /// may point at its extraction folder.</summary>
    public static string ExeDir { get; } = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>%APPDATA%\EVE MultiPreview: the settings folder unless MultiPreview is portable.</summary>
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataFolderName);

    /// <summary>The settings folder this run uses.</summary>
    public static string ConfigDir { get; private set; } = ExeDir;

    public static string SettingsPath => Path.Combine(ConfigDir, SettingsFileName);
    public static string BackupsDir => Path.Combine(ConfigDir, "Backups");
    public static string LogsDir => Path.Combine(ConfigDir, "Logs");
    public static string ErrorLogPath => Path.Combine(LogsDir, "error_log.txt");
    /// <summary>The alert history's day files (IDEAS #1).</summary>
    public static string HistoryDir => Path.Combine(ConfigDir, "History");

    /// <summary>False while the Logs folder is a link (a junction or a symbolic link): MultiPreview never writes through
    /// one, so this run writes no log files (the error log, the debug logs, the trace file). Checked once, when the
    /// settings folder is set.</summary>
    public static bool LogsWritable { get; private set; } = true;

    /// <summary>Sets the settings folder. App calls it once, right after SettingsHome.Resolve; a test that calls it puts
    /// the old value back when it ends.</summary>
    public static void Use(string configDir)
    {
        ConfigDir = configDir;
        LogsWritable = !SettingsHome.IsLink(LogsDir);
    }
}
