using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

/// <summary>Every page of the new settings window, in navigation order (spec §5).
/// Task 11/12 add the App pages; Plan 3 adds Home and the other groups.</summary>
public static class SettingsRegistry
{
    public static IReadOnlyList<PageDef> Pages { get; } = BuildPages();

    private static IReadOnlyList<PageDef> BuildPages() => new List<PageDef>(AppPages.All);

    /// <summary>Settings the shell itself edits (not through a card): the Simple/Advanced switch.</summary>
    public static IReadOnlyList<string> ShellBinds { get; } = new[] { "AppSettings.SimpleMode" };

    public static PageDef? Page(string id) => Pages.FirstOrDefault(p => p.Id == id);

    public static PageDef? PageOf(string itemId) =>
        Pages.FirstOrDefault(p => p.Sections.Any(s => s.Items.Any(i => i.Id == itemId)));

    public static IEnumerable<PageItem> Items => Pages.SelectMany(p => p.Sections).SelectMany(s => s.Items);
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
