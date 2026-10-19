using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using EveMultiPreview.Interop;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>What the bug report (IDEAS #23) is made from on this PC: the about lines, the monitors, the saved settings,
/// MultiPreview's own logs and the names of every character MultiPreview knows. Only <see cref="BugReportActions"/>
/// gathers; the rules of what is kept and taken out are <see cref="BugReport"/>'s, tested with made-up values.</summary>
public static class BugReportSources
{
    /// <summary>The two logs MultiPreview keeps in %TEMP% (App writes them under these names).</summary>
    public const string PerfLogName = "evemultipreview_perf.log", SettingsDiagLogName = "EveMultiPreview_SettingsDiag.log";

    // MultiPreview's own logs in its Logs folder (DiagnosticsService, ErrorLog): nothing else that lies there.
    private static readonly Regex OwnLog = new(@"^(debug_log\.txt|error_log\.txt|debug_[a-z_]+\.log)$", RegexOptions.CultureInvariant);

    /// <summary>The names of the Logs folder that are MultiPreview's own logs, sorted.</summary>
    public static IReadOnlyList<string> LogFileNames(IEnumerable<string> namesInLogsFolder) =>
        namesInLogsFolder.Where(n => OwnLog.IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>Every character name MultiPreview knows, to be masked: the running ones first, then every name in a
    /// per-character store of every profile and the global ones. Each name once, ignoring case, first spelling kept.</summary>
    public static IReadOnlyList<string> Characters(AppSettings s, IEnumerable<string> running)
    {
        var names = new List<string>(running);
        names.AddRange(CharacterDirectory.AllKnown(s, running));
        foreach (var p in s.Profiles.Values)
        {
            names.AddRange(p.ThumbnailPositions.Keys);
            names.AddRange(p.ClientPositions.Keys);
            names.AddRange(p.Hotkeys.Keys);
            names.AddRange(p.SecondaryThumbnails.Keys);
            names.AddRange(p.ThumbnailVisibility.Keys);
            names.AddRange(p.CustomColors.Keys);
            names.AddRange(p.Crops.Keys);
            names.AddRange(p.PerClientCores.Keys);
            names.AddRange(p.PerClientAudioVolume.Keys);
            names.AddRange(p.StatOverlayCharacters.Keys);
            names.AddRange(p.DontMinimizeClients);
            names.AddRange(p.ClientSlotExcluded);
            names.AddRange(p.ClientSlots.SelectMany(slot => slot.Characters));
            names.AddRange(p.HotkeyGroups.Values.SelectMany(g => g.Characters));
            names.AddRange(p.Groups.Values.SelectMany(g => g.Members));
            names.AddRange(AutoProfile.Characters(p.AutoSelectCharacters));   // IDEAS #11
        }
        names.AddRange(s.ThumbnailGroups.SelectMany(g => g.Members));
        names.AddRange(s.ThumbnailAnnotations.Keys);
        names.AddRange(s.ThumbnailLabelStyles.Keys);
        names.AddRange(s.PerCharacterStats.Keys);
        names.AddRange(s.AccountCharacterMap.Values.SelectMany(v => v));
        names.AddRange(s.QuickSwitchCardOrder);
        names.AddRange(s.StatWindowPositions.Keys.Select(k => k.Split(':')[0]));
        // EVE Manager's names, looked up from ESI: often every character on every account (WithoutCharacterIds empties
        // the store itself in the report; these masks reach the logs).
        names.AddRange(s.EveManager?.CharNameCache?.Values.Select(e => e.Name ?? "") ?? Enumerable.Empty<string>());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return names.Where(n => !string.IsNullOrWhiteSpace(n) && seen.Add(n)).ToArray();
    }

    /// <summary>What the window does to an error's text before it shows it: the user's folder names and the PC's name
    /// taken out, as in the report (a screenshot of the window is shared too).</summary>
    public static Func<string, string> Redactor(string userName, string userProfile, string machineName)
    {
        var folders = BugReport.UserFolders(userName, userProfile);
        return text => BugReport.Redact(text, folders, machineName);
    }

    /// <summary>The name the Save dialog offers.</summary>
    public static string FileName(DateTime now) =>
        string.Create(CultureInfo.InvariantCulture, $"EVE MultiPreview bug report {now:yyyy-MM-dd HH-mm}.zip");

    /// <summary>The report's files, from this PC: read only, nothing written, nothing sent. Never called by a test.</summary>
    public static IReadOnlyList<BugReportFile> Gather(AppSettings s, IReadOnlyList<string> running, bool maskNames)
    {
        var about = new List<string>
        {
            "EVE MultiPreview " + AboutActions.VersionText,
            "Windows: " + WindowsVersion(),
            ".NET: " + RuntimeInformation.FrameworkDescription,
            "64-bit: " + Environment.Is64BitProcess,
            "Language: " + LocalizationService.CurrentLanguage + " (setting: " + (string.IsNullOrEmpty(s.Language) ? "automatic" : s.Language) + ")",
            "Look: " + s.UiStyle,
            "Settings folder: " + AppPaths.ConfigDir,
            "Clients with a character: " + running.Count.ToString(CultureInfo.InvariantCulture),
            "Logical processors: " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            "Errors this session: " + ErrorLog.Shared.Count.ToString(CultureInfo.InvariantCulture),
            "Created (UTC): " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
        };

        var screens = System.Windows.Forms.Screen.AllScreens;
        double scale = DpiHelper.GetSystemScaleFactor();
        var monitors = screens.Select((sc, i) => BugReport.Monitor(i + 1, sc.Primary, sc.Bounds, sc.WorkingArea,
            MonitorInfo.PhysicalResolution(sc), scale)).ToList();

        var paths = new List<string>();
        if (AppPaths.LogsWritable && Directory.Exists(AppPaths.LogsDir))
            paths.AddRange(LogFileNames(Directory.EnumerateFiles(AppPaths.LogsDir).Select(f => Path.GetFileName(f)))
                .Select(n => Path.Combine(AppPaths.LogsDir, n)));
        paths.Add(Path.Combine(Path.GetTempPath(), PerfLogName));
        paths.Add(Path.Combine(Path.GetTempPath(), SettingsDiagLogName));
        var logs = paths.Select(p => new BugReportLog(Path.GetFileName(p), BugReport.LastLines(p))).Where(l => l.Lines.Count > 0).ToList();

        return BugReport.Build(about, monitors, SettingsService.Canonical(s), logs, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.MachineName, Characters(s, running), maskNames);
    }

    // major.minor.build.UBR and the display version (23H2), read only; the version alone when the key can't be read.
    private static string WindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        var text = string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Build}");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            if (key?.GetValue("UBR") is int ubr) text += "." + ubr.ToString(CultureInfo.InvariantCulture);
            if (key?.GetValue("DisplayVersion") is string display && display.Length > 0) text += " (" + display + ")";
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return text;
    }
}

/// <summary>Diagnostics › Bug report › Create bug report…, and the after-a-crash notice's button (IDEAS #23, #55).</summary>
public static class BugReportActions
{
    public static void Open(SettingsContext ctx) =>
        Show(ctx.S, CharacterDirectory.Running(ctx), ctx.Host.DialogOwner, modal: true);

    /// <summary>The window, on this PC's files; Explorer opens the folder a zip was saved in.</summary>
    public static void Show(AppSettings s, IReadOnlyList<string> running, Window? owner, bool modal)
    {
        var window = new Views.BugReportWindow(mask => BugReportSources.Gather(s, running, mask), PickPath,
            path => DiagnosticsService.OpenFolder(Path.GetDirectoryName(path)!),
            BugReportSources.Redactor(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.MachineName))
        { Owner = owner };
        if (owner != null) window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (modal && owner != null) window.ShowDialog();
        else window.Show();
    }

    // Where the player wants the zip; null when cancelled.
    private static string? PickPath(Window owner)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = BugReportSources.FileName(DateTime.Now),
            DefaultExt = ".zip",
            Filter = LocalizationService.Str("L.Report.ZipFilter", "Zip files (*.zip)|*.zip"),
            AddExtension = true,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}
