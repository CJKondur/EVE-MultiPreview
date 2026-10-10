using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>The alert history as a CSV file (IDEAS #1). Two rules, both for every field:
///
/// 1. RFC 4180. A field with a comma, a double quote or a line break stands in double quotes, with every double quote
///    inside it doubled. Records end in CR LF.
/// 2. No formulas. A spreadsheet runs a cell that starts with =, +, - or @ as a formula, and some also one that starts
///    with a tab or a carriage return. A history row can hold text another player typed (what a custom rule
///    matched), so a field that starts with one of those six gets an apostrophe in front: the spreadsheet then shows
///    it as text.
///
/// The file is UTF-8 with a byte order mark: without the mark Excel reads it in the PC's old code page and breaks
/// every name that isn't plain Latin.</summary>
public static class AlertHistoryCsv
{
    /// <summary>UTF-8 with a byte order mark.</summary>
    public static Encoding FileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static readonly char[] NeedsQuotes = { ',', '"', '\r', '\n' };

    /// <summary>One field as it stands in the file.</summary>
    public static string Field(string? value)
    {
        value ??= "";
        if (value.Length > 0 && StartsAFormula(value[0])) value = "'" + value;
        return value.IndexOfAny(NeedsQuotes) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool StartsAFormula(char first) => first is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>One record: the fields with commas between them, ended by CR LF.</summary>
    public static string Record(IEnumerable<string?> fields) => string.Join(",", fields.Select(Field)) + "\r\n";

    /// <summary>What goes into each column, in the order of <see cref="Columns"/>.</summary>
    public static IReadOnlyList<string> Values(AlertHistoryEntry e, Func<AlertHistoryEntry, string> alertName,
        Func<string, string> severityName, Func<AlertOutcome, string> outcomeName, TimeZoneInfo zone) => new[]
    {
        e.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeZoneInfo.ConvertTimeFromUtc(e.TimeUtc, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        e.Character,
        alertName(e),
        severityName(e.Severity),
        e.SolarSystem,
        outcomeName(e.Outcome),
        e.Text,
        e.EventKey,
    };

    /// <summary>The English column names; the window passes them translated.</summary>
    public static IReadOnlyList<string> Columns { get; } = new[]
    {
        "Time (UTC)", "Local time", "Character", "Alert", "Severity", "System", "Outcome", "Matched text", "Alert key",
    };

    /// <summary>The whole file's text: the header record, then one record per alert in the order given.</summary>
    public static string Text(IReadOnlyList<string> header, IEnumerable<AlertHistoryEntry> entries, Func<AlertHistoryEntry, string> alertName,
        Func<string, string> severityName, Func<AlertOutcome, string> outcomeName, TimeZoneInfo zone)
    {
        var text = new StringBuilder(Record(header));
        foreach (var e in entries) text.Append(Record(Values(e, alertName, severityName, outcomeName, zone)));
        return text.ToString();
    }

    /// <summary>Writes the file (replacing one of that name: the player picked it in a save dialog). Throws when it
    /// can't be written.</summary>
    public static void Write(string path, string text) => File.WriteAllText(path, text, FileEncoding);
}
