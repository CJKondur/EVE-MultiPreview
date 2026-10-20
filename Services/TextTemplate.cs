using System;
using System.Collections.Generic;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>Thumbnail text templates (IDEAS P2): the text a thumbnail shows built from a template such as
/// "{name} · {system} · {timer}". The tokens are {name}, {system}, {timer}, {label}, {jumps} (the jump count, IDEAS P10) and {week} (the playtime of the last seven days, IDEAS P11); any other {word} stays as typed,
/// "{{" and "}}" are a literal brace. A token whose value is empty takes the separators next to it away, so a thumbnail
/// with no label or system never shows a dangling " · ". A bracket pair () [] or <> that stands right around an empty
/// token ("{name} ({system})") goes with it; brackets around anything else, and words typed in the template, stay.
///
/// The values are display text that was already masked (streamer mode) and are only ever copied into the result: never
/// parsed again (a name that reads "{timer}" stays that), never used as a format string, never logged. The template is
/// bounded (<see cref="MaxLength"/>, <see cref="MaxTokens"/>) because a hand-edited settings file can hold anything.</summary>
public static class TextTemplate
{
    public const string NameToken = "name";
    public const string SystemToken = "system";
    public const string TimerToken = "timer";
    public const string LabelToken = "label";
    public const string JumpsToken = "jumps";
    public const string WeekToken = "week";

    /// <summary>The longest template kept (characters).</summary>
    public const int MaxLength = 120;

    /// <summary>The most tokens a template fills; a token after that renders empty.</summary>
    public const int MaxTokens = 8;

    /// <summary>What goes between two pieces of text and goes away with an empty one: spaces and these marks.</summary>
    private const string SeparatorMarks = "·•|/\\-–—,;:.~+<>«»→←";

    /// <summary>The template as the app keeps it: nothing for null or blank, no control character (a line break would put
    /// text on a second line), at most <see cref="MaxLength"/> characters (an over-long one is cut, not dropped, and a cut
    /// never leaves half of a surrogate pair).</summary>
    public static string Clean(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var sb = new StringBuilder(Math.Min(template.Length, MaxLength));
        foreach (char c in template)
        {
            if (sb.Length >= MaxLength) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;   // the cut fell between the two halves of a pair
        return string.IsNullOrWhiteSpace(sb.ToString()) ? "" : sb.ToString();
    }

    /// <summary>The template filled with <paramref name="values"/> (token name, in lower case, to display text). A token
    /// with no entry or a blank one is empty. A template that is blank gives "".</summary>
    public static string Render(string? template, IReadOnlyDictionary<string, string?> values)
    {
        template = Clean(template);
        if (template.Length == 0) return "";

        var parts = Parse(template, values);
        int first = parts.FindIndex(p => p.IsToken && !p.Empty);
        var sb = new StringBuilder();
        if (first < 0)
        {
            AppendRegion(sb, parts, 0, parts.Count, interior: false);
            return sb.ToString().Trim();
        }

        AppendRegion(sb, parts, 0, first, interior: false);
        sb.Append(parts[first].Text);
        int previous = first;
        for (int i = first + 1; i < parts.Count; i++)
        {
            if (!parts[i].IsToken || parts[i].Empty) continue;
            AppendRegion(sb, parts, previous + 1, i, interior: true);
            sb.Append(parts[i].Text);
            previous = i;
        }
        AppendRegion(sb, parts, previous + 1, parts.Count, interior: false);
        return sb.ToString().Trim();
    }

    private readonly record struct Part(string Text, bool IsToken)
    {
        public bool Empty => IsToken && string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>Splits the template into literal text and filled tokens. Values are appended as they are and never looked at again.</summary>
    private static List<Part> Parse(string template, IReadOnlyDictionary<string, string?> values)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        int tokens = 0;
        void Flush()
        {
            if (literal.Length == 0) return;
            parts.Add(new Part(literal.ToString(), false));
            literal.Clear();
        }

        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i + 1)
                {
                    string word = template.Substring(i + 1, close - i - 1).ToLowerInvariant();
                    if (word is NameToken or SystemToken or TimerToken or LabelToken or JumpsToken or WeekToken)
                    {
                        Flush();
                        string text = ++tokens > MaxTokens || !values.TryGetValue(word, out var v) ? "" : v ?? "";
                        parts.Add(new Part(text, true));
                        i = close;
                        continue;
                    }
                }
            }
            literal.Append(c);
        }
        Flush();
        DropBracketsAroundEmptyTokens(parts);
        return parts;
    }

    /// <summary>"(" before an empty token and ")" right after it (and the same for [] and <>) are taken away with it: the
    /// brackets must be the last character of the literal before and the first of the literal after, so a word in between
    /// ("(sys: {system})") keeps its brackets as typed.</summary>
    private static void DropBracketsAroundEmptyTokens(List<Part> parts)
    {
        for (int i = 1; i + 1 < parts.Count; i++)
        {
            if (!parts[i].Empty || parts[i - 1].IsToken || parts[i + 1].IsToken) continue;
            string before = parts[i - 1].Text, after = parts[i + 1].Text;
            if (before.Length == 0 || after.Length == 0) continue;
            bool pair = (before[^1], after[0]) is ('(', ')') or ('[', ']') or ('<', '>');
            if (!pair) continue;
            parts[i - 1] = new Part(before[..^1], false);
            parts[i + 1] = new Part(after[1..], false);
        }
    }

    /// <summary>Appends what lies between two kept tokens (or before the first, or after the last). Where no token in the
    /// region is empty the text is kept as typed. Where one is, the separators go: an interior region keeps its first one
    /// (so two kept tokens still have a separator between them), an edge region keeps none. Words typed in the template stay.</summary>
    private static void AppendRegion(StringBuilder sb, List<Part> parts, int from, int to, bool interior)
    {
        bool anyEmpty = false;
        for (int i = from; i < to; i++) anyEmpty |= parts[i].Empty;
        bool kept = false;
        for (int i = from; i < to; i++)
        {
            var p = parts[i];
            if (p.IsToken) continue;   // an empty token adds nothing; a filled one is never inside a region
            if (!anyEmpty || !IsSeparator(p.Text)) { sb.Append(p.Text); continue; }
            if (interior && !kept) { sb.Append(p.Text); kept = true; }
        }
    }

    private static bool IsSeparator(string text)
    {
        foreach (char c in text)
            if (!char.IsWhiteSpace(c) && SeparatorMarks.IndexOf(c) < 0) return false;
        return true;
    }
}
