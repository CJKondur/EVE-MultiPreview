using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Services;

/// <summary>One file of the bug report: its name in the zip and its text.</summary>
public sealed record BugReportFile(string Name, string Text);

/// <summary>One log as read for the bug report: its file name and its last lines.</summary>
public sealed record BugReportLog(string Name, IReadOnlyList<string> Lines);

/// <summary>What goes into the bug report (IDEAS #23), and what never does: the Windows user name in a path, the PC's
/// name and the Discord webhook are always taken out, the known character names hidden unless the player shows them.
/// The zip's text is English, since it is read by whoever fixes the bug (ruling 17). Nothing here reads this PC: the
/// window hands everything in, so every rule can be tested with made-up values.</summary>
public static class BugReport
{
    /// <summary>How many lines of each log go in.</summary>
    public const int LogLines = 500;

    /// <summary>A longer line is cut here, so one runaway line can't fill the report.</summary>
    public const int MaxLineLength = 2000;

    /// <summary>A log is read from this far before its end at most.</summary>
    public const int MaxTailBytes = 1 << 20;

    /// <summary>What the user name and the PC's name become.</summary>
    public const string UserMark = "<user>", PcMark = "<pc>";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline;

    /// <summary>The Windows user name wherever it is a folder in a path (also JSON-escaped), and the PC's name as a word.</summary>
    public static string Redact(string text, string userName, string machineName)
    {
        if (!string.IsNullOrWhiteSpace(userName))
            text = Regex.Replace(text, @"(?<=[\\/])" + Regex.Escape(userName) + @"(?=[\\/""'\s,;)\]]|$)", UserMark, Options);
        if (!string.IsNullOrWhiteSpace(machineName))
            text = Regex.Replace(text, @"(?<![\p{L}\p{N}_-])" + Regex.Escape(machineName) + @"(?![\p{L}\p{N}_-])", PcMark, Options);
        return text;
    }

    // The one line the settings file keeps the Discord webhook in, as SettingsBackup's export finds it (IDEAS #2).
    private static readonly Regex WebhookLine = new("(\"PushDiscordWebhook\"\\s*:\\s*)\"[^\"\\r\\n]*\"", RegexOptions.CultureInvariant);

    /// <summary>The settings with the Discord webhook written empty, as an export writes it.</summary>
    public static string WithoutWebhook(string settingsJson) => WebhookLine.Replace(settingsJson, "$1\"\"");

    /// <summary>Each known name, as a whole name in any case, becomes "Character N" by its place in the list (blank and
    /// repeated names left out); longer names first, so a name inside a longer one is not cut.</summary>
    public static string Mask(string text, IReadOnlyList<string> names)
    {
        var known = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (known.Count == 0) return text;
        var label = known.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => "Character " + (x.i + 1).ToString(CultureInfo.InvariantCulture),
            StringComparer.OrdinalIgnoreCase);
        var pattern = @"(?<![\p{L}\p{N}])(?:" + string.Join("|", known.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])";
        return Regex.Replace(text, pattern, m => label[m.Value], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The last <paramref name="count"/> lines of a log, each cut at <see cref="MaxLineLength"/>, read from at
    /// most its last <see cref="MaxTailBytes"/> (the partial first line then dropped). The log may be open for writing:
    /// it is shared for read, write and delete. A log that is missing or can't be read gives no lines.</summary>
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
                last.Enqueue(line.Length > MaxLineLength ? line[..MaxLineLength] : line);
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
    /// webhook out), then each log under logs/. Every file has the user name and the PC's name taken out, and the
    /// <paramref name="characters"/> masked when <paramref name="maskNames"/>.</summary>
    public static IReadOnlyList<BugReportFile> Build(IReadOnlyList<string> about, IReadOnlyList<string> monitors, string settingsJson,
        IReadOnlyList<BugReportLog> logs, string userName, string machineName, IReadOnlyList<string> characters, bool maskNames)
    {
        string Clean(string text)
        {
            text = Redact(text, userName, machineName);
            return maskNames ? Mask(text, characters) : text;
        }

        var files = new List<BugReportFile>
        {
            new("about.txt", Clean(string.Join("\n", about))),
            new("monitors.txt", Clean(string.Join("\n", monitors))),
            new("settings.json", Clean(WithoutWebhook(settingsJson))),
        };
        foreach (var log in logs)
            files.Add(new("logs/" + Path.GetFileName(log.Name), Clean(string.Join("\n", log.Lines))));
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
