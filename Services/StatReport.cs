using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>Repairs (or capacitor transfers) of one direction in a day, by what was repaired.</summary>
public readonly record struct RepTotals(double Armor, double Shield, double Cap, double Other)
{
    public double Total => Armor + Shield + Cap + Other;
    public static RepTotals operator +(RepTotals a, RepTotals b) => new(a.Armor + b.Armor, a.Shield + b.Shield, a.Cap + b.Cap, a.Other + b.Other);
}

/// <summary>The numbers a report can chart.</summary>
public enum StatMetric { Bounty, Mined, DamageOut, DamageIn, RepsOut, RepsIn, Active }

/// <summary>One character's day, from one or more stat logs. <see cref="Mined"/> is in units (ore, gas and ice together):
/// the log does not carry an ore's volume, so there is no m³. <see cref="Active"/> is an estimate, see
/// <see cref="StatReport.ActiveGap"/>.</summary>
public sealed record StatDay(DateOnly Day, double Bounty, double Mined, double DamageOut, double DamageIn,
    RepTotals RepsOut, RepTotals RepsIn, TimeSpan Active)
{
    public double Value(StatMetric metric) => metric switch
    {
        StatMetric.Bounty => Bounty,
        StatMetric.Mined => Mined,
        StatMetric.DamageOut => DamageOut,
        StatMetric.DamageIn => DamageIn,
        StatMetric.RepsOut => RepsOut.Total,
        StatMetric.RepsIn => RepsIn.Total,
        _ => Active.TotalSeconds,
    };

    public static StatDay operator +(StatDay a, StatDay b) => new(a.Day, a.Bounty + b.Bounty, a.Mined + b.Mined,
        a.DamageOut + b.DamageOut, a.DamageIn + b.DamageIn, a.RepsOut + b.RepsOut, a.RepsIn + b.RepsIn, a.Active + b.Active);
}

/// <summary>A stat log as it was read: whose (from the file's name), which local day (from the file's name, never from the
/// rows: the writer names the file by the local date it wrote it) and its lines.</summary>
public sealed record StatLogFile(string Character, DateOnly Day, IReadOnlyList<StatRow> Rows);

/// <summary>One character's days, oldest first.</summary>
public sealed record CharacterReport(string Name, IReadOnlyList<StatDay> Days);

/// <summary>Session reports (IDEAS #20): the numbers of the stat logs, per character per day. Pure.</summary>
public static class StatReport
{
    /// <summary>Two log lines this far apart or closer are one stretch of play; a longer silence is not counted. The
    /// active time is an estimate: the log only says when something happened, never when the player was there.</summary>
    public static readonly TimeSpan ActiveGap = TimeSpan.FromMinutes(5);

    /// <summary>One file's day: the amounts added up by kind, and the estimated active time (the sum of the gaps of at most
    /// <see cref="ActiveGap"/> between neighbouring lines in time order; one line is no time).</summary>
    public static StatDay Summarise(DateOnly day, IReadOnlyList<StatRow> rows)
    {
        double bounty = 0, mined = 0, damageOut = 0, damageIn = 0;
        double armorOut = 0, shieldOut = 0, capOut = 0, otherOut = 0, armorIn = 0, shieldIn = 0, capIn = 0, otherIn = 0;
        var times = new DateTime[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            times[i] = row.TimeUtc;
            switch (row.Kind)
            {
                case StatKind.Bounty: bounty += row.Amount; break;
                case StatKind.MineOre: case StatKind.MineGas: case StatKind.MineIce: mined += row.Amount; break;
                case StatKind.DamageOut: damageOut += row.Amount; break;
                case StatKind.DamageIn: damageIn += row.Amount; break;
                case StatKind.RepArmorOut: armorOut += row.Amount; break;
                case StatKind.RepShieldOut: shieldOut += row.Amount; break;
                case StatKind.RepCapOut: capOut += row.Amount; break;
                case StatKind.RepOtherOut: otherOut += row.Amount; break;
                case StatKind.RepArmorIn: armorIn += row.Amount; break;
                case StatKind.RepShieldIn: shieldIn += row.Amount; break;
                case StatKind.RepCapIn: capIn += row.Amount; break;
                case StatKind.RepOtherIn: otherIn += row.Amount; break;
            }
        }
        Array.Sort(times);
        var active = TimeSpan.Zero;
        for (int i = 1; i < times.Length; i++)
        {
            var gap = times[i] - times[i - 1];
            if (gap <= ActiveGap) active += gap;
        }
        return new StatDay(day, bounty, mined, damageOut, damageIn,
            new RepTotals(armorOut, shieldOut, capOut, otherOut), new RepTotals(armorIn, shieldIn, capIn, otherIn), active);
    }

    /// <summary>The files grouped by character (names compared ignoring case; the first spelling is kept) and day; characters
    /// in name order, days oldest first. A file with no usable line makes no day. Two files of one character and day are
    /// added together. The files are taken one at a time, so a caller can read each lazily and hold one file's lines at once.</summary>
    public static IReadOnlyList<CharacterReport> Aggregate(IEnumerable<StatLogFile> files)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var days = new Dictionary<string, SortedDictionary<DateOnly, StatDay>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Rows.Count == 0) continue;
            var day = Summarise(file.Day, file.Rows);
            if (!days.TryGetValue(file.Character, out var byDay))
            {
                days[file.Character] = byDay = new SortedDictionary<DateOnly, StatDay>();
                names[file.Character] = file.Character;
            }
            byDay[file.Day] = byDay.TryGetValue(file.Day, out var before) ? before + day : day;
        }
        return days.OrderBy(kv => names[kv.Key], StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => new CharacterReport(names[kv.Key], kv.Value.Values.ToList())).ToList();
    }
}
