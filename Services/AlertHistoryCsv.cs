using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>The alert history as a CSV file (IDEAS #1). Three rules, the first two for every field:
///
/// 1. RFC 4180, and a little more. A field with a comma, a semicolon, a tab, a double quote or a line break stands in
///    double quotes, with every double quote inside it doubled. Records end in CR LF.
/// 2. No formulas. A spreadsheet runs a cell that starts with =, +, - or @ as a formula, some also one that starts
///    with a tab or a carriage return, and one set to an East Asian language also the full-width forms of the first
///    four (U+FF1D, U+FF0B, U+FF0D, U+FF20). A history row can hold text another player typed (what a custom rule
///    matched), so an apostrophe goes in front of each of those ten wherever a cell could begin: the spreadsheet then
///    shows it as text. A cell could begin at the start of the field, and inside it behind every comma, semicolon
///    and tab, behind every carriage return and line feed, and behind this PC's own list separator when that is
///    something else again (the Arabic comma or semicolon, or whatever was typed into the Region settings): a
///    spreadsheet with another list separator, or a reader that doesn't honour the quotes, splits the field or
///    starts a new record there. Double quotes in between don't hide a formula, and at the start of the field
///    neither do white space and characters that don't show (Unicode category Format: a byte order mark, a
///    zero-width space), which a spreadsheet may drop. Nothing else of the field is changed.
/// 3. The separator is the caller's: a semicolon where this PC's list separator is one (German, French, Spanish,
///    Italian, Russian Windows: Excel there opens a comma file as one column), else a comma. Never anything else:
///    a PC that lists with a third thing gets a comma file, and its own separator only counts for rule 2.
///
/// The file is UTF-8 with a byte order mark: without the mark Excel reads it in the PC's old code page and breaks
/// every name that isn't plain Latin.</summary>
public static class AlertHistoryCsv
{
    /// <summary>UTF-8 with a byte order mark.</summary>
    public static Encoding FileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static readonly char[] NeedsQuotes = { ',', ';', '\t', '"', '\r', '\n' };

    /// <summary>The separator for a PC whose list separator (CultureInfo.CurrentCulture.TextInfo.ListSeparator) is
    /// <paramref name="listSeparator"/>: a semicolon for ";", a comma for everything else.</summary>
    public static char SeparatorFor(string? listSeparator) => listSeparator == ";" ? ';' : ',';

    /// <summary>One field as it stands in the file: made harmless (rule 2), then quoted when it needs it (rule 1).</summary>
    public static string Field(string? value) => Field(value, "");

    /// <summary>The same on a PC that lists with <paramref name="ownSeparator"/> ("" for none to look out for).</summary>
    private static string Field(string? value, string ownSeparator)
    {
        value = Harmless(value ?? "", ownSeparator);
        return value.IndexOfAny(NeedsQuotes) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>= + - @, tab and carriage return, and the full-width forms of the first four.</summary>
    private static bool StartsAFormula(char c) =>
        c is '=' or '+' or '-' or '@' or '\t' or '\r' or (char)0xFF1D or (char)0xFF0B or (char)0xFF0D or (char)0xFF20;

    /// <summary>Whether a cell could begin at the next character: not at all, behind a separator or a line break
    /// inside the field (only double quotes are looked past), or at the field's start (white space and characters
    /// that don't show are looked past too).</summary>
    private enum Start { No, BehindASeparator, OfTheField }

    /// <summary>The field with an apostrophe in front of every character that would start a formula where a cell
    /// could begin. Nothing else is added, and nothing is taken away. <paramref name="ownSeparator"/> is this PC's
    /// list separator, "" for none: behind each place it stands in the field a cell could begin as well. Looking out
    /// for one only ever adds apostrophes: every one that is set without it is set with it.</summary>
    private static string Harmless(string value, string ownSeparator)
    {
        StringBuilder? text = null;                             // made at the first apostrophe: most fields need none
        var start = Start.OfTheField;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (start != Start.No && StartsAFormula(c))
            {
                text ??= new StringBuilder(value, 0, i, value.Length + 4);
                text.Append('\'');
            }
            text?.Append(c);
            // Looked up before the second half of a two-unit character is stepped over.
            bool unseen = start == Start.OfTheField
                          && (char.IsWhiteSpace(c) || CharUnicodeInfo.GetUnicodeCategory(value, i) == UnicodeCategory.Format);
            if (char.IsSurrogatePair(value, i))
            {
                i++;
                text?.Append(value[i]);
            }

            if (c is ',' or ';') start = Start.BehindASeparator;
            else if (c == '"' && start != Start.No) { }
            else if (!unseen) start = Start.No;
            // A tab, a carriage return, a line feed and the end of this PC's own separator are places a cell could
            // begin too. Only where none could before: at the field's start a tab or a line break is white space,
            // and was looked past above.
            if (start == Start.No && (c is '\t' or '\r' or '\n' || EndsAt(value, i, ownSeparator))) start = Start.BehindASeparator;
        }
        return text?.ToString() ?? value;
    }

    /// <summary>Whether <paramref name="what"/> stands in <paramref name="value"/> with its last character at
    /// <paramref name="at"/>. Never for "".</summary>
    private static bool EndsAt(string value, int at, string what) =>
        what.Length > 0 && at + 1 >= what.Length && string.CompareOrdinal(value, at + 1 - what.Length, what, 0, what.Length) == 0;

    /// <summary>One record: the fields with the separator between them (a semicolon when asked for one, else a
    /// comma), ended by CR LF.</summary>
    public static string Record(IEnumerable<string?> fields, char separator) => Record(fields, separator, "");

    /// <summary>The same on a PC that lists with <paramref name="ownSeparator"/> ("" for none to look out for).</summary>
    private static string Record(IEnumerable<string?> fields, char separator, string ownSeparator) =>
        string.Join(separator == ';' ? ';' : ',', fields.Select(field => Field(field, ownSeparator))) + "\r\n";

    /// <summary>What goes into each column, in the order of <see cref="Columns"/>. The time is taken the way
    /// <see cref="AlertHistory.Entry"/> takes it (a local time is converted, one that isn't marked is UTC), so an
    /// entry made without it can't make the export throw.</summary>
    public static IReadOnlyList<string> Values(AlertHistoryEntry e, Func<AlertHistoryEntry, string> alertName,
        Func<string, string> severityName, Func<AlertOutcome, string> outcomeName, TimeZoneInfo zone)
    {
        var utc = e.TimeUtc.Kind == DateTimeKind.Local ? e.TimeUtc.ToUniversalTime() : DateTime.SpecifyKind(e.TimeUtc, DateTimeKind.Utc);
        return new[]
        {
            utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            e.Character,
            alertName(e),
            severityName(e.Severity),
            e.SolarSystem,
            outcomeName(e.Outcome),
            e.Text,
            e.EventKey,
        };
    }

    /// <summary>The English column names; the window passes them translated.</summary>
    public static IReadOnlyList<string> Columns { get; } = new[]
    {
        "Time (UTC)", "Local time", "Character", "Alert", "Severity", "System", "Outcome", "Matched text", "Alert key",
    };

    /// <summary>The whole file's text: the header record, then one record per alert in the order given, with
    /// <paramref name="separator"/> between the fields (rule 3). <paramref name="listSeparator"/> is this PC's own
    /// list separator as Windows has it (CultureInfo.CurrentCulture.TextInfo.ListSeparator), null or "" when it isn't
    /// known. It never changes what stands between the fields; where it is neither a comma nor a semicolon, it is
    /// one more place in a field where a spreadsheet on this PC could begin a cell (rule 2).</summary>
    public static string Text(IReadOnlyList<string> header, IEnumerable<AlertHistoryEntry> entries, Func<AlertHistoryEntry, string> alertName,
        Func<string, string> severityName, Func<AlertOutcome, string> outcomeName, TimeZoneInfo zone, char separator, string? listSeparator)
    {
        string own = listSeparator ?? "";
        var text = new StringBuilder(Record(header, separator, own));
        foreach (var e in entries) text.Append(Record(Values(e, alertName, severityName, outcomeName, zone), separator, own));
        return text.ToString();
    }

    /// <summary>Writes the file (replacing one of that name: the player picked it in a save dialog). Throws when it
    /// can't be written.</summary>
    public static void Write(string path, string text) => File.WriteAllText(path, text, FileEncoding);
}
