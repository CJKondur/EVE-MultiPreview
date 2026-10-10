using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>How far back the history window looks.</summary>
public enum HistoryRange { Today, Week, All }

/// <summary>What the history window's filters ask for. An empty value means "all".</summary>
public sealed record HistoryFilter(string Character = "", string EventKey = "", string Severity = "", string Search = "");

/// <summary>One row of the history window: an alert, or a run of the same alert for the same character shown as one.
/// <paramref name="Newest"/> is the run's last alert, <paramref name="Count"/> how many it holds and
/// <paramref name="OldestUtc"/> when it began.</summary>
public sealed record HistoryRow(AlertHistoryEntry Newest, int Count, DateTime OldestUtc);

/// <summary>What the history window shows, worked out without any window (IDEAS #1): the time a range starts, the
/// rows that pass the filters, and repeats folded into one row.</summary>
public static class AlertHistoryView
{
    /// <summary>The most alerts the window loads for a range. Beyond it, it says that it shows the newest.</summary>
    public const int MaxEntries = 20000;

    /// <summary>Today is since midnight on this PC's clock; the week is the last seven days to the hour; All is
    /// everything kept.</summary>
    public static DateTime SinceUtc(HistoryRange range, DateTime nowLocal) => range switch
    {
        HistoryRange.Today => nowLocal.Date.ToUniversalTime(),
        HistoryRange.Week => nowLocal.AddDays(-7).ToUniversalTime(),
        _ => DateTime.MinValue,
    };

    /// <summary>An alert's name as the toast shows it. A custom rule's alert is named after the rule as it was called
    /// when the alert fired, which the history kept, so a rule deleted since still reads right.</summary>
    public static string EventName(AlertHistoryEntry e) =>
        e.Source.Length > 0 ? e.Source : AlertHub.EventDisplayName(e.EventKey);

    public static bool Matches(AlertHistoryEntry e, HistoryFilter f, Func<AlertHistoryEntry, string> eventName)
    {
        if (f.Character.Length > 0 && !string.Equals(e.Character, f.Character, StringComparison.OrdinalIgnoreCase)) return false;
        if (f.EventKey.Length > 0 && !string.Equals(e.EventKey, f.EventKey, StringComparison.Ordinal)) return false;
        if (f.Severity.Length > 0 && !string.Equals(e.Severity, f.Severity, StringComparison.Ordinal)) return false;
        var search = f.Search.Trim();
        if (search.Length == 0) return true;
        return Has(e.Character) || Has(eventName(e)) || Has(e.SolarSystem) || Has(e.Text);

        bool Has(string text) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    public static IReadOnlyList<AlertHistoryEntry> Filter(IEnumerable<AlertHistoryEntry> entries, HistoryFilter filter,
        Func<AlertHistoryEntry, string> eventName) => entries.Where(e => Matches(e, filter, eventName)).ToList();

    /// <summary>Folds each run of the same alert for the same character into one row. The list is newest first, and
    /// stays so.</summary>
    public static IReadOnlyList<HistoryRow> Collapse(IReadOnlyList<AlertHistoryEntry> newestFirst)
    {
        var rows = new List<HistoryRow>();
        for (int i = 0; i < newestFirst.Count;)
        {
            var newest = newestFirst[i];
            int end = i + 1;
            while (end < newestFirst.Count && SameAlert(newestFirst[end], newest)) end++;
            rows.Add(new HistoryRow(newest, end - i, newestFirst[end - 1].TimeUtc));
            i = end;
        }
        return rows;
    }

    private static bool SameAlert(AlertHistoryEntry a, AlertHistoryEntry b) =>
        string.Equals(a.EventKey, b.EventKey, StringComparison.Ordinal)
        && string.Equals(a.Character, b.Character, StringComparison.OrdinalIgnoreCase);

    /// <summary>The time as the row shows it, on this PC's clock: the time alone for today, the date with it otherwise.</summary>
    public static string TimeText(DateTime utc, DateTime nowLocal)
    {
        var local = utc.ToLocalTime();
        return local.Date == nowLocal.Date
            ? local.ToString("T", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }

    /// <summary>The characters, alert types and severities the loaded alerts hold, for the filter lists: each once, in
    /// the order a player reads a list (by name).</summary>
    public static IReadOnlyList<string> Characters(IEnumerable<AlertHistoryEntry> entries) =>
        entries.Select(e => e.Character).Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static IReadOnlyList<(string Key, string Name)> Events(IEnumerable<AlertHistoryEntry> entries,
        Func<AlertHistoryEntry, string> eventName) =>
        entries.GroupBy(e => e.EventKey, StringComparer.Ordinal)
               .Select(g => (g.Key, Name: eventName(g.First())))
               .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}
