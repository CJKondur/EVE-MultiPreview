using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>One alert rule of the player's own (IDEAS #8), as custom_alerts.json holds it. Kind, Source and Severity
/// are words, so a file written by hand or by a later version reads; a word this version doesn't know makes the
/// rule one that can't be switched on (CustomRuleCheck), never an error.</summary>
public sealed class CustomAlertRule
{
    /// <summary>4 to 32 lower-case letters and digits, made by MultiPreview. The alert type is "custom:" + Id.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>What the toast, the thumbnail and the history call the alert. Shown as plain text.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    /// <summary>"text": the line contains Pattern. "regex": Pattern is a regular expression.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = CustomRules.KindText;
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
    [JsonPropertyName("caseSensitive")] public bool CaseSensitive { get; set; }
    /// <summary>"game": the game log. "chat": the chat channel named by Channel.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = CustomRules.SourceGame;
    /// <summary>The channel as its chat log files are named: the part before the date ("Fleet", "Corp", "Local").</summary>
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    /// <summary>The characters whose logs the rule reads. Empty: every character.</summary>
    [JsonPropertyName("characters")] public List<string> Characters { get; set; } = new();
    [JsonPropertyName("severity")] public string Severity { get; set; } = "warning";
    /// <summary>A sound file of the rule's own, or "" for the default alert sound.</summary>
    [JsonPropertyName("sound")] public string Sound { get; set; } = "";
    /// <summary>Seconds before the rule alerts again for one character, or null for the quiet time every alert has.</summary>
    [JsonPropertyName("quietSeconds")] public int? QuietSeconds { get; set; }
    /// <summary>Chat only: a line the listening character wrote itself raises nothing.</summary>
    [JsonPropertyName("ignoreOwn")] public bool IgnoreOwn { get; set; } = true;

    [JsonIgnore] public string AlertType => CustomAlertTypes.Prefix + Id;
    [JsonIgnore] public bool IsRegex => Kind == CustomRules.KindRegex;
    [JsonIgnore] public bool IsChat => Source == CustomRules.SourceChat;

    /// <summary>A copy of its own, with no null in it (a file can say "name": null).</summary>
    public CustomAlertRule Clone() => new()
    {
        Id = Id ?? "", Name = Name ?? "", Enabled = Enabled, Kind = Kind ?? "", Pattern = Pattern ?? "", CaseSensitive = CaseSensitive,
        Source = Source ?? "", Channel = Channel ?? "", Characters = Characters == null ? new List<string>() : new List<string>(Characters),
        Severity = Severity ?? "", Sound = Sound ?? "", QuietSeconds = QuietSeconds, IgnoreOwn = IgnoreOwn,
    };
}

/// <summary>The words and limits of custom rules.</summary>
public static class CustomRules
{
    public const string KindText = "text", KindRegex = "regex";
    public const string SourceGame = "game", SourceChat = "chat";
    public static readonly IReadOnlyList<string> Severities = new[] { "critical", "warning", "info" };

    public const int MaxRules = 50;
    public const int MaxName = 40, MaxPattern = 500, MaxChannel = 60, MaxCharacters = 50, MaxSound = 400;
    public const int MaxQuietSeconds = 3600;
    /// <summary>Chat channels read for rules, beyond Local. Each one is a folder listing at every scan.</summary>
    public const int MaxChannels = 10;
    /// <summary>A log line longer than this is cut before a rule sees it; what a rule sees is cut again to MaxText.</summary>
    public const int MaxRawLine = 8192, MaxText = 2000;
    /// <summary>The most text the editor's Test box takes: a log line, with room to spare.</summary>
    public const int MaxPasted = 16384;
    // What regular-expression rules may cost the thread that reads every client's logs (CustomAlertEngine has the
    // bounds). Plain-text rules are a search for a word: they are never counted and never skipped.
    /// <summary>The longest one regular expression may take on one line: .NET gives the match up after it. A healthy
    /// pattern needs microseconds, so 50 ms is a pattern gone wrong, and still short enough for the lines behind.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    /// <summary>A rule that slow on this many lines of one hundred (WindowLines) is switched off. Three, so one or
    /// two stalls of the PC cost no rule; and in one hundred lines, because the 50 ms are counted by the wall clock:
    /// on a PC that is short of CPU a healthy rule is held up now and then, and over a session that adds up to three.</summary>
    public const int TimeoutsBeforeOff = 3;
    /// <summary>What all regular expressions together may take on one line; the rest of them skip that line. One time
    /// limit, so no line waits for more than two: this one, and the match that was running when it ran out.</summary>
    public static readonly TimeSpan LineBudget = TimeSpan.FromMilliseconds(50);
    /// <summary>The lines an average is taken over, and within which strikes count: enough that one slow line is no
    /// verdict, few enough that a costly rule is gone within a second of a fight. Only lines on which a regular
    /// expression ran are counted.</summary>
    public const int WindowLines = 100;
    /// <summary>What the regular expressions may cost a line on average. Fifty plausible ones cost a tenth of it on
    /// short lines and under half of it on lines of 2,000 characters on a fast PC, so a PC several times slower still
    /// keeps them; and at this cost the reader still reads five hundred lines a second.</summary>
    public static readonly TimeSpan AverageBudget = TimeSpan.FromMilliseconds(2);
}

