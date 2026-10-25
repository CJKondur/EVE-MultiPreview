using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

public enum SearchKind { Page, Section, Setting, Action, Help, Character, Event }

/// <summary>Text is shown; Alt (English label, description, help body) only matches.
/// ItemId is the card to highlight; null for a page.</summary>
public sealed record SearchEntry(SearchKind Kind, string Text, string Alt, string Path, string PageId, string? ItemId);

public static class SearchIndex
{
    /// <summary>resolve(key) is the current language ("" when missing); english(key) the English text.</summary>
    public static IReadOnlyList<SearchEntry> Build(IEnumerable<PageDef> pages, IEnumerable<HelpTopic> topics,
        Func<string, string> resolve, Func<string, string> english, IEnumerable<SearchEntry>? extra = null)
    {
        var list = new List<SearchEntry>();
        var paths = new Dictionary<string, string>();
        foreach (var page in pages)
        {
            var group = resolve("L.S.Nav." + page.Group);
            var title = resolve(page.TitleKey);
            var path = page.Group == NavGroup.Home ? title : $"{group} › {title}";
            paths[page.Id] = path;
            list.Add(new SearchEntry(SearchKind.Page, title, english(page.TitleKey) + " " + resolve(page.DescKey), group, page.Id, null));
            foreach (var section in page.Sections)
            {
                var sectionTitle = resolve(section.TitleKey);
                Add(list, SearchKind.Section, sectionTitle, english(section.TitleKey), path, page.Id, section.Items.FirstOrDefault()?.Id);
                foreach (var item in section.Items)
                {
                    switch (item)
                    {
                        case SettingItem s:
                            Add(list, SearchKind.Setting, resolve(s.Setting.LabelKey),
                                Join(english(s.Setting.LabelKey), resolve(s.Setting.DescKey), sectionTitle), path, page.Id, s.Id);
                            break;
                        case ActionRow r:
                            Add(list, SearchKind.Setting, resolve(r.LabelKey), Join(english(r.LabelKey), resolve(r.DescKey), sectionTitle), path, page.Id, r.Id);
                            foreach (var a in r.Actions)
                                Add(list, SearchKind.Action, AccessKeys.Spoken(resolve(a.LabelKey)), AccessKeys.Spoken(english(a.LabelKey)), path, page.Id, r.Id);   // a button's "_" is not for search (IDEAS #51)
                            break;
                        case EditorItem e:
                            Add(list, SearchKind.Setting, AccessKeys.Spoken(resolve(e.LabelKey)),
                                Join(AccessKeys.Spoken(english(e.LabelKey)), resolve(e.LabelKey + ".Desc"), sectionTitle), path, page.Id, e.Id);
                            break;
                    }
                }
            }
        }
        foreach (var t in topics)
        {
            if (!paths.TryGetValue(t.PageId, out var path)) continue;
            Add(list, SearchKind.Help, resolve(t.TitleKey), Join(english(t.TitleKey), resolve(t.BodyKey), english(t.BodyKey)),
                path, t.PageId, t.SettingIds.FirstOrDefault());
        }
        if (extra != null) list.AddRange(extra);
        return list;
    }

    public static IReadOnlyList<SearchEntry> Query(IReadOnlyList<SearchEntry> index, string query, int max = 20)
    {
        var q = query.Trim();
        if (!IsSearchable(q)) return Array.Empty<SearchEntry>();
        return index
            .Select(e => (e, rank: e.Text.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0
                                 : e.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase) ? 1
                                 : e.Alt.Contains(q, StringComparison.CurrentCultureIgnoreCase) ? 2 : -1))
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank).ThenBy(x => x.e.Kind == SearchKind.Help ? 1 : 0).ThenBy(x => x.e.Text.Length)
            .Select(x => x.e).Take(max).ToList();
    }

    /// <summary>Two characters, or one Chinese, Japanese or Korean character, which is already a word ("音", "열").</summary>
    public static bool IsSearchable(string query)
    {
        var q = query.Trim();
        return q.Length >= 2 || (q.Length == 1 && IsCjk(q[0]));
    }

    private static bool IsCjk(char c) =>
        c is >= '\u3040' and <= '\u30FF'      // Hiragana, Katakana
          or >= '\u3400' and <= '\u4DBF'      // CJK Extension A
          or >= '\u4E00' and <= '\u9FFF'      // CJK Unified Ideographs
          or >= '\uAC00' and <= '\uD7AF'      // Hangul syllables
          or >= '\u1100' and <= '\u11FF'      // Hangul Jamo
          or >= '\u3130' and <= '\u318F'      // Hangul compatibility Jamo
          or >= '\uF900' and <= '\uFAFF'      // CJK compatibility ideographs
          or >= '\uFF66' and <= '\uFF9F';     // half-width Katakana

    private static void Add(List<SearchEntry> list, SearchKind kind, string text, string alt, string path, string pageId, string? itemId)
    {
        if (text.Length > 0) list.Add(new SearchEntry(kind, text, alt, path, pageId, itemId));
    }

    private static string Join(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));
}
