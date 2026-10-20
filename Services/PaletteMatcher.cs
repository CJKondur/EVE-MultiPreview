using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EveMultiPreview.Services;

/// <summary>IDEAS #14: what a palette row stands for. The order here is the order of the rows when two tie.</summary>
public enum PaletteKind { Character, Label, Group, Profile }

/// <summary>IDEAS #14: one row the palette may show. <see cref="Name"/> is the text typed against (a character, a label, a group
/// or a profile name); <see cref="Command"/> is what Enter runs, handed back untouched so the window can pass it to the same
/// runner as the remote commands. A row with no name is never matched.</summary>
public sealed record PaletteEntry(PaletteKind Kind, string? Name, RemoteCommand Command);

/// <summary>IDEAS #14: the pure matching of the type-to-switch palette. Case- and accent-insensitive. Every space-separated token
/// of the query must be the start of a word of the name or appear anywhere in it. A name whose tokens all start a word ranks above
/// one that needs a substring; then the shorter name; then the kind (character, label, group, profile); then ordinal.</summary>
public static class PaletteMatcher
{
    public const int MaxResults = 12;
    public const int MaxQueryLength = 64;

    /// <summary>The rows to show for <paramref name="query"/>, best first, at most <see cref="MaxResults"/>. An empty (or blank)
    /// query gives the characters only, in the order handed in. A query over <see cref="MaxQueryLength"/> characters is cut.</summary>
    public static IReadOnlyList<PaletteEntry> Match(string? query, IEnumerable<PaletteEntry> entries)
    {
        var named = entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
        var text = query ?? "";
        if (text.Length > MaxQueryLength) text = text[..MaxQueryLength];
        var tokens = Fold(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return named.Where(e => e.Kind == PaletteKind.Character).Take(MaxResults).ToList();

        var ranked = new List<(PaletteEntry Entry, bool AllWordPrefix)>();
        foreach (var entry in named)
        {
            var folded = Fold(entry.Name!);
            var words = Words(folded).ToList();
            bool allPrefix = true;
            bool matches = true;
            foreach (var token in tokens)
            {
                if (words.Any(w => w.StartsWith(token, StringComparison.OrdinalIgnoreCase))) continue;
                allPrefix = false;
                if (!folded.Contains(token, StringComparison.OrdinalIgnoreCase)) { matches = false; break; }
            }
            if (matches) ranked.Add((entry, allPrefix));
        }
        return ranked
            .OrderBy(r => r.AllWordPrefix ? 0 : 1)
            .ThenBy(r => r.Entry.Name!.Length)
            .ThenBy(r => r.Entry.Kind)
            .ThenBy(r => r.Entry.Name, StringComparer.Ordinal)
            .Take(MaxResults)
            .Select(r => r.Entry)
            .ToList();
    }

    /// <summary>The words of a name: runs of letters and digits; anything else (space, hyphen, apostrophe) breaks a word.</summary>
    private static IEnumerable<string> Words(string text)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0) start = i;
            else if (!inWord && start >= 0) { yield return text[start..i]; start = -1; }
        }
    }

    /// <summary>The text with its accents taken off (<c>José</c> to <c>Jose</c>); case is left to the comparisons. Text that is
    /// not valid Unicode (a lone surrogate, say from a cut) cannot be decomposed and is returned as it is.</summary>
    private static string Fold(string text)
    {
        string decomposed;
        try { decomposed = text.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { return text; }
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }
}
