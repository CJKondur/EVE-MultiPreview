using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

/// <summary>Every page of the new settings window, in navigation order (spec §5), plus the
/// templates of pages built at run time.</summary>
public static class SettingsRegistry
{
    public static IReadOnlyList<PageDef> Pages { get; } = BuildPages();

    /// <summary>One instance of each page built at run time (the character page), so the registry,
    /// coverage, string and help tests check its cards. Never in the navigation or the search.</summary>
    public static IReadOnlyList<PageDef> Templates { get; } = BuildTemplates();

    public static IEnumerable<PageDef> AllPages => Pages.Concat(Templates);

    private static IReadOnlyList<PageDef> BuildPages() => HomePage.All.Concat(ClientsPages.All).Concat(ThumbnailsPages.All).Concat(AlertsPages.All)
            .Concat(PerformancePages.All).Concat(AppPages.All).ToList();

    private static IReadOnlyList<PageDef> BuildTemplates() => new List<PageDef> { CharacterPage.Template };

    /// <summary>Builds a page from its id ("character/Logi"), or null.</summary>
    public static PageDef? Dynamic(string id) => CharacterPage.TryBuild(id);

    /// <summary>Settings the shell itself edits (not through a card): the Simple/Advanced switch, and
    /// the colour dialog's shared custom palette.</summary>
    public static IReadOnlyList<string> ShellBinds { get; } = new[] { "AppSettings.SimpleMode", "AppSettings.CustomColorPalette" };

    public static PageDef? Page(string id) => Pages.FirstOrDefault(p => p.Id == id);

    public static PageDef? PageOf(string itemId) =>
        AllPages.FirstOrDefault(p => p.Sections.Any(s => s.Items.Any(i => i.Id == itemId)));

    public static IEnumerable<PageItem> Items => AllPages.SelectMany(p => p.Sections).SelectMany(s => s.Items);
    public static IEnumerable<SettingDescriptor> Settings => Items.OfType<SettingItem>().Select(i => i.Setting);
    public static IEnumerable<ActionDef> Actions => Items.OfType<ActionRow>().SelectMany(r => r.Actions);

    /// <summary>"AppSettings.X" / "Profile.X" names bound by any card, editor or the shell.</summary>
    public static IEnumerable<string> Binds =>
        Settings.SelectMany(d => d.Binds)
                .Concat(Items.OfType<EditorItem>().SelectMany(e => e.Binds))
                .Concat(ShellBinds);

    /// <summary>Old x:Names and handler names claimed by any card, button or editor.</summary>
    public static IEnumerable<string> Replaces =>
        Settings.SelectMany(d => d.Replaces)
                .Concat(Actions.SelectMany(a => a.Replaces))
                .Concat(Items.OfType<EditorItem>().SelectMany(e => e.Replaces));
}