/// <summary>Why a rule can't be switched on. The editor shows "L.S.Rules.Err." + the name.</summary>
public enum CustomRuleProblem
{
    None, NameEmpty, PatternEmpty, PatternTooLong, PatternInvalid, KindUnknown, SourceUnknown, ChannelEmpty, ChannelInvalid,
    TooManyChannels, SeverityUnknown,
}

/// <summary>Whether a rule can run.</summary>
public static class CustomRuleCheck
{
    private static readonly char[] NotInAChannel = Path.GetInvalidFileNameChars().Concat(new[] { '*', '?' }).Distinct().ToArray();

    /// <summary>What is wrong with a rule, or None. With <paramref name="all"/>, also whether its channel is one of
    /// the ten that are read.</summary>
    public static CustomRuleProblem Problem(CustomAlertRule rule, IReadOnlyList<CustomAlertRule>? all = null)
    {
        var problem = Shape(rule, all);
        if (problem != CustomRuleProblem.None) return problem;
        return rule.IsRegex && CustomRulePatterns.Get(rule.Pattern, rule.CaseSensitive) == null ? CustomRuleProblem.PatternInvalid : CustomRuleProblem.None;
    }

    /// <summary>Everything but whether a regular expression reads: the engine builds each pattern once and sees that
    /// for itself.</summary>
    internal static CustomRuleProblem Shape(CustomAlertRule rule, IReadOnlyList<CustomAlertRule>? all)
    {
        // A name as the store keeps it (PlainText.OneLine): one of nothing that shows is no name.
        if (PlainText.OneLine(rule.Name, CustomRules.MaxName).Length == 0) return CustomRuleProblem.NameEmpty;
        if (rule.Kind != CustomRules.KindText && rule.Kind != CustomRules.KindRegex) return CustomRuleProblem.KindUnknown;
        if (string.IsNullOrEmpty(rule.Pattern)) return CustomRuleProblem.PatternEmpty;
        if (rule.Pattern.Length > CustomRules.MaxPattern) return CustomRuleProblem.PatternTooLong;
        if (rule.Source != CustomRules.SourceGame && rule.Source != CustomRules.SourceChat) return CustomRuleProblem.SourceUnknown;
        if (rule.IsChat)
        {
            var problem = ChannelProblem(rule);
            if (problem != CustomRuleProblem.None) return problem;
            if (all != null && !ChannelsRead(all).Contains(rule.Channel.Trim(), StringComparer.OrdinalIgnoreCase)) return CustomRuleProblem.TooManyChannels;
        }
        return CustomRules.Severities.Contains(rule.Severity) ? CustomRuleProblem.None : CustomRuleProblem.SeverityUnknown;
    }

    private static CustomRuleProblem ChannelProblem(CustomAlertRule rule)
    {
        var channel = rule.Channel?.Trim() ?? "";
        if (channel.Length == 0) return CustomRuleProblem.ChannelEmpty;
        // The name becomes a file pattern ("Fleet_*.txt"): nothing a file name can't hold, no wildcard, no path.
        return channel.Length > CustomRules.MaxChannel || channel.IndexOfAny(NotInAChannel) >= 0 || channel.EndsWith('.')
            ? CustomRuleProblem.ChannelInvalid
            : CustomRuleProblem.None;
    }

    private static readonly char[] NotInASound = Path.GetInvalidPathChars().Concat(new[] { '*', '?' }).Distinct().ToArray();
    private static readonly string[] Devices = { "CON", "PRN", "AUX", "NUL" };

    /// <summary>A rule's sound as it may be kept: a path that starts with a drive letter ("C:\Sounds\ping.wav"), else
    /// "". A rules file can come from another player. A path to another computer ("\\server\share\ping.wav", or the
    /// same behind "\\?\") would make Windows log in to that computer when the alert plays, and a web address is no
    /// file: none of them is ever kept, played or looked at. Nor is a path that is no plain file name under its drive
    /// letter: one with a wildcard, with a second colon (a stream of a file), with a "." or ".." part, with a part that
    /// ends in a dot or a space (Windows reads another name than is written), or with a device's name for a part
    /// (CON, PRN, AUX, NUL, COM1, LPT1 ..., with or without an extension). Only the text is looked at, never the
    /// disk: a drive letter can still be a drive of another computer, as it can for every other sound setting.</summary>
    public static string LocalSound(string? sound)
    {
        var path = (sound ?? "").Trim();
        bool local = path.Length >= 4 && path.Length <= CustomRules.MaxSound
            && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
            && path.IndexOfAny(NotInASound) < 0
            && path.IndexOf(':', 2) < 0
            && path[3..].Split('\\', '/').All(IsAFileName);
        return local ? path : "";
    }

