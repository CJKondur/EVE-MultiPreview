using System;
using System.Globalization;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>Text a player or another player wrote (a rule's name, a character's name, a line a rule matched), made
/// safe to keep and to show. One place, so the alert history and the custom rules clean the same way.</summary>
public static class PlainText
{
    /// <summary>The text as one line of at most <paramref name="max"/> UTF-16 units. In this order:
    ///
    /// 1. Null gives "".
    /// 2. Every control character (char.IsControl: line breaks, tab, NUL, U+007F to U+009F) becomes a space, and so
    ///    does every other white-space character (char.IsWhiteSpace: the line and paragraph separators U+2028 and
    ///    U+2029, the no-break space, the ideographic space and the like).
    /// 3. Every character of Unicode category Format (Cf) is dropped, inside and outside the first 65,536: the
    ///    zero-width space and joiners (U+200B to U+200D), the marks, embeddings, overrides and isolates that turn the
    ///    reading direction round (U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069), the byte order mark (U+FEFF),
    ///    the soft hyphen, the tag characters. Text another player typed could use those to show a row as something it
    ///    isn't, or to make a name that looks empty. An emoji made of several joined by U+200D therefore shows as its
    ///    parts (a family as its people), which is accepted.
    /// 4. Half of a character that takes two UTF-16 units (a lone surrogate) is dropped: it is not text. The JSON
    ///    writer does not refuse it: it writes U+FFFD (the replacement character) in its place, so what is read back
    ///    from a file would not be what was kept. Two halves are a character only when they stand next to each other
    ///    in the text as given.
    /// 5. A run of spaces is one space, and there is none at the start or the end.
    /// 6. The result is cut to <paramref name="max"/> units, one less when the cut would fall between the two halves of
    ///    a character, and a space the cut left at the end is removed. A <paramref name="max"/> of 0 or less gives "".
    ///
    /// Cleaning a cleaned text with the same limit changes nothing, so a text read back from a file is the text that
    /// was written. Never throws. A text much longer than <paramref name="max"/> is only read as far as it is kept.</summary>
    public static string OneLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        var clean = new StringBuilder(Math.Min(text.Length, max));
        // One unit past the limit is enough to know where the cut falls.
        for (int i = 0; i < text.Length && clean.Length <= max; i++)
        {
            char c = text[i];
            if (char.IsSurrogate(c))
            {
                if (!char.IsHighSurrogate(c) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) continue;
                char low = text[++i];
                if (CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, low)) != UnicodeCategory.Format)
                    clean.Append(c).Append(low);
            }
            else if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (clean.Length > 0 && clean[^1] != ' ') clean.Append(' ');
            }
            else if (char.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                clean.Append(c);
            }
        }
        int length = Math.Min(clean.Length, max);
        if (length > 0 && char.IsHighSurrogate(clean[length - 1])) length--;
        while (length > 0 && clean[length - 1] == ' ') length--;
        return clean.ToString(0, length);
    }
}
