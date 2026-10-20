using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>One logged-in client as the thumbnails know it, taken on the UI thread (<c>ThumbnailManager.SnapshotCharacters</c>):
/// the character, when its thumbnail's session began (UTC) and the process that owns the client's window (0 when unknown).</summary>
public sealed record CharacterSnapshot(string Name, DateTime SessionStartUtc, int ProcessId);

/// <summary>Everything the fleet board (IDEAS #18) knows about one character, handed in so the rows are a pure function of it.
/// <paramref name="System"/> is "" when not known. <paramref name="SystemSinceUtc"/> is when MultiPreview saw the character
/// arrive there (null when not known). <paramref name="Stats"/> is null when the process monitor has no reading.
/// <paramref name="Alerts"/> are this character's alerts in the alert history, which holds those raised since MultiPreview
/// started (the newest 2,000 of all characters). <paramref name="SinceApproximate"/>: the time is not the moment of arrival but
/// the last write of the Local chat log the start-up system was read from, which moves on every chat line, so the real arrival
/// can be earlier. A jump MultiPreview saw itself is exact.</summary>
public sealed record FleetBoardInput(string Name, string System, DateTime? SystemSinceUtc, DateTime SessionStart,
    ProcessMonitorService.ProcessStats? Stats, IReadOnlyList<AlertHistoryEntry> Alerts, bool SinceApproximate = false);

/// <summary>The board's columns, in the order the window shows them.</summary>
public enum FleetBoardColumn { Name, System, Since, Session, LastAlert, Alerts, Cpu, Ram }

/// <summary>One row of the board. The raw values sort; the texts are what a cell shows, with <see cref="FleetBoardRows.Missing"/>
/// for what is not known. <see cref="Name"/> and <see cref="System"/> are already masked for streamer mode.</summary>
public sealed record FleetBoardRow(string Name, string System, DateTime? SinceUtc, TimeSpan Session, TimeSpan? LastAlertAge, int Alerts,
    double? Cpu, long? RamMB, string SinceText, string SinceTip, string SessionText, string LastAlertText, string CpuText, string RamText);

/// <summary>The fleet board's rows (IDEAS #18): a table with one row per character, built from numbers handed in (the clock
/// too), so none of it needs a window, a process or a log. The window only draws what this gives it.</summary>
public static class FleetBoardRows
{
    /// <summary>What a cell shows when the value is not known (no process reading, no alert, no system).</summary>
    public const string Missing = "—";

    /// <summary>Put before a time that is only an estimate of the arrival (see <see cref="FleetBoardInput.SinceApproximate"/>).
    /// "~", not "≥" or "≤": the real arrival can only be earlier, but the cell is read as "about then".</summary>
    public const string ApproximateMark = "~";