    /// <summary>Whether one part of a path between two separators is a name Windows takes as written. An empty part
    /// (two separators in a row) is none at all and passes, as it always did.</summary>
    private static bool IsAFileName(string part)
    {
        if (part.Length == 0) return true;
        if (part[^1] == '.' || part[^1] == ' ') return false;                // also "." and ".."
        int dot = part.IndexOf('.');
        var name = (dot < 0 ? part : part[..dot]).TrimEnd(' ');
        if (Devices.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        // COM1 to COM9 and LPT1 to LPT9; Windows also reserves the digit 0 and the superscript digits 1, 2 and 3.
        return !(name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (char.IsAsciiDigit(name[3]) || name[3] == (char)0xB9 || name[3] == (char)0xB2 || name[3] == (char)0xB3));
    }

    /// <summary>The chat channels the rules name, each once, in the rules' order: the first ten are read. A rule
    /// counts whether it is on or off, so switching one on never takes a channel away from another.</summary>
    public static IReadOnlyList<string> ChannelsRead(IReadOnlyList<CustomAlertRule> rules) =>
        rules.Where(r => r.IsChat && ChannelProblem(r) == CustomRuleProblem.None)
             .Select(r => r.Channel.Trim())
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .Take(CustomRules.MaxChannels).ToList();
}

/// <summary>What a rule looks at. The log monitor and the editor's Test box both go through here, so a rule that
/// passes the test fires in the game. Both hand over a line whose localized names are already resolved
/// (LogMonitorService.ResolveLocalizedNames): the monitor does that to every line before anything looks at it, and
/// the Test box (CustomAlertEngine.Test) does the same to the pasted line, once.</summary>
public static class CustomRuleText
{
    /// <summary>A game-log line as a rule sees it: the timestamp gone, EVE's markup (&lt;color&gt;, &lt;b&gt;,
    /// &lt;font&gt; ...) gone, runs of white space made one space. The line's tag stays: "(combat) 123 from Guristas
    /// Despoiler - Hits". Null when the line is not a log entry: no timestamp at its start (a header, or the second
    /// line of a long message), unless <paramref name="pasted"/> says the text was pasted by the player, who may have
    /// copied it without one.</summary>
    public static string? Game(string line, bool pasted = false)
    {
        var body = Body(Cut(line), pasted);
        return body == null ? null : Tidy(body, stripMarkup: true);
    }

    /// <summary>A chat line as a rule sees it: "Speaker &gt; message", the timestamp gone. Null when the line is not
    /// a message. <paramref name="speaker"/> is who wrote it: the client writes that field itself, before the first
    /// "&gt;" (a name can't hold one).</summary>
    public static string? Chat(string line, out string speaker, bool pasted = false)
    {
        speaker = "";
        var body = Body(Cut(line), pasted);
        if (body == null) return null;
        int says = body.IndexOf('>');
        if (says <= 0) return null;
        speaker = Tidy(body[..says], stripMarkup: false) ?? "";
        if (speaker.Length == 0) return null;
        return Tidy(speaker + " > " + body[(says + 1)..], stripMarkup: false);
    }

    private static string Cut(string line) => line.Length > CustomRules.MaxRawLine ? line[..CustomRules.MaxRawLine] : line;

    /// <summary>The line behind its "[ timestamp ]". EVE starts every chat message line with U+FEFF, which is no
    /// white space to Trim.</summary>
    private static string? Body(string line, bool pasted)
    {
        int start = 0;
        while (start < line.Length && (char.IsWhiteSpace(line[start]) || line[start] == (char)0xFEFF)) start++;
        // "[ 2026.01.01 00:00:00 ]": a bracket, then a digit. Pasted text that merely begins with "[" keeps it.
        int digit = start + 1;
        while (digit < line.Length && line[digit] == ' ') digit++;
        if (start < line.Length && line[start] == '[' && digit < line.Length && char.IsAsciiDigit(line[digit]))
        {
            int close = line.IndexOf(']', digit);
            if (close > 0) return line[(close + 1)..];
        }
        return pasted ? line[start..] : null;
    }

