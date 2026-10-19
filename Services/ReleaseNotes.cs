using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.Services;

/// <summary>P14: the update dialog's notes for a player several versions behind: the notes of every release newer
/// than the running version, newest first, each under a "Version X.Y.Z" line. Pure text; the release notes are
/// English on GitHub, so this one line of its own is English too.</summary>
public static class ReleaseNotes
{
    private static string OlderVersions(int count) => $"… and {count} older version" + (count == 1 ? "" : "s");

    /// <summary>The notes of the releases newer than <paramref name="current"/>. Drafts are left out, pre-releases
    /// unless <paramref name="includePrereleases"/>. A release whose Version is null is read from its Tag ("v1.2.3");
    /// a tag that is no version is skipped, never thrown. Over <paramref name="maxChars"/> the text is cut at a block
    /// boundary and ends with "… and N older versions"; the whole text, that line included, stays within the cap.
    /// No release, or none newer: "".</summary>
    public static string Combine(IEnumerable<(Version? Version, string Tag, string? Body, bool Prerelease, bool Draft)> releases,
        Version current, bool includePrereleases, int maxChars = 16000)
    {
        var blocks = new List<(Version Version, string Text)>();
        foreach (var r in releases)
        {
            if (r.Draft || (r.Prerelease && !includePrereleases)) continue;
            var version = r.Version;
            if (version is null && !Version.TryParse(r.Tag?.Trim().TrimStart('v', 'V'), out version)) continue;
            if (version <= current) continue;
            var body = r.Body?.Trim();
            blocks.Add((version, "Version " + version + (string.IsNullOrEmpty(body) ? "" : "\n\n" + body)));
        }
        if (blocks.Count == 0) return "";
        var ordered = blocks.OrderByDescending(b => b.Version).Select(b => b.Text).ToList();

        if (ordered.Sum(b => b.Length) + 2 * (ordered.Count - 1) <= maxChars) return string.Join("\n\n", ordered);

        // Over the cap: whole blocks only, newest first, leaving room for the closing line. The newest block is
        // always there, cut short when it alone is over the budget.
        int budget = maxChars - 2 - OlderVersions(ordered.Count).Length;
        var kept = new List<string>();
        int used = 0;
        foreach (var block in ordered)
        {
            int add = block.Length + (kept.Count == 0 ? 0 : 2);
            if (kept.Count > 0 && used + add > budget) break;
            kept.Add(block);
            used += add;
        }
        if (kept[0].Length > budget)
        {
            int keep = Math.Max(0, budget - 1);
            if (keep > 0 && char.IsHighSurrogate(kept[0][keep - 1])) keep--;
            kept[0] = kept[0][..keep].TrimEnd() + "…";
        }
        var left = ordered.Count - kept.Count;
        return left == 0 ? string.Join("\n\n", kept) : string.Join("\n\n", kept) + "\n\n" + OlderVersions(left);
    }
}
