using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S8: what the bug report (IDEAS #23) is made from on Linux: BugReportSources.Gather's Linux half (keep in sync).
/// The same files and the same rules (the shared BugReport.Build: the Discord webhook, the user's folder name and the PC's
/// name always taken out, the known character names masked unless the player shows them); only the about lines name Linux
/// things and the monitors come from Avalonia. Read only: nothing is written and nothing is sent; the window saves the zip
/// where the player chooses, and only when they click Save.</summary>
internal static class LinuxBugReport
{
    // MultiPreview's own logs in its Logs folder (BugReportSources.OwnLog: keep in sync), nothing else that lies there.
    private static readonly Regex OwnLog = new(@"^(debug_log\.txt|error_log\.txt|debug_[a-z_]+\.log)$", RegexOptions.CultureInvariant);

    /// <summary>BugReportSources.PerfLogName and SettingsDiagLogName: the two logs kept in the temp folder.</summary>
    public const string PerfLogName = "evemultipreview_perf.log", SettingsDiagLogName = "EveMultiPreview_SettingsDiag.log";

    /// <summary>BugReportSources.LogFileNames: the names of the Logs folder that are MultiPreview's own logs, sorted.</summary>
    public static IReadOnlyList<string> LogFileNames(IEnumerable<string> namesInLogsFolder) =>
        namesInLogsFolder.Where(n => OwnLog.IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>BugReportSources.Characters (keep in sync; a Linux check compares the stores the two read): every character
    /// name MultiPreview knows, to be masked. The running ones first; each name once, ignoring case, first spelling kept.</summary>
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
        names.AddRange(s.EveManager?.CharNameCache?.Values.Select(e => e.Name ?? "") ?? Enumerable.Empty<string>());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return names.Where(n => !string.IsNullOrWhiteSpace(n) && seen.Add(n)).ToArray();
    }

    /// <summary>BugReportSources.Redactor: what an error's text goes through before the window shows it.</summary>
    public static Func<string, string> Redactor(string userName, string userProfile, string machineName)
    {
        var folders = BugReport.UserFolders(userName, userProfile);
        return text => BugReport.Redact(text, folders, machineName);
    }

    /// <summary>One monitor as Avalonia gives it: its bounds and work area in pixels and its scaling.</summary>
    public readonly record struct Monitor(bool Primary, System.Drawing.Rectangle Bounds, System.Drawing.Rectangle WorkArea, double Scaling);

    /// <summary>The report's files, from this PC (BugReportSources.Gather). Never called by a test.</summary>
    public static IReadOnlyList<BugReportFile> Gather(AppSettings s, IReadOnlyList<string> running, IReadOnlyList<Monitor> screens, bool maskNames)
    {
        var about = new List<string>
        {
            "EVE MultiPreview " + AboutActions.VersionText + " (Linux)",
            "Linux: " + RuntimeInformation.OSDescription,
            "Distribution: " + Distribution(),
            "Desktop: " + Desktop(),
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

        // Avalonia's bounds are pixels: the "physical" size is the same, and the scaling is the screen's own.
        var monitors = screens.Select((m, i) => BugReport.Monitor(i + 1, m.Primary, m.Bounds, m.WorkArea, (m.Bounds.Width, m.Bounds.Height), m.Scaling)).ToList();

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

    /// <summary>The distribution's own name (PRETTY_NAME of /etc/os-release, read only), or "unknown".</summary>
    private static string Distribution()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                    return line["PRETTY_NAME=".Length..].Trim().Trim('"');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return "unknown";
    }

    /// <summary>The desktop and the session type (XDG_CURRENT_DESKTOP, XDG_SESSION_TYPE), as the environment says them.</summary>
    private static string Desktop()
    {
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") is { Length: > 0 } d ? d : "unknown";
        string session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is { Length: > 0 } t ? t : "unknown";
        return PlainText.OneLine(desktop, 60) + " (" + PlainText.OneLine(session, 20) + ")";
    }
}