    /// <summary>Markup out, white space runs to one space, ends trimmed, and never longer than MaxText (the cut never
    /// leaves a space at the end). Null when nothing is left. One pass: no character is looked at more than twice.</summary>
    private static string? Tidy(string text, bool stripMarkup)
    {
        var tidy = new StringBuilder(Math.Min(text.Length, CustomRules.MaxText));
        bool space = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (stripMarkup && c == '<')
            {
                int close = TagEnd(text, i);
                if (close > 0) { i = close; continue; }
            }
            if (char.IsWhiteSpace(c) || c == (char)0xFEFF) { space = tidy.Length > 0; continue; }
            if (tidy.Length + (space ? 2 : 1) > CustomRules.MaxText) break;
            if (space) { tidy.Append(' '); space = false; }
            tidy.Append(c);
        }
        return tidy.Length == 0 ? null : tidy.ToString();
    }

    /// <summary>Where the tag that opens at <paramref name="open"/> (a "&lt;") closes, or -1 when none opens there.
    /// EVE's markup is "&lt;", an optional "/", a letter, then anything but "&lt;" up to the next "&gt;":
    /// &lt;color=0xffcc0000&gt;, &lt;b&gt;, &lt;/font&gt;. Any other "&lt;" is text and stays as it was typed, with
    /// what follows it: "5 &lt; 7 and 9 &gt; 3". The search ends at the next "&lt;", so a line of them is still read
    /// once.</summary>
    private static int TagEnd(string text, int open)
    {
        int i = open + 1;
        if (i < text.Length && text[i] == '/') i++;
        if (i >= text.Length || !char.IsAsciiLetter(text[i])) return -1;
        for (i++; i < text.Length; i++)
        {
            if (text[i] == '>') return i;
            if (text[i] == '<') return -1;
        }
        return -1;
    }
}

/// <summary>The regular expression each pattern becomes, built once and kept. A pattern is asked for whenever the
/// rules load or are saved, whenever the settings page draws its cards, at every test and whenever the engine is
/// built; building one is by far the costliest step of each. What is kept is the Regex, which never changes and may be
/// used by any thread at once, or the fact that .NET can't read the pattern. Only a rule of the regex kind has one:
/// plain text is searched as it stands.
///
/// Every one is an ordinary regular expression with the time limit of CustomRules.MatchTimeout. Never
/// RegexOptions.NonBacktracking: measured on .NET 10, that engine was bounded in neither time nor memory (one
/// 33-character pattern held gigabytes, and single lines took half a second without being given up).</summary>
public static class CustomRulePatterns
{
    /// <summary>The most patterns kept. A rules file holds 50, and the editor asks for a few more while a player
    /// changes one; when this many are kept, all are dropped and built again as they are asked for.</summary>
    public const int MaxKept = 256;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Pattern, bool CaseSensitive), Regex?> Kept = new();
    private static int _built;

    /// <summary>How many patterns were built since MultiPreview started; one that .NET can't read counts too. A test
    /// reads it before and after a step to see that the step built none.</summary>
    public static int Built => Volatile.Read(ref _built);

    /// <summary>Forgets every pattern; each is built again when it is next asked for. The patterns are kept for the
    /// whole process, so a test that counts builds starts here: with nothing kept, the 256 can't run out, and empty
    /// the cache, in the middle of it.</summary>
    public static void Clear()
    {
        lock (Gate) Kept.Clear();
    }

    /// <summary>The regular expression of a pattern, or null when .NET can't read it. Asked by the settings window's
    /// thread and by whoever builds the engine: one lock, never taken while a line is matched.</summary>
    public static Regex? Get(string pattern, bool caseSensitive)
    {
        lock (Gate)
        {
            if (Kept.TryGetValue((pattern, caseSensitive), out var known)) return known;
            Regex? regex;
            try { regex = new Regex(pattern, RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), CustomRules.MatchTimeout); }
            catch (ArgumentException) { regex = null; }   // not a regular expression
            _built++;
            if (Kept.Count >= MaxKept) Kept.Clear();
            Kept[(pattern, caseSensitive)] = regex;
            return regex;
        }
    }
}

/// <summary>One rule made ready to match. Plain text is an ordinal search. A regular expression is the one
/// CustomRulePatterns keeps for its pattern: .NET gives a match up after 50 ms. The matcher itself only matches; what
/// a rule may cost is the engine's to count (it has the clock), and the matcher carries the counts of its rule: its
/// strikes, and what it took in the engine's current window.</summary>
public sealed class CustomRuleMatcher
{
    private readonly Regex? _regex;
    private readonly string _text;
    private readonly StringComparison _comparison;
    private int _strikes;

    // The engine's, on the reading thread, for the current window: what this rule's matches took without the slowest
    // of them, the slowest, how many there were, and what the window counts for the rule (WindowTicks, and their
    // average once more: the slowest match counted as an ordinary one).
    internal long WindowTicks, WindowSlowest, WindowCounted;
    internal int WindowMatches;

