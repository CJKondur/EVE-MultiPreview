using System;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #2: the text of one Discord message. A line holds a severity mark, the character's name and the
/// event's name (and the count when repeated), nothing else. Names are player text, so they reach Discord only
/// through <see cref="Escape"/>; the message as a whole is kept under Discord's 2000-character limit.</summary>
public static class PushMessage
{
    /// <summary>The most a message holds (Discord's limit is 2000; the rest is room for the "more" line).</summary>
    public const int MaxChars = 1900;
    /// <summary>The most one line holds.</summary>
    public const int MaxLineChars = 200;

    /// <summary>Makes a name plain text in Discord. A backslash goes before each of: <c>\</c> (the escape itself),
    /// <c>* _ ~ ` |</c> (bold, italic, strike, code, spoiler), <c>&gt; #  -</c> (quote, heading and list marks at a
    /// line start), <c>[ ] ( )</c> (links), <c>&lt; : @</c> (mentions, channel links and custom emoji such as
    /// <c>&lt;@123&gt;</c>, <c>&lt;:a:1&gt;</c>, <c>@everyone</c>). Line breaks become spaces so a name cannot start
    /// a new line of its own.</summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(' ');
                continue;
            }
            if ("\\*_~`|>#-[]()<:@".IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Mark(string severity) => severity switch
    {
        "critical" => char.ConvertFromUtf32(0x1F534),
        "warning" => char.ConvertFromUtf32(0x1F7E0),
        "info" => char.ConvertFromUtf32(0x1F535),
        _ => "",
    };

    /// <summary>One line: the severity mark, <c>**character**</c>, the event's name, and <c>×count</c> from 2; cut to
    /// <see cref="MaxLineChars"/>. <paramref name="eventName"/> turns the event key into the name shown.</summary>
    public static string Line(PushLine line, Func<string, string> eventName)
    {
        var mark = Mark(line.Severity);
        var text = (mark.Length > 0 ? mark + " " : "") + "**" + Escape(line.Character) + "** " + Escape(eventName(line.EventKey));
        if (line.Count >= 2) text += " " + (char)0xD7 + line.Count;
        if (text.Length <= MaxLineChars) return text;
        int cut = MaxLineChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text.Substring(0, cut);
    }

    /// <summary>The lines of a batch, one per row. When the next line would pass <see cref="MaxChars"/>, the rest is
    /// counted in one last line (<c>L.Push.More</c>, with the number as its only argument).</summary>
    public static string Build(PushBatch batch, Func<string, string> eventName, Func<string, string, string> str)
    {
        var lines = batch.Lines;
        var more = str("L.Push.More", "…and {0} more");
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = Line(lines[i], eventName);
            int left = lines.Count - i - 1;
            int need = sb.Length + (sb.Length > 0 ? 1 : 0) + line.Length
                       + (left > 0 ? 1 + string.Format(more, left).Length : 0);
            if (need > MaxChars)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(string.Format(more, lines.Count - i));
                break;
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }
}
