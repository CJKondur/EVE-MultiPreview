using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    /// <summary>How long a regular expression that can't be matched in linear time may take on one line.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    /// <summary>A rule that runs out of time this often is switched off until the rules change or MultiPreview restarts.</summary>
    public const int TimeoutsBeforeOff = 3;
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
        return rule.IsRegex && CustomRuleMatcher.Create(rule) == null ? CustomRuleProblem.PatternInvalid : CustomRuleProblem.None;
    }

    /// <summary>Everything but whether a regular expression reads: the engine builds each pattern once and sees that
    /// for itself.</summary>
    internal static CustomRuleProblem Shape(CustomAlertRule rule, IReadOnlyList<CustomAlertRule>? all)
    {
        if (string.IsNullOrWhiteSpace(rule.Name)) return CustomRuleProblem.NameEmpty;
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

    /// <summary>A rule's sound as it may be kept: a file on a drive of this PC ("C:\Sounds\ping.wav"), else "". A rules
    /// file can come from another player. A path to another computer ("\\server\share\ping.wav", or the same behind
    /// "\\?\") would make Windows log in to that computer when the alert plays, and a web address is no file: none of
    /// them is ever kept, played or looked at.</summary>
    public static string LocalSound(string? sound)
    {
        var path = (sound ?? "").Trim();
        bool local = path.Length >= 4 && path.Length <= CustomRules.MaxSound
            && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
            && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        return local ? path : "";
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

    /// <summary>Markup out (a "&lt;" with no "&gt;" behind it is text and stays), white space runs to one space, ends
    /// trimmed, cut to MaxText. Null when nothing is left.</summary>
    private static string? Tidy(string text, bool stripMarkup)
    {
        var tidy = new StringBuilder(Math.Min(text.Length, CustomRules.MaxText));
        bool space = false;
        for (int i = 0; i < text.Length && tidy.Length < CustomRules.MaxText; i++)
        {
            char c = text[i];
            if (stripMarkup && c == '<')
            {
                int close = text.IndexOf('>', i + 1);
                if (close > 0) { i = close; continue; }
            }
            if (char.IsWhiteSpace(c) || c == (char)0xFEFF) { space = tidy.Length > 0; continue; }
            if (space) { tidy.Append(' '); space = false; }
            tidy.Append(c);
        }
        return tidy.Length == 0 ? null : tidy.ToString();
    }
}

/// <summary>One rule made ready to match. Plain text is an ordinal search. A regular expression is built once, with
/// RegexOptions.NonBacktracking where .NET accepts the pattern: such a match takes time in proportion to the line,
/// whatever the pattern, so it can't hold the reading thread. A pattern NonBacktracking refuses (a lookahead, a
/// lookbehind, a backreference ...) is built as an ordinary regular expression with a time limit of 50 ms a line; after
/// three lines that ran out of time the rule is off until the rules change.</summary>
public sealed class CustomRuleMatcher
{
    private readonly Regex? _regex;
    private readonly string _text;
    private readonly StringComparison _comparison;
    private int _timeouts;

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
    /// <summary>True for a regular expression that matches in linear time (NonBacktracking); false for one that
    /// runs under the time limit, and for plain text.</summary>
    public bool Linear { get; private init; }
    /// <summary>The rule ran out of time too often and matches nothing any more.</summary>
    public bool Off { get; private set; }

    /// <summary>The matcher of a rule, or null when its pattern is not a regular expression .NET can read.</summary>
    public static CustomRuleMatcher? Create(CustomAlertRule rule)
    {
        if (!rule.IsRegex) return new CustomRuleMatcher(rule, null);
        var options = RegexOptions.CultureInvariant | (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            try { return new CustomRuleMatcher(rule, new Regex(rule.Pattern, options | RegexOptions.NonBacktracking, CustomRules.MatchTimeout)) { Linear = true }; }
            catch (NotSupportedException) { return new CustomRuleMatcher(rule, new Regex(rule.Pattern, options, CustomRules.MatchTimeout)); }
        }
        catch (ArgumentException) { return null; }   // not a regular expression
    }

    /// <summary>Whether the rule's pattern is in <paramref name="text"/>. <paramref name="timedOut"/>: the match was
    /// given up after 50 ms and counts as no match.</summary>
    public bool Matches(string text, out bool timedOut)
    {
        timedOut = false;
        if (Off) return false;
        if (_regex == null) return text.Contains(_text, _comparison);
        try { return _regex.IsMatch(text); }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            if (++_timeouts >= CustomRules.TimeoutsBeforeOff) Off = true;
            return false;
        }
    }
}

