using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §5 Clients: Characters, Hotkeys &amp; switching, Groups, Windows &amp; positions, Game audio.</summary>
public static class ClientsPages
{
    public const string CharactersId = "clients.characters";
    public const string HotkeysId = "clients.hotkeys";
    public const string GroupsId = "clients.groups";
    public const string WindowsId = "clients.windows";
    public const string AudioId = "clients.audio";

    public static IReadOnlyList<PageDef> All => new[] { Groups() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(GroupsId, "keys", new[] { "clients.groups.editor" }),
        new HelpTopic(GroupsId, "scope", new[] { "clients.groups.showBorders" }),
    };

    private static PageDef Groups() => new(GroupsId, NavGroup.Clients, new[]
    {
        new SectionDef("clients.groups.borders", new PageItem[]
        {
            // ChkShowAllBorders (Thumbnails › Text & borders) and ChkShowGroupBorders both wrote
            // ShowAllColoredBorders; the new window shows it once, here (spec §5).
            new SettingItem(Setting.Toggle("clients.groups.showBorders", s => s.ShowAllColoredBorders, ApplyScope.Thumbnails) with
            {
                Replaces = new[] { "ChkShowGroupBorders", "ChkShowAllBorders" },
            }),
        }),
        new SectionDef("clients.groups.list", new PageItem[]
        {
            new EditorItem("clients.groups.editor", ctx => new GroupsEditor(ctx))
            {
                Binds = new[] { "AppSettings.ThumbnailGroups", "AppSettings.HotkeyGroups" },
                Replaces = new[]
                {
                    "CmbGroupSelect", "TxtGroupName", "TxtGroupColor", "TxtGroupChars",
                    "CmbHotkeyGroup", "TxtHotkeyGroupChars", "TxtGroupFwd", "TxtGroupBwd",
                    "OnGroupSave", "OnGroupDelete", "OnGroupAddChar", "OnGroupAddAllActive",
                    "OnHotkeyGroupNew", "OnHotkeyGroupDelete", "OnHotkeyGroupAddChar", "OnHotkeyGroupAddAllActive",
                },
            },
        }),
    });
}
