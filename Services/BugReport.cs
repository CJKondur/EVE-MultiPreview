using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Services;

/// <summary>One file of the bug report: its name in the zip and its text.</summary>
public sealed record BugReportFile(string Name, string Text);

/// <summary>One log as read for the bug report: its file name and its last lines.</summary>
public sealed record BugReportLog(string Name, IReadOnlyList<string> Lines);

/// <summary>What goes into the bug report (IDEAS #23), and what never does: the Windows user name in a path, the PC's
/// name and any Discord webhook are always taken out, the known character names hidden unless the player shows them.
/// The zip's text is English, since it is read by whoever fixes the bug (ruling 17). The window hands everything in, so
/// every rule can be tested with made-up values; the one question asked of this PC is the 8.3 short form of the
/// profile folder (<see cref="UserFolders"/>), from the file system, never the network.</summary>
public static class BugReport
{
    /// <summary>How many lines of each log go in.</summary>
    public const int LogLines = 500;

    /// <summary>A longer line is cut here, after it is cleaned, so one runaway line can't fill the report and a cut
    /// can't leave the start of a name behind.</summary>
    public const int MaxLineLength = 2000;

    /// <summary>A log is read from this far before its end at most.</summary>
    public const int MaxTailBytes = 1 << 20;

    /// <summary>What the user name, the PC's name and a Discord webhook become.</summary>
    public const string UserMark = "<user>", PcMark = "<pc>", WebhookMark = "<webhook>";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline;

    /// <summary>The Windows user name wherever it is a folder in a path, and the PC's name as a word.</summary>
    public static string Redact(string text, string userName, string machineName) => Redact(text, new[] { userName }, machineName);

