using System;
using System.Collections.Generic;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #4: the two (combat) lines of an English EVE client that say an energy neutralizer took capacitor
/// from this ship, and that this ship is jammed. Each is an alert event of its own ("neutralized", "jammed") that is
/// off until the player switches it on. The wording and the markup are those of a real game log (the test fixtures
/// hold one line of each: combat.neut.in and combat.jammed). A line is taken only when it begins with exactly that,
/// directly behind its own (combat) tag: EVE writes the markup, and nothing a player can name holds a tag, so a
/// ship, a module or a pilot named like the phrase is never taken for the line.
///
/// What is NOT read, because no real line was at hand: a nosferatu's line ("energy drained"), a neutralizer of the
/// player's own on somebody else (another colour, by IDEAS #4), and the wording of the eight other client languages.
/// A line whose source is a player was not at hand either; the two lines are told by how they begin, which does not
/// depend on the source.
///
/// On the reader's path: comparisons of how a line begins, no pattern, and nothing allocated unless the line is one
/// of the two (SourceName, for PvE mode).</summary>
public static class EwarLines
{
    public const string Neutralized = "neutralized", Jammed = "jammed";
    public const string NeutralizedSeverity = "critical", JammedSeverity = "warning";
    public const int NeutralizedBit = 1, JammedBit = 2;

    /// <summary>A neutralizer and a jam are states, not hits: the log has a line for every cycle of the module, and
    /// the player is told once when it starts and then not again for this many seconds, for that character and that
    /// event. The reader hands it to its raise as the event's own quiet time; the quiet time of Settings counts
    /// where it is the longer of the two.</summary>
    public const int QuietSeconds = 30;

    /// <summary>The most characters an amount may have ("1,234.5").</summary>
    public const int MaxAmountLength = 12;

    private const string Tag = " (combat) ";
    private const string NeutOpens = "<color=0xffe57f7f><b>";
    private const string NeutPhrase = " GJ</b><color=0x77ffffff><font size=10> energy neutralized </font>";
    private const string JamOpens = "<color=0x77ffffff><font size=10>You're</font> <color=0xffffffff><b>jammed</b> <color=0x77ffffff><font size=10>by</font> ";
    private const string ModuleOpens = "<font size=10> - ";

    /// <summary>Which of the two events are switched on, as bits. A missing key is off: both are off until the
    /// player switches them on, the other way round from the events that were always there.</summary>
    public static int Bits(IReadOnlyDictionary<string, bool>? enabled) =>
        enabled == null ? 0
        : (enabled.TryGetValue(Neutralized, out var neutralized) && neutralized ? NeutralizedBit : 0)
          | (enabled.TryGetValue(Jammed, out var jammed) && jammed ? JammedBit : 0);

    /// <summary>Reads a game-log line. True when it is one of the two lines and that event is on: then the event's
    /// key, its severity and the rest of the line, which names the source and the module.</summary>
    /// <param name="on">The events that are on (Bits). With none on, nothing is read.</param>
    public static bool TryRead(string line, int on, out string eventKey, out string severity, out ReadOnlySpan<char> source)
    {
        eventKey = severity = "";
        source = default;
        if (on == 0 || line == null) return false;

        // The line's own tag, as LogMonitorService.ReadOwnTag reads it: behind the "]" that closes the timestamp.
        var text = line.AsSpan().TrimStart();
        if (text.Length == 0 || text[0] != '[') return false;
        int close = text.IndexOf(']');
        if (close < 0 || !text[(close + 1)..].StartsWith(Tag, StringComparison.Ordinal)) return false;
        var body = text[(close + 1 + Tag.Length)..];

        if ((on & NeutralizedBit) != 0 && body.StartsWith(NeutOpens, StringComparison.OrdinalIgnoreCase))
        {
            // The amount: digits, with or without separators ("12", "1,234", "12.5").
            var rest = body[NeutOpens.Length..];
            int at = 0, digits = 0;
            while (at < rest.Length && at < MaxAmountLength && (char.IsAsciiDigit(rest[at]) || rest[at] is ',' or '.'))
            {
                if (char.IsAsciiDigit(rest[at])) digits++;
                at++;
            }
            if (digits == 0 || !rest[at..].StartsWith(NeutPhrase, StringComparison.OrdinalIgnoreCase)) return false;
            eventKey = Neutralized;
            severity = NeutralizedSeverity;
            source = rest[(at + NeutPhrase.Length)..];
            return true;
        }

        if ((on & JammedBit) != 0 && body.StartsWith(JamOpens, StringComparison.OrdinalIgnoreCase))
        {
            eventKey = Jammed;
            severity = JammedSeverity;
            source = body[JamOpens.Length..];
            return true;
        }
        return false;
    }

    /// <summary>The source's name as plain text: what TryRead handed out, without the module at its end (" - ..."
    /// in small type) and without markup. A player's then reads "Name [TICKER] Ship type"; an NPC's is its name.</summary>
    public static string SourceName(ReadOnlySpan<char> source)
    {
        int module = source.LastIndexOf(ModuleOpens, StringComparison.OrdinalIgnoreCase);
        if (module >= 0) source = source[..module];
        var name = new StringBuilder(source.Length);
        bool inTag = false;
        foreach (char c in source)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) name.Append(c);
        }
        return name.ToString().Trim();
    }
}
