using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>What one stat log line counts as (IDEAS #20). The writer (StatTrackerService.LogCsv) names a line's type
/// BOUNTY, MINE_ORE, DMG_OUT, REP_ARMOR_IN ...; any other type is <see cref="Other"/>: it still shows that the character
/// was active, and nothing more.</summary>
public enum StatKind
{
    Other, Bounty, MineOre, MineGas, MineIce, DamageOut, DamageIn,
    RepArmorOut, RepArmorIn, RepShieldOut, RepShieldIn, RepCapOut, RepCapIn, RepOtherOut, RepOtherIn,
}

/// <summary>One line of a stat log: when (UTC), what, how much.</summary>
public readonly record struct StatRow(DateTime TimeUtc, StatKind Kind, double Amount);

/// <summary>Reads a stat log (<c>StatLog_&lt;character&gt;_&lt;date&gt;.csv</c>: <c>Timestamp,Type,Amount</c>, the file
/// StatTrackerService writes) back, defensively: the file is the player's, may be half written by a crash, and may be
/// anything at all. A line that is not exactly three fields, with a UTC round-trip timestamp, a type of capitals, digits
/// and underscores, and a plain non-negative amount, is skipped. Reading is streamed in chunks and bounded three ways:
/// <see cref="MaxLines"/> lines, <see cref="MaxChars"/> characters, and <see cref="MaxLineChars"/> characters held for one
/// line, so a 100 MB file, or one line of 100 MB, costs time and never memory. Nothing in a line is ever logged.</summary>
public static class StatLogReader
{
    /// <summary>Lines (the header and bad lines count) read from one file; the rest is ignored.</summary>
    public const int MaxLines = 1_000_000;

    /// <summary>Characters read from one file; the rest is ignored. A real line is about 60 characters.</summary>
    public const long MaxChars = 100_000_000;

    /// <summary>A longer line is garbage and is dropped without being held (a real one is under 80).</summary>
    public const int MaxLineChars = 256;

    private const double MaxAmount = 1e15;

    /// <summary>The rows of a stat log, in file order. The header, an empty or malformed line, and a last line that has no
    /// line break after it (half written: its amount may be cut short and still look right) are skipped.</summary>
    public static IReadOnlyList<StatRow> Parse(TextReader reader)
    {
        var rows = new List<StatRow>();
        var chunk = new char[8192];
        var line = new char[MaxLineChars];
        int length = 0, lines = 0, got;
        bool tooLong = false;
        long read = 0;
        while (lines < MaxLines && read < MaxChars && (got = reader.Read(chunk, 0, chunk.Length)) > 0)
        {
            read += got;
            for (int i = 0; i < got && lines < MaxLines; i++)
            {
                char c = chunk[i];
                if (c == '\n')
                {
                    lines++;
                    if (!tooLong && TryParseLine(line.AsSpan(0, length), out var row)) rows.Add(row);
                    length = 0;
                    tooLong = false;
                }
                else if (c == '\r') continue;
                else if (length < MaxLineChars) line[length++] = c;
                else tooLong = true;
            }
        }
        return rows;
    }

    private static bool TryParseLine(ReadOnlySpan<char> line, out StatRow row)
    {
        row = default;
        int first = line.IndexOf(',');
        if (first <= 0) return false;
        int second = line[(first + 1)..].IndexOf(',');
        if (second < 0) return false;
        second += first + 1;
        var amountText = line[(second + 1)..];
        if (amountText.IndexOf(',') >= 0) return false;                       // four fields: two lines run together

        if (!DateTime.TryParseExact(line[..first], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            || time.Kind != DateTimeKind.Utc) return false;
        var type = line[(first + 1)..second];
        if (type.Length is < 1 or > 48) return false;
        foreach (char c in type)
            if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
        if (amountText.Length is < 1 or > 24
            || !double.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || double.IsNaN(amount) || amount < 0 || amount > MaxAmount) return false;

        row = new StatRow(time, KindOf(type), amount);
        return true;
    }

    /// <summary>The kind of a type as the writer spells it. Armor, shield and capacitor repairs are told apart; hull and
    /// anything the writer was given are "other" repairs (the tracker counts them as armor, the log does not say).</summary>
    public static StatKind KindOf(ReadOnlySpan<char> type)
    {
        if (type.SequenceEqual("BOUNTY")) return StatKind.Bounty;
        if (type.SequenceEqual("MINE_ORE")) return StatKind.MineOre;
        if (type.SequenceEqual("MINE_GAS")) return StatKind.MineGas;
        if (type.SequenceEqual("MINE_ICE")) return StatKind.MineIce;
        if (type.SequenceEqual("DMG_OUT")) return StatKind.DamageOut;
        if (type.SequenceEqual("DMG_IN")) return StatKind.DamageIn;
        if (type.StartsWith("REP_"))
        {
            bool outgoing = type.EndsWith("_OUT");
            if (!outgoing && !type.EndsWith("_IN")) return StatKind.Other;
            int suffix = outgoing ? 4 : 3;
            if (type.Length < 4 + suffix) return StatKind.Other;                  // "REP_OUT": nothing between the two
            var what = type[4..^suffix];
            if (what.SequenceEqual("ARMOR")) return outgoing ? StatKind.RepArmorOut : StatKind.RepArmorIn;
            if (what.SequenceEqual("SHIELD")) return outgoing ? StatKind.RepShieldOut : StatKind.RepShieldIn;
            if (what.SequenceEqual("CAPACITOR") || what.SequenceEqual("CAP")) return outgoing ? StatKind.RepCapOut : StatKind.RepCapIn;
            return outgoing ? StatKind.RepOtherOut : StatKind.RepOtherIn;
        }
        return StatKind.Other;
    }
}