    /// <summary>Each of <paramref name="userNames"/> (the user name, the profile folder, its short form: see
    /// <see cref="UserFolders"/>) wherever it is a folder in a path, as written and JSON-escaped (the settings file
    /// writes "ü" as ü), longest first; a folder ends at a separator, a quote, a space, a dot (name.DOMAIN) or the
    /// line's end. The PC's name as a word.</summary>
    public static string Redact(string text, IReadOnlyList<string> userNames, string machineName)
    {
        var names = userNames.Where(n => !string.IsNullOrWhiteSpace(n)).SelectMany(n => new[] { n, JsonForm(n) })
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToList();
        if (names.Count > 0)
            text = Regex.Replace(text, @"(?<=[\\/])(?:" + string.Join("|", names.Select(Regex.Escape)) + @")(?=[\\/""'\s,;.)\]]|$)",
                UserMark, Options);
        if (!string.IsNullOrWhiteSpace(machineName))
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}_-])" + Regex.Escape(machineName) + @"(?![\p{L}\p{N}_-])", PcMark, Options);
        return text;
    }

    /// <summary>The names the user's folder can go by in a path: <paramref name="userName"/>
    /// (<c>Environment.UserName</c>), the last folder of <paramref name="userProfile"/>
    /// (<c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c>: a renamed account keeps its old folder, a
    /// domain profile is name.DOMAIN) and that folder's 8.3 short form (NAME~1, as %TEMP% often has it) when Windows
    /// has one. Blank and repeated names left out.</summary>
    public static IReadOnlyList<string> UserFolders(string userName, string userProfile)
    {
        var names = new List<string> { userName };
        var profile = (userProfile ?? "").TrimEnd('\\', '/');
        if (profile.Length > 0)
        {
            names.Add(Path.GetFileName(profile));
            var buffer = new char[32768];
            uint length = GetShortPathNameW(profile, buffer, (uint)buffer.Length);
            if (length > 0 && length < buffer.Length) names.Add(Path.GetFileName(new string(buffer, 0, (int)length)));
        }
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint size);

    // The one line the settings file keeps the Discord webhook in, as SettingsBackup's export finds it (IDEAS #2).
    private static readonly Regex WebhookLine = new("(\"PushDiscordWebhook\"\\s*:\\s*)\"[^\"\\r\\n]*\"", RegexOptions.CultureInvariant);

    /// <summary>The settings with the Discord webhook written empty, as an export writes it.</summary>
    public static string WithoutWebhook(string settingsJson) => WebhookLine.Replace(settingsJson, "$1\"\"");

    // A Discord webhook link anywhere (discord.com, discordapp.com, their canary. and ptb. hosts), and a kept,
    // DPAPI-wrapped one (dpapi1: and its base64, which the settings file writes with + as +).
    private static readonly Regex WebhookAnywhere = new(
        @"https?://(?:(?:canary|ptb|www)\.)?discord(?:app)?\.com/api/webhooks/[^\s""'<>]*|dpapi1:(?:[A-Za-z0-9+/=]|\\u[0-9A-Fa-f]{4})*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every Discord webhook link, and every kept one, becomes <see cref="WebhookMark"/>: whatever wrote a
    /// file, none leaves in the report.</summary>
    public static string WithoutWebhookLinks(string text) => WebhookAnywhere.Replace(text, WebhookMark);

    /// <summary>Each known name, as a whole name in any case, as written or JSON-escaped (O'Neil is O'Neil in the
    /// settings file), becomes "Character N" by its place in the list (blank and repeated names left out); longer names
    /// first, so a name inside a longer one is not cut. A match the list can't place (the regex's case folding is wider
    /// than the list's, e.g. the Kelvin sign for K) becomes "Character".</summary>
    public static string Mask(string text, IReadOnlyList<string> names)
    {
        var known = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (known.Count == 0) return text;
        var label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < known.Count; i++) label.TryAdd(known[i], "Character " + (i + 1).ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < known.Count; i++) label.TryAdd(JsonForm(known[i]), label[known[i]]);
        var pattern = @"(?<![\p{L}\p{N}])(?:" + string.Join("|", label.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])";
        return Regex.Replace(text, pattern, m => label.TryGetValue(m.Value, out var l) ? l : "Character",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // A name as the settings file writes it inside a JSON string (default escaping); the name itself if it can't be.
    private static string JsonForm(string name)
    {
        try { return JsonEncodedText.Encode(name).ToString(); }
        catch (ArgumentException) { return name; }
    }

    /// <summary>The last <paramref name="count"/> lines of a log, read from at most its last <see cref="MaxTailBytes"/>
    /// (the partial first line then dropped), uncut: <see cref="Build"/> cuts them once they are cleaned. The log may be
    /// open for writing: it is shared for read, write and delete. A log that is missing or can't be read gives no
    /// lines.</summary>
    public static IReadOnlyList<string> LastLines(string path, int count = LogLines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var partial = stream.Length > MaxTailBytes;
            if (partial) stream.Seek(-MaxTailBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (partial) reader.ReadLine();
            var last = new Queue<string>(count + 1);
            while (reader.ReadLine() is { } line)
            {
                last.Enqueue(line);
                if (last.Count > count) last.Dequeue();
            }
            return last.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The report's files, in the order the window shows them: about.txt, monitors.txt, settings.json (its
    /// webhook written empty), then each log under logs/, each log line cut at <see cref="MaxLineLength"/> after it is
    /// cleaned. In every file each Discord webhook link is taken out, every name of
    /// <see cref="UserFolders"/>(<paramref name="userName"/>, <paramref name="userProfile"/>) and the PC's name too,
    /// and the <paramref name="characters"/> are masked when <paramref name="maskNames"/>. The window passes
    /// <c>Environment.UserName</c>, <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c> and
    /// <c>Environment.MachineName</c>.</summary>
    public static IReadOnlyList<BugReportFile> Build(IReadOnlyList<string> about, IReadOnlyList<string> monitors, string settingsJson,
        IReadOnlyList<BugReportLog> logs, string userName, string userProfile, string machineName, IReadOnlyList<string> characters,
        bool maskNames)
    {
        var userNames = UserFolders(userName, userProfile);
        string Clean(string text)
        {
            text = Redact(WithoutWebhookLinks(text), userNames, machineName);
            return maskNames ? Mask(text, characters) : text;
        }
        string Cut(string line) => line.Length > MaxLineLength ? line[..MaxLineLength] : line;

        var files = new List<BugReportFile>
        {
            new("about.txt", Clean(string.Join("\n", about))),
            new("monitors.txt", Clean(string.Join("\n", monitors))),
            new("settings.json", Clean(WithoutWebhook(settingsJson))),
        };
        foreach (var log in logs)
            files.Add(new("logs/" + Path.GetFileName(log.Name), string.Join("\n", log.Lines.Select(l => Cut(Clean(l))))));
        return files;
    }

    /// <summary>The files as a zip on <paramref name="to"/> (left open), each entry UTF-8 without a BOM.</summary>
    public static void WriteZip(Stream to, IReadOnlyList<BugReportFile> files)
    {
        using var zip = new ZipArchive(to, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open(), new UTF8Encoding(false));
            writer.Write(file.Text);
        }
    }

    /// <summary>One line of monitors.txt. <paramref name="bounds"/> are as this system-aware process sees them, so the
    /// monitor's own scaling is the system's times how much bigger its real <paramref name="physical"/> width is.</summary>
    public static string Monitor(int number, bool primary, System.Drawing.Rectangle bounds, System.Drawing.Rectangle workArea,
        (int W, int H) physical, double systemScale)
    {
        var pct = (int)Math.Round(100 * systemScale * physical.W / bounds.Width);
        return string.Create(CultureInfo.InvariantCulture,
            $"{number}: {(primary ? "primary, " : "")}{bounds.Width} x {bounds.Height} at {bounds.X}, {bounds.Y}, work area {workArea.Width} x {workArea.Height}, {physical.W} x {physical.H} pixels, scaling {pct} %");
    }
}