    private CustomRuleMatcher(CustomAlertRule rule, Regex? regex)
    {
        Rule = rule;
        _regex = regex;
        _text = rule.Pattern;
        _comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Characters = new HashSet<string>(rule.Characters, StringComparer.OrdinalIgnoreCase);
    }

    public CustomAlertRule Rule { get; }
    public HashSet<string> Characters { get; }
    /// <summary>A regular expression: its matches are timed and counted. False for plain text.</summary>
    public bool IsRegex => _regex != null;
    /// <summary>The rule cost too much and matches nothing any more.</summary>
    public bool Off { get; private set; }

    /// <summary>The matcher of a rule, or null when its pattern is not a regular expression .NET can read.</summary>
    public static CustomRuleMatcher? Create(CustomAlertRule rule)
    {
        if (!rule.IsRegex) return new CustomRuleMatcher(rule, null);
        var regex = CustomRulePatterns.Get(rule.Pattern, rule.CaseSensitive);
        return regex == null ? null : new CustomRuleMatcher(rule, regex);
    }

    /// <summary>Whether the rule's pattern is in <paramref name="text"/>. <paramref name="timedOut"/>: the match was
    /// given up after 50 ms and counts as no match. Nothing is counted here.</summary>
    public bool Matches(string text, out bool timedOut)
    {
        timedOut = false;
        if (Off) return false;
        if (_regex == null) return text.Contains(_text, _comparison);
        try { return _regex.IsMatch(text); }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return false;
        }
    }

    /// <summary>One line on which the rule took the whole time limit. True when it was one too many in this window.</summary>
    internal bool Strike() => ++_strikes >= CustomRules.TimeoutsBeforeOff;

    /// <summary>A window starts anew: the strikes of the one before are forgotten.</summary>
    internal void ForgetStrikes() => _strikes = 0;

    internal void SwitchOff() => Off = true;
}

/// <summary>What the editor's Test box says.</summary>
public enum CustomRuleTestOutcome { Matches, NoMatch, NotALine, TimedOut, Broken }

/// <summary>The answer of a test: what happened, how long the match took, the text the rule looked at (what the
/// pasted line becomes), and for a broken rule what is wrong with it.</summary>
public sealed record CustomRuleTestResult(CustomRuleTestOutcome Outcome, TimeSpan Took, string Seen, CustomRuleProblem Problem);

/// <summary>The rules that are switched on and can run, made ready once (IDEAS #8). The log monitor asks it about
/// every line while it has one; with no rule on there is no engine at all (Build returns null), and the monitor
/// does nothing more per line than see that it has none.
///
/// The monitor's one thread reads every client's logs, and "under attack" waits behind whatever a rule costs. A
/// plain-text rule is a search for a word and costs nothing worth counting. A regular expression is a player's
/// program, so these bounds hold what all of them together can cost (the numbers are in CustomRules). Three of the
/// four are counted over a window: a hundred lines on which a regular expression ran.
///
/// 1. A rule and a line: a match that was given up after 50 ms, or that came back after as long, is a strike. Three
///    strikes in one window switch the rule off. The strikes are forgotten when a window starts anew: they are
///    counted by the wall clock, and a reader that is kept off the CPU in the middle of a match earns one for a rule
///    that did nothing wrong.
/// 2. A line: when the regular expressions have used 50 ms on it, the rest of them skip that line.
/// 3. The average: when the regular expressions cost more than 2 ms a line over a window, every rule that by itself
///    took more than 2 ms a match on average is switched off, or the costliest rule when none did. What a window
///    counts for a rule is its ordinary cost: the average of its matches without the slowest, for every match it
///    made. So one stall of the PC costs no rule, and a rule is counted in full however few of the window's lines
///    are its own (a rule for one of fifty characters makes two matches in a hundred lines).
/// 4. The left-out matches: what the average leaves out (each rule's slowest match, beyond an ordinary one) may take
///    another 2 ms a line over a window, not counting the largest left-out match of the window: that one is the
///    hiccup, and always forgiven. Beyond that, the rule with the largest of the rest is switched off, one rule at
///    a verdict. Without this bound, rules that are each slow on rare lines of their own would never answer for them.
///
/// A verdict of the third or the fourth bound is given as soon as it is certain, and not only on the hundredth
/// line: once a window's allowance is used up, the lines still to come can't give it back. A window ends with a
/// verdict or after a hundred lines, and every count starts anew.
///
/// A rule that is off stays off until the rules change or MultiPreview restarts, and the error log says so once, by
/// the rule's id and the bound. The counts are the reading thread's alone: there is no lock on this path, and with
/// only plain-text rules no clock is read.</summary>
public sealed class CustomAlertEngine
{
    /// <summary>The word the error log names the bound by that switched a rule off: the first one (three matches of
    /// 50 ms or more in a hundred lines), the third (the average), or the fourth (the left-out matches).</summary>
    public const string OffByTimeout = "timeout", OffByAverage = "average", OffByPeaks = "peaks";

