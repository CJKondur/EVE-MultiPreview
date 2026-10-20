using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Services;

/// <summary>Reads the stat logs (StatTrackerService writes one <c>StatLog_&lt;character&gt;_&lt;yyyy-MM-dd&gt;.csv</c> per
/// character per local day into the folder the player chose, Tools › Stats) back as session reports (IDEAS #20). It reads
/// only that one folder, only files of that name, none that is a link and none outside the last <see cref="MaxDays"/> days,
/// and never throws: a missing, blank, unreadable or empty folder, or a file that is locked or garbage, is just no report.
/// Names (the character is in the file's name) are never logged. Nothing here writes.</summary>
public static class StatReportFolder
{
    /// <summary>The most days back a report goes, today included.</summary>
    public const int MaxDays = 30;

    /// <summary>The most files read in one go (the newest): 30 days of 60 characters is 1,800.</summary>
    public const int MaxFiles = 2000;

    private static readonly Regex FileName = new(@"^StatLog_(?<name>.+?)_(?<day>\d{4}-\d{2}-\d{2})\.csv$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    /// <summary>The character and the local day a stat log's file name says. The writer turns every character a file name
    /// can't hold into "_" (StatTrackerService.LogCsv), which can't be undone, but an EVE character name has none of them
    /// (letters, digits, spaces, apostrophes and hyphens), so the name in the file name is the name. A name that does have
    /// an underscore is shown as the file name has it.</summary>
    public static bool TryParseName(string fileName, out string character, out DateOnly day)
    {
        character = "";
        day = default;
        try
        {
            var m = FileName.Match(fileName ?? "");
            if (!m.Success || string.IsNullOrWhiteSpace(m.Groups["name"].Value) || m.Groups["name"].Length > 100) return false;
            if (!DateOnly.TryParseExact(m.Groups["day"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return false;
            character = m.Groups["name"].Value;
            return true;
        }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>The report of the last <paramref name="days"/> days (1 to <see cref="MaxDays"/>), today included, from the
    /// stat logs in <paramref name="directory"/>. Empty when there is nothing to read.</summary>
    /// <param name="today">The local date today; a file dated after it is not read.</param>
    /// <param name="isLink">Whether a path is a link (SettingsHome.IsLink, unless a test says so): neither the folder nor a
    /// file that is one is opened.</param>
    /// <param name="maxFiles">Tests: the number of files read, the newest first.</param>
    public static IReadOnlyList<CharacterReport> Read(string? directory, DateOnly today, int days, Func<string, bool>? isLink = null, int maxFiles = MaxFiles)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Array.Empty<CharacterReport>();
        isLink ??= SettingsHome.IsLink;
        var oldest = today.AddDays(1 - Math.Clamp(days, 1, MaxDays));
        try
        {
            if (!Directory.Exists(directory) || isLink(directory)) return Array.Empty<CharacterReport>();
            var wanted = new List<(string Path, string Character, DateOnly Day)>();
            foreach (var path in Directory.EnumerateFiles(directory, "StatLog_*.csv", SearchOption.TopDirectoryOnly))
            {
                if (TryParseName(Path.GetFileName(path), out var character, out var day) && day >= oldest && day <= today && !isLink(path))
                    wanted.Add((path, character, day));
            }
            var newest = wanted.OrderByDescending(f => f.Day).Take(Math.Max(0, maxFiles));
            return StatReport.Aggregate(newest.Select(f => Open(f.Path, f.Character, f.Day)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<CharacterReport>();            // the exception's text names a path, and the path a character
        }
    }

    /// <summary>One file's lines, or none when it can't be opened (in use by another program, removed, no access). The
    /// writer keeps appending to today's file, so it is opened for sharing.</summary>
    private static StatLogFile Open(string path, string character, DateOnly day)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, new UTF8Encoding(false), true, 65536);
            return new StatLogFile(character, day, StatLogReader.Parse(reader));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new StatLogFile(character, day, Array.Empty<StatRow>());
        }
    }
}
