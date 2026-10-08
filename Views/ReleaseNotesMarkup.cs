using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Views;

/// <summary>GitHub release notes (Markdown) for the update dialog (Plan 4): a "### heading" becomes a bold paragraph and
/// "* item" a bullet; the rest is the help pane's markup (HelpMarkup: paragraphs, "- " bullets, **bold**). Links and other
/// Markdown stay as written.</summary>
public static class ReleaseNotesMarkup
{
    public static IReadOnlyList<HelpBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<HelpBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var t = line.Trim();
            if (t.StartsWith('#'))
            {
                var heading = t.TrimStart('#').Trim();
                return heading.Length == 0 ? "" : "\n**" + heading + "**\n";   // its own paragraph
            }
            return t.StartsWith("* ", StringComparison.Ordinal) ? "- " + t[2..] : line;
        });
        return HelpMarkup.Parse(string.Join("\n", lines));
    }
}