    private static readonly long MatchLimit = InTicks(CustomRules.MatchTimeout);
    private static readonly long LineLimit = InTicks(CustomRules.LineBudget);
    private static readonly long AverageLimit = InTicks(CustomRules.AverageBudget);
    private static readonly long WindowLimit = AverageLimit * CustomRules.WindowLines;

    private static long InTicks(TimeSpan time) => time.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;

    private readonly CustomRuleMatcher[] _game;
    private readonly Dictionary<string, CustomRuleMatcher[]> _chat;
    private readonly CustomRuleMatcher[] _regex;      // every regular expression, game and chat: what the window counts
    private readonly ErrorLog _errors;
    private readonly Func<long> _clock;
    private int _windowLines;                         // lines of the current window on which a regular expression ran
    private long _windowTicks;                        // what the window counts: each rule's ordinary cost (WindowCounted)
    private long _windowAll;                          // everything the regular expressions took in it

    private CustomAlertEngine(CustomRuleMatcher[] game, Dictionary<string, CustomRuleMatcher[]> chat, CustomRuleMatcher[] regex,
        ErrorLog errors, Func<long> clock)
    {
        _game = game;
        _chat = chat;
        _regex = regex;
        Channels = chat.Keys.ToList();
        _errors = errors;
        _clock = clock;
    }

    /// <summary>The chat channels to read beyond what the monitor reads anyway, as the rules name them.</summary>
    public IReadOnlyList<string> Channels { get; }

    /// <summary>How many rules are ready.</summary>
    public int Count => _game.Length + _chat.Values.Sum(m => m.Length);

    /// <summary>The ids of the rules that were switched off for what they cost, in the rules' order.</summary>
    public IReadOnlyList<string> SwitchedOff => _regex.Where(m => m.Off).Select(m => m.Rule.Id).ToList();

