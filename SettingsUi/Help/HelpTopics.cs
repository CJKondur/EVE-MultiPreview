using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

/// <summary>One help topic: title and body strings plus the cards it explains (spec §9).</summary>
public sealed record HelpTopic(string PageId, string Id, IReadOnlyList<string> SettingIds)
{
    public string TitleKey => $"L.Help.{PageId}.{Id}.Title";
    public string BodyKey => $"L.Help.{PageId}.{Id}.Body";
}

public static class HelpTopics
{
    public static string IntroKey(string pageId) => $"L.Help.{pageId}.Intro";

    /// <summary>Every page's topics. Tasks 11–12 add the App pages'; Plan 3 adds the rest.</summary>
    public static IReadOnlyList<HelpTopic> All { get; } = Build();

    private static IReadOnlyList<HelpTopic> Build() => ClientsPages.Help.Concat(CharacterPage.Help).Concat(AppPages.Help).ToList();

    public static IEnumerable<HelpTopic> ForPage(string pageId) => All.Where(t => t.PageId == pageId);
    public static HelpTopic? ForSetting(string itemId) => All.FirstOrDefault(t => t.SettingIds.Contains(itemId));
}
