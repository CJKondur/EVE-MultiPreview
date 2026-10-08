using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Views;

/// <summary>GitHub release notes (Markdown) for the update dialog (Plan 4): a "### heading" becomes a bold paragraph and
/// "* item" a bullet; the rest is the help pane's markup (HelpMarkup: paragraphs, "- " bullets, **bold**). Links and other
/// Markdown stay as written. A heading is 1-6 "#" and a space ("#108 fixed" is text); output is capped at MaxBlocks so a
/// huge note can't freeze the dialog.</summary>
public static class ReleaseNotesMarkup
{
    public const int MaxBlocks = 300;
    private static readonly Regex Heading = new(@"^#{1,6}\s", RegexOptions.Compiled);

    public static IReadOnlyList<HelpBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<HelpBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var t = line.Trim();
            if (Heading.IsMatch(t))
            {
                var heading = t.TrimStart('#').Replace("**", "").Trim();
                return heading.Length == 0 ? "" : "\n**" + heading + "**\n";   // its own paragraph
            }
            return t.StartsWith("* ", StringComparison.Ordinal) ? "- " + t[2..] : line;
        });
        var blocks = HelpMarkup.Parse(string.Join("\n", lines));
        if (blocks.Count <= MaxBlocks) return blocks;
        return blocks.Take(MaxBlocks).Append(new HelpParagraph(new[] { new HelpRun("…", false) })).ToList();
    }
}