    /// <summary>The engine for these rules, or null when none of them is on and able to run.
    /// <paramref name="clock"/> counts Stopwatch ticks (Stopwatch.GetTimestamp unless a test hands in its own): it
    /// is read directly before and directly after each match of a regular expression, and at no other time.</summary>
    public static CustomAlertEngine? Build(IReadOnlyList<CustomAlertRule> rules, ErrorLog errors, Func<long>? clock = null)
    {
        var ready = rules.Take(CustomRules.MaxRules)
            .Where(r => r.Enabled && CustomRuleCheck.Shape(r, rules) == CustomRuleProblem.None)
            .Select(r => CustomRuleMatcher.Create(r.Clone()))       // null: a pattern that is no regular expression
            .Where(m => m != null).Select(m => m!).ToList();
        if (ready.Count == 0) return null;
        var chat = ready.Where(m => m.Rule.IsChat)
            .GroupBy(m => m.Rule.Channel.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new CustomAlertEngine(ready.Where(m => !m.Rule.IsChat).ToArray(), chat, ready.Where(m => m.IsRegex).ToArray(),
            errors, clock ?? Stopwatch.GetTimestamp);
    }

    /// <summary>Whether a rule reads this chat channel.</summary>
    public bool Reads(string channel) => _chat.ContainsKey(channel);

    /// <summary>A game-log line of <paramref name="character"/>: <paramref name="raise"/> is called for each rule it
    /// matches, with the text the rule looked at.</summary>
    public void MatchGame(string line, string character, Action<CustomAlertRule, string> raise)
    {
        if (_game.Length == 0) return;
        var text = CustomRuleText.Game(line);
        if (text == null) return;
        Run(_game, text, character, own: false, raise);
    }

    /// <summary>A line of a chat channel that <paramref name="listener"/> hears.</summary>
    public void MatchChat(string line, string channel, string listener, Action<CustomAlertRule, string> raise)
    {
        if (!_chat.TryGetValue(channel, out var rules)) return;
        var text = CustomRuleText.Chat(line, out var speaker);
        if (text == null) return;
        Run(rules, text, listener, string.Equals(speaker, listener, StringComparison.OrdinalIgnoreCase), raise);
    }

    /// <summary>One line through its rules, in their order. <paramref name="own"/>: the listener wrote it.</summary>
    private void Run(CustomRuleMatcher[] rules, string text, string character, bool own, Action<CustomAlertRule, string> raise)
    {
        long used = 0;        // what the regular expressions took on this line
        bool timed = false;   // one of them ran
        try
        {
            foreach (var matcher in rules)
            {
                if (matcher.Off || (own && matcher.Rule.IgnoreOwn)) continue;
                if (matcher.Characters.Count != 0 && !matcher.Characters.Contains(character)) continue;
                bool matched;
                if (!matcher.IsRegex) matched = matcher.Matches(text, out _);
                else
                {
                    if (used >= LineLimit) continue;                          // the second bound: this line has had its time
                    long before = _clock();
                    matched = matcher.Matches(text, out bool timedOut);
                    long took = Math.Max(0, _clock() - before);
                    used += took;
                    timed = true;
                    AddToWindow(matcher, took);
                    if ((timedOut || took >= MatchLimit) && matcher.Strike()) SwitchOff(matcher, OffByTimeout);   // the first bound
                }
                if (matched) raise(matcher.Rule, text);
            }
        }
        finally
        {
            // Also when raising an alert threw: what the line cost is in the window, so the line is too.
            if (timed) EndLine();
        }
    }

    /// <summary>Adds a match to the window. A rule's slowest match stays out of its ordinary ones: when a slower one
    /// comes, the one that was the slowest until now joins them in its place. What the window counts for the rule is
    /// its ordinary matches and their average once more, for the slowest: so the slowest counts as an ordinary match,
    /// and a rule that made only two matches in the window is counted for both.</summary>
    private void AddToWindow(CustomRuleMatcher matcher, long took)
    {
        matcher.WindowMatches++;
        _windowAll += took;
        long ordinary = took;
        if (took > matcher.WindowSlowest)
        {
            ordinary = matcher.WindowSlowest;
            matcher.WindowSlowest = took;
        }
        matcher.WindowTicks += ordinary;
        // One match is the slowest one: nothing ordinary to go by yet, and all of it is left out.
        long counted = matcher.WindowMatches < 2 ? 0 : matcher.WindowTicks + matcher.WindowTicks / (matcher.WindowMatches - 1);
        _windowTicks += counted - matcher.WindowCounted;
        matcher.WindowCounted = counted;
    }

    /// <summary>What the average leaves out of a rule in this window: its slowest match, beyond an ordinary one.</summary>
    private static long LeftOut(CustomRuleMatcher matcher) => matcher.WindowSlowest + matcher.WindowTicks - matcher.WindowCounted;

    /// <summary>Asked after every line on which a regular expression ran: a verdict of the third or the fourth bound
    /// ends the window, and so does its hundredth line.</summary>
    private void EndLine()
    {
        _windowLines++;
        if (!Verdict() && _windowLines < CustomRules.WindowLines) return;

        _windowLines = 0;
        _windowTicks = 0;
        _windowAll = 0;
        foreach (var matcher in _regex)
        {
            matcher.WindowTicks = 0;
            matcher.WindowSlowest = 0;
            matcher.WindowCounted = 0;
            matcher.WindowMatches = 0;
            matcher.ForgetStrikes();
        }
    }

    /// <summary>Switches off what the third or the fourth bound says must go. False when neither says so.</summary>
    private bool Verdict()
    {
        if (_windowTicks > WindowLimit)
        {
            // Certain already: a hundred lines' allowance is used, however little the rest of them would take.
            CustomRuleMatcher? costliest = null;
            bool found = false;
            foreach (var matcher in _regex)
            {
                if (matcher.Off || matcher.WindowMatches < 2) continue;      // one match is the slowest one: left out
                if (costliest == null || matcher.WindowTicks > costliest.WindowTicks) costliest = matcher;
                if (matcher.WindowTicks <= AverageLimit * (matcher.WindowMatches - 1)) continue;
                SwitchOff(matcher, OffByAverage);
                found = true;
            }
            if (!found && costliest != null) SwitchOff(costliest, OffByAverage);
            return true;
        }

        // The fourth bound. What the average left out is everything the rules took less what it counted; while that
        // is within the allowance (nearly always), this is all a line costs.
        long leftOut = _windowAll - _windowTicks;
        if (leftOut <= WindowLimit) return false;
        CustomRuleMatcher? forgiven = null;                                  // the rule that made the slowest match of the window
        foreach (var matcher in _regex)
            if (!matcher.Off && (forgiven == null || matcher.WindowSlowest > forgiven.WindowSlowest)) forgiven = matcher;
        if (forgiven == null || leftOut - LeftOut(forgiven) <= WindowLimit) return false;
        CustomRuleMatcher? largest = null;                                   // of the rest
        foreach (var matcher in _regex)
        {
            if (matcher.Off || matcher == forgiven) continue;
            if (largest == null || LeftOut(matcher) > LeftOut(largest)) largest = matcher;
        }
        if (largest == null) return false;
        SwitchOff(largest, OffByPeaks);                                      // one rule at a verdict
        return true;
    }

    /// <summary>Switches a rule off and says so, once (a rule that is off is never matched again, so it can't come
    /// here twice). The record holds the rule's id, which is MultiPreview's own, and the bound: never the rule's name,
    /// its pattern or a line. What the rule took leaves the window with it, so the rules that stay don't answer for it.</summary>
    private void SwitchOff(CustomRuleMatcher matcher, string bound)
    {
        matcher.SwitchOff();
        _windowTicks -= matcher.WindowCounted;
        _windowAll -= matcher.WindowTicks + matcher.WindowSlowest;
        matcher.WindowTicks = 0;
        matcher.WindowSlowest = 0;
        matcher.WindowCounted = 0;
        matcher.WindowMatches = 0;
        string why = bound == OffByTimeout
            ? $"it took {CustomRules.MatchTimeout.TotalMilliseconds:0} ms or more to match a line, {CustomRules.TimeoutsBeforeOff} times in {CustomRules.WindowLines} lines"
            : bound == OffByAverage
            ? $"the regular-expression rules took more than {CustomRules.AverageBudget.TotalMilliseconds:0} ms a line on average, and this rule was the slowest or one of the slow ones"
            : $"the slowest matches of the regular-expression rules, one for each rule and the longest of them left out, took more than {CustomRules.AverageBudget.TotalMilliseconds * CustomRules.WindowLines:0} ms in {CustomRules.WindowLines} lines, and this rule's was the longest of those";
        _errors.Record(new TimeoutException($"Custom alert rule {matcher.Rule.AlertType} was switched off ({bound}): {why}. It stays off until the rules change or MultiPreview restarts."),
            "CustomAlert", DateTime.Now);
    }

    /// <summary>The editor's Test box: what this rule does with a pasted line, through the functions the reader
    /// uses (ResolveLocalizedNames, then CustomRuleText and CustomRuleMatcher). The rule needn't be on, and its
    /// character list isn't asked: the test is about the pattern. With <paramref name="all"/> (the rules it is one
    /// of) the answer is also "broken" for a rule that can't run for a reason outside itself: its channel is not one
    /// of the ten that are read.</summary>
    public static CustomRuleTestResult Test(CustomAlertRule rule, string pasted, IReadOnlyList<CustomAlertRule>? all = null)
    {
        var problem = CustomRuleCheck.Problem(rule, all);
        var matcher = problem == CustomRuleProblem.None ? CustomRuleMatcher.Create(rule) : null;
        if (matcher == null) return new CustomRuleTestResult(CustomRuleTestOutcome.Broken, TimeSpan.Zero, "", problem == CustomRuleProblem.None ? CustomRuleProblem.PatternInvalid : problem);
        // What the log monitor does to every line it reads, before anything looks at it (ProcessLine).
        pasted = LogMonitorService.ResolveLocalizedNames(pasted.Length > CustomRules.MaxPasted ? pasted[..CustomRules.MaxPasted] : pasted);
        var seen = rule.IsChat ? CustomRuleText.Chat(pasted, out _, pasted: true) : CustomRuleText.Game(pasted, pasted: true);
        if (seen == null) return new CustomRuleTestResult(CustomRuleTestOutcome.NotALine, TimeSpan.Zero, "", CustomRuleProblem.None);
        var clock = Stopwatch.StartNew();
        bool matched = matcher.Matches(seen, out bool timedOut);
        clock.Stop();
        var outcome = timedOut ? CustomRuleTestOutcome.TimedOut : matched ? CustomRuleTestOutcome.Matches : CustomRuleTestOutcome.NoMatch;
        return new CustomRuleTestResult(outcome, clock.Elapsed, seen, CustomRuleProblem.None);
    }
}

/// <summary>The custom rules by alert type, for everything that shows or plays an alert and only gets its type: the
/// toast and the thumbnail (the rule's name), the sound (the rule's file), the log monitor's quiet time. App publishes
/// the rules whenever they load or change. Every rule is in it, also one that is switched off: its last toast may
/// still be up.</summary>
public static class CustomAlertTypes
{
    public const string Prefix = "custom:";

    private static volatile IReadOnlyDictionary<string, CustomAlertRule> _byType = new Dictionary<string, CustomAlertRule>();

    public static bool IsCustom(string? alertType) => alertType != null && alertType.StartsWith(Prefix, StringComparison.Ordinal);

    public static void Publish(IEnumerable<CustomAlertRule> rules)
    {
        var byType = new Dictionary<string, CustomAlertRule>(StringComparer.Ordinal);
        foreach (var rule in rules) byType[rule.AlertType] = rule.Clone();
        _byType = byType;
    }

    /// <summary>The rule behind an alert type, or null: not a custom type, or a rule deleted since.</summary>
    public static CustomAlertRule? Find(string? alertType) =>
        IsCustom(alertType) && _byType.TryGetValue(alertType!, out var rule) ? rule : null;
}
