using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

public sealed record HelpRun(string Text, bool Bold);
public abstract record HelpBlock(IReadOnlyList<HelpRun> Runs);
public sealed record HelpParagraph(IReadOnlyList<HelpRun> Runs) : HelpBlock(Runs);
public sealed record HelpBullet(IReadOnlyList<HelpRun> Runs) : HelpBlock(Runs);

/// <summary>Minimal help markup (spec §9): blank line = new paragraph, "- " line = bullet,
/// **text** = bold. Single line breaks inside a paragraph join with a space (no hand wrapping).</summary>
public static class HelpMarkup
{
    public static IReadOnlyList<HelpBlock> Parse(string text)
    {
        var blocks = new List<HelpBlock>();
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count > 0) blocks.Add(new HelpParagraph(Inline(string.Join(" ", paragraph))));
            paragraph.Clear();
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { FlushParagraph(); continue; }
            if (line.StartsWith("- ")) { FlushParagraph(); blocks.Add(new HelpBullet(Inline(line[2..]))); continue; }
            paragraph.Add(line);
        }
        FlushParagraph();
        return blocks;
    }

    /// <summary>**bold** spans. An odd number of markers leaves the text exactly as written.</summary>
    public static IReadOnlyList<HelpRun> Inline(string s)
    {
        var parts = s.Split("**");
        if (parts.Length % 2 == 0) return new[] { new HelpRun(s, false) };
        return parts.Select((p, i) => new HelpRun(p, i % 2 == 1)).Where(r => r.Text.Length > 0).ToList();
    }
}
