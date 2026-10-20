using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>What the session report window has to say before it has a table: nothing is set up, nothing was found, or
/// there is something to show.</summary>
public enum StatReportState { NoFolder, NoFiles, Ready }

/// <summary>One row of the days table, as text. The reps cells carry the split by kind as their tooltip.</summary>
public sealed record StatTableRow(string Day, string Active, string Bounty, string Mined, string DamageOut, string DamageIn,
    string RepsOut, string RepsIn, string RepsOutTip, string RepsInTip);

/// <summary>The session report window's content, worked out without any window (IDEAS #20): which characters, their days
/// as table rows, and a number per day for the bar strip. Names are kept as the files have them; the window masks them
/// (StreamerMask) where it draws them.</summary>
public sealed class StatReportModel
{
    private readonly Dictionary<string, CharacterReport> _by;
    private readonly string _repsTip;

    /// <param name="folder">The log folder as set (blank: nothing is set up).</param>
    /// <param name="days">How many days the strip covers, ending today.</param>
    /// <param name="repsTip">The catalog's text for a reps cell's split, with four placeholders (armor, shield, capacitor, other).</param>
    public StatReportModel(string? folder, IReadOnlyList<CharacterReport> reports, DateOnly today, int days, string repsTip)
    {
        Today = today;
        Days = Math.Clamp(days, 1, StatReportFolder.MaxDays);
        Characters = reports.Select(r => r.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        _by = reports.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        _repsTip = repsTip;
        State = string.IsNullOrWhiteSpace(folder) ? StatReportState.NoFolder : reports.Count == 0 ? StatReportState.NoFiles : StatReportState.Ready;
    }

    public StatReportState State { get; }
    public DateOnly Today { get; }
    public int Days { get; }

    /// <summary>The characters with at least one day, in name order.</summary>
    public IReadOnlyList<string> Characters { get; }

    /// <summary>The first day the strip covers.</summary>
    public DateOnly FirstDay => Today.AddDays(1 - Days);

    /// <summary>The days of a character inside the strip, newest first. Nothing for a character that isn't there.</summary>
    public IReadOnlyList<StatDay> DaysOf(string? character) =>
        character != null && _by.TryGetValue(character, out var report)
            ? report.Days.Where(d => d.Day >= FirstDay && d.Day <= Today).OrderByDescending(d => d.Day).ToList()
            : Array.Empty<StatDay>();

    /// <summary>The days added up, one row's worth.</summary>
    public StatDay? Total(string? character)
    {
        var days = DaysOf(character);
        return days.Count == 0 ? null : days.Aggregate((a, b) => a + b) with { Day = Today };
    }

    /// <summary>One number per day of the strip, oldest first, <see cref="Days"/> long; a day without a log is 0.</summary>
    public IReadOnlyList<double> Series(string? character, StatMetric metric)
    {
        var byDay = DaysOf(character).ToDictionary(d => d.Day);
        return Enumerable.Range(0, Days).Select(i => byDay.TryGetValue(FirstDay.AddDays(i), out var d) ? d.Value(metric) : 0d).ToList();
    }

    /// <summary>The table: a row per day, newest first, then the total (named <paramref name="totalLabel"/>) when there is
    /// more than one day.</summary>
    public IReadOnlyList<StatTableRow> Table(string? character, string totalLabel)
    {
        var rows = DaysOf(character).Select(d => Row(d.Day.ToString("d", CultureInfo.CurrentCulture), d)).ToList();
        if (Total(character) is { } total && rows.Count > 1) rows.Add(Row(totalLabel, total));
        return rows;
    }

    private StatTableRow Row(string day, StatDay d) => new(day, Text(StatMetric.Active, d.Value(StatMetric.Active)),
        Text(StatMetric.Bounty, d.Bounty), Text(StatMetric.Mined, d.Mined), Text(StatMetric.DamageOut, d.DamageOut),
        Text(StatMetric.DamageIn, d.DamageIn), Text(StatMetric.RepsOut, d.RepsOut.Total), Text(StatMetric.RepsIn, d.RepsIn.Total),
        Split(d.RepsOut), Split(d.RepsIn));

    private string Split(RepTotals r) => string.Format(CultureInfo.CurrentCulture, _repsTip,
        Number(r.Armor), Number(r.Shield), Number(r.Cap), Number(r.Other));

    /// <summary>A value as the table and the strip's tooltips show it: whole numbers, and the active time as hours and minutes.</summary>
    public static string Text(StatMetric metric, double value) =>
        metric == StatMetric.Active ? PlaytimeLedger.Format((int)Math.Min(int.MaxValue, Math.Max(0, Math.Round(value)))) : Number(value);

    private static string Number(double value) => Math.Round(value).ToString("N0", CultureInfo.CurrentCulture);
}
