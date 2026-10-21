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

/// <summary>One row of the history window: an alert, or a run of the same alert shown as one (see
/// <see cref="AlertHistoryView.Collapse"/> for what "the same" is). <paramref name="Newest"/> is the run's last alert,
/// <paramref name="Count"/> how many it holds and <paramref name="OldestUtc"/> when it began.</summary>
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

    /// <summary>Two neighbours further apart than this are not one run, however alike they are.</summary>
    public static readonly TimeSpan RunGap = TimeSpan.FromMinutes(10);

    /// <summary>Folds each run of the same alert into one row. Two neighbours are the same alert when they have the
    /// same alert type, character, matched text and solar system, when both had their sound held or both not (P81), when a row says the same of what became of them
    /// (<see cref="HasNote"/>), and when they are at most <see cref="RunGap"/> apart. That is everything a row shows
    /// but the time: three matches of one rule with three different lines are three rows. The list is newest first,
    /// and stays so.</summary>
    public static IReadOnlyList<HistoryRow> Collapse(IReadOnlyList<AlertHistoryEntry> newestFirst)
    {
        var rows = new List<HistoryRow>();
        for (int i = 0; i < newestFirst.Count;)
        {
            var newest = newestFirst[i];
            int end = i + 1;
            while (end < newestFirst.Count && SameAlert(newestFirst[end], newest)
                   && (newestFirst[end - 1].TimeUtc - newestFirst[end].TimeUtc).Duration() <= RunGap) end++;
            rows.Add(new HistoryRow(newest, end - i, newestFirst[end - 1].TimeUtc));
            i = end;
        }
        return rows;
    }

    private static bool SameAlert(AlertHistoryEntry a, AlertHistoryEntry b) =>
        string.Equals(a.EventKey, b.EventKey, StringComparison.Ordinal)
        && string.Equals(a.Character, b.Character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Text, b.Text, StringComparison.Ordinal)
        && string.Equals(a.SolarSystem, b.SolarSystem, StringComparison.Ordinal)
        && a.SoundHeld == b.SoundHeld
        && (HasNote(a.Outcome) ? a.Outcome == b.Outcome : !HasNote(b.Outcome));

    /// <summary>Whether a row says what became of an alert with this outcome. It does only when nothing of the alert
    /// reached the player: it was muted, or the character had no client window. An alert that was shown needs no
    /// note, and "no toast" would stand in every row of a player who doesn't use the hub. So an alert with its toast
    /// and one without look the same in the list, and are the same alert to a run: a run is folded by what its row
    /// shows, not by the outcome as the history keeps it. (The sound held by do-not-disturb is shown too, so it counts: P81.)</summary>
    internal static bool HasNote(AlertOutcome outcome) => outcome is AlertOutcome.Muted or AlertOutcome.NoWindow or AlertOutcome.Snoozed;

    /// <summary>Whether <paramref name="alert"/> belongs to the run a row shows: the same alert, no older than the run's
    /// first and no newer than its last. The window keeps its selection by this: a re-read makes new objects, and the
    /// selected run may have grown by then.</summary>
    public static bool Holds(HistoryRow row, AlertHistoryEntry alert) =>
        SameAlert(row.Newest, alert) && row.OldestUtc <= alert.TimeUtc && alert.TimeUtc <= row.Newest.TimeUtc;

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