/// <summary>What the editor's Test box says.</summary>
public enum CustomRuleTestOutcome { Matches, NoMatch, NotALine, TimedOut, Broken }

/// <summary>The answer of a test: what happened, how long the match took, the text the rule looked at (what the
/// pasted line becomes), and for a broken rule what is wrong with it.</summary>
public sealed record CustomRuleTestResult(CustomRuleTestOutcome Outcome, TimeSpan Took, string Seen, CustomRuleProblem Problem);

/// <summary>The rules that are switched on and can run, made ready once (IDEAS #8). The log monitor asks it about
/// every line while it has one; with no rule on there is no engine at all (Build returns null), and the monitor
/// does nothing more per line than see that it has none.</summary>
public sealed class CustomAlertEngine
{
    private readonly CustomRuleMatcher[] _game;
    private readonly Dictionary<string, CustomRuleMatcher[]> _chat;
    private readonly ErrorLog _errors;

    private CustomAlertEngine(CustomRuleMatcher[] game, Dictionary<string, CustomRuleMatcher[]> chat, IReadOnlyList<string> channels, ErrorLog errors)
    {
        _game = game;
        _chat = chat;
        Channels = channels;
        _errors = errors;
    }

    /// <summary>The chat channels to read beyond what the monitor reads anyway, as the rules name them.</summary>
    public IReadOnlyList<string> Channels { get; }

    /// <summary>How many rules are ready.</summary>
    public int Count => _game.Length + _chat.Values.Sum(m => m.Length);

    /// <summary>The engine for these rules, or null when none of them is on and able to run.</summary>
    public static CustomAlertEngine? Build(IReadOnlyList<CustomAlertRule> rules, ErrorLog errors)
    {
        var ready = rules.Take(CustomRules.MaxRules)
            .Where(r => r.Enabled && CustomRuleCheck.Shape(r, rules) == CustomRuleProblem.None)
            .Select(r => CustomRuleMatcher.Create(r.Clone()))       // null: a pattern that is no regular expression
            .Where(m => m != null).Select(m => m!).ToList();
        if (ready.Count == 0) return null;
        var chat = ready.Where(m => m.Rule.IsChat)
            .GroupBy(m => m.Rule.Channel.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new CustomAlertEngine(ready.Where(m => !m.Rule.IsChat).ToArray(), chat, chat.Keys.ToList(), errors);
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
        foreach (var matcher in _game)
            if (AppliesTo(matcher, character) && Matches(matcher, text)) raise(matcher.Rule, text);
    }

    /// <summary>A line of a chat channel that <paramref name="listener"/> hears.</summary>
    public void MatchChat(string line, string channel, string listener, Action<CustomAlertRule, string> raise)
    {
        if (!_chat.TryGetValue(channel, out var rules)) return;
        var text = CustomRuleText.Chat(line, out var speaker);
        if (text == null) return;
        bool own = string.Equals(speaker, listener, StringComparison.OrdinalIgnoreCase);
        foreach (var matcher in rules)
            if (!(own && matcher.Rule.IgnoreOwn) && AppliesTo(matcher, listener) && Matches(matcher, text)) raise(matcher.Rule, text);
    }

    private static bool AppliesTo(CustomRuleMatcher matcher, string character) =>
        matcher.Characters.Count == 0 || matcher.Characters.Contains(character);

    private bool Matches(CustomRuleMatcher matcher, string text)
    {
        bool matched = matcher.Matches(text, out _);
        if (matcher.Off && !_reported.Contains(matcher.Rule.Id))
        {
            // Once per rule, and never the rule's name, its pattern or the line: the id is MultiPreview's own.
            _reported.Add(matcher.Rule.Id);
            _errors.Record(new TimeoutException($"Custom alert rule {matcher.Rule.AlertType} took too long to match {CustomRules.TimeoutsBeforeOff} lines and is off until the rules change or MultiPreview restarts."),
                "CustomAlert", DateTime.Now);
        }
        return matched;
    }

    private readonly HashSet<string> _reported = new();

    /// <summary>The editor's Test box: what this rule does with a pasted line, through the functions the reader
    /// uses (ResolveLocalizedNames, then CustomRuleText and CustomRuleMatcher). The rule needn't be on, and its
    /// character list isn't asked: the test is about the pattern.</summary>
    public static CustomRuleTestResult Test(CustomAlertRule rule, string pasted)
    {
        var problem = CustomRuleCheck.Problem(rule);
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