    /// <summary>The rows for <paramref name="inputs"/>, in the default order: by the name as it is shown (so the order on a
    /// stream is the order of the numbers on it, not of the real names). <paramref name="mask"/> turns a character name into
    /// what is drawn, <paramref name="maskSystem"/> a system name (the same function when null). A name is only ever passed
    /// to these functions; it is not kept anywhere else, logged or put into a format string.</summary>
    public static IReadOnlyList<FleetBoardRow> Build(IReadOnlyList<FleetBoardInput> inputs, DateTime nowUtc, Func<string, string> mask,
        Func<string, string>? maskSystem = null)
    {
        maskSystem ??= mask;
        // One row per character: two windows under one name (a stale one beside the live one) show the newer session.
        var one = inputs.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(i => i.SessionStart).First());
        return Sort(one.Select(i => RowOf(i, nowUtc, mask, maskSystem)), FleetBoardColumn.Name, descending: false);
    }

    private static FleetBoardRow RowOf(FleetBoardInput i, DateTime nowUtc, Func<string, string> mask, Func<string, string> maskSystem)
    {
        var session = Positive(nowUtc - i.SessionStart);
        TimeSpan? lastAge = i.Alerts.Count == 0 ? null : Positive(nowUtc - i.Alerts.Max(a => a.TimeUtc));
        var stats = i.Stats;
        string sinceText = Missing, sinceTip = "";
        if (i.SystemSinceUtc is { } since)
        {
            var local = since.ToLocalTime();
            sinceText = local.ToString(local.Date == nowUtc.ToLocalTime().Date ? "t" : "g", CultureInfo.CurrentCulture);   // the date only when it is not today
            sinceTip = AlertAge.Text(Positive(nowUtc - since));
            if (i.SinceApproximate)
            {
                sinceText = ApproximateMark + sinceText;
                sinceTip = string.Format(CultureInfo.CurrentCulture, LocalizationService.Str("L.Fleet.Tip.SinceApprox",
                    "Time since: {0}. The character was already here when MultiPreview started, so it may have arrived earlier than the time shown."), sinceTip);
            }
        }
        return new FleetBoardRow(
            mask(i.Name),
            string.IsNullOrEmpty(i.System) ? Missing : maskSystem(i.System),
            i.SystemSinceUtc, session, lastAge, i.Alerts.Count,
            stats?.CpuPercent, stats?.RamMB,
            sinceText, sinceTip,
            (long)session.TotalHours + ":" + session.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + session.Seconds.ToString("00", CultureInfo.InvariantCulture),
            lastAge is { } age ? AlertAge.Text(age) : Missing,
            stats is null ? Missing : stats.CpuPercent.ToString("F1", CultureInfo.CurrentCulture) + "%",
            stats is null ? Missing : stats.RamMB.ToString("N0", CultureInfo.CurrentCulture) + " MB");
    }

    /// <summary>A time that ran backwards (the PC's clock was set back) counts as none, never as a negative.</summary>
    private static TimeSpan Positive(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

    /// <summary>The rows ordered by a column's number (not its text: 1,000 MB is more than 999 MB). A row with nothing in the
    /// column is last in either direction; rows that tie are in name order, whichever way the sort runs. Stable.</summary>
    public static IReadOnlyList<FleetBoardRow> Sort(IEnumerable<FleetBoardRow> rows, FleetBoardColumn column, bool descending)
    {
        int Compare(FleetBoardRow a, FleetBoardRow b)
        {
            var ka = KeyOf(a, column);
            var kb = KeyOf(b, column);
            if (ka is null || kb is null)
            {
                if (ka is not null) return -1;
                if (kb is not null) return 1;
            }
            else
            {
                int c = ka is string s ? string.Compare(s, (string)kb, StringComparison.OrdinalIgnoreCase) : ka.CompareTo(kb);
                if (c != 0) return descending ? -c : c;
            }
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
        return rows.OrderBy(r => r, Comparer<FleetBoardRow>.Create(Compare)).ToList();
    }

    private static IComparable? KeyOf(FleetBoardRow r, FleetBoardColumn column) => column switch
    {
        FleetBoardColumn.Name => r.Name,
        FleetBoardColumn.System => r.System == Missing ? null : r.System,
        FleetBoardColumn.Since => r.SinceUtc,
        FleetBoardColumn.Session => r.Session,
        FleetBoardColumn.LastAlert => r.LastAlertAge,
        FleetBoardColumn.Alerts => r.Alerts,
        FleetBoardColumn.Cpu => r.Cpu,
        _ => r.RamMB,
    };

    /// <summary>Joins the characters with what the rest of the app knows about each. The lookups are handed in (the log monitor's
    /// system and time, the process monitor's reading, the alert history's list), so a test gives it fakes. A character's alerts
    /// are the history's entries under its name, in any letter case, whatever became of them (shown, muted, snoozed). A character with no
    /// known process is never looked up. <paramref name="sinceApproximate"/> says, per character, that its time in the system is only
    /// the last write of a Local log (<see cref="FleetBoardInput.SinceApproximate"/>); without it every time counts as exact.</summary>
    public static IReadOnlyList<FleetBoardInput> Gather(IReadOnlyList<CharacterSnapshot> characters, IReadOnlyList<AlertHistoryEntry> alerts,
        Func<string, string?> systemOf, Func<string, DateTime?> sinceOf, Func<int, ProcessMonitorService.ProcessStats?> statsOf,
        Func<string, bool>? sinceApproximate = null)
    {
        var byName = alerts.GroupBy(a => a.Character, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AlertHistoryEntry>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        return characters.Select(c => new FleetBoardInput(
            c.Name, systemOf(c.Name) ?? "", sinceOf(c.Name), c.SessionStartUtc,
            c.ProcessId > 0 ? statsOf(c.ProcessId) : null,
            byName.GetValueOrDefault(c.Name) ?? Array.Empty<AlertHistoryEntry>(),
            sinceApproximate?.Invoke(c.Name) ?? false)).ToList();
    }
}
