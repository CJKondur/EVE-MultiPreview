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

    public static IReadOnlyList<PageDef> All => new[] { Characters(), Groups() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(CharactersId, "table", new[] { "clients.characters.table" }),
        new HelpTopic(CharactersId, "clicks", new[] { "clients.characters.ctrlClick", "clients.characters.shiftClick", "clients.characters.customColors" }),
        new HelpTopic(GroupsId, "keys", new[] { "clients.groups.editor" }),
        new HelpTopic(GroupsId, "scope", new[] { "clients.groups.showBorders" }),
    };

    private static PageDef Characters() => new(CharactersId, NavGroup.Clients, new[]
    {
        new SectionDef("clients.characters.list", new PageItem[]
        {
            new EditorItem("clients.characters.table", ctx => new CharactersEditor(ctx))
            {
                Binds = new[] { "Profile.Hotkeys", "AppSettings.ThumbnailVisibility" },
                // The hotkey cell captures and clears (OnHotkeyEdit/OnHotkeyDelete). Running characters are
                // listed anyway, so the annotation and colour "add all" buttons have nothing left to do.
                Replaces = new[]
                {
                    "LvHotkeys", "LvVisibility", "OnHotkeyAdd", "OnHotkeyAddAllActive", "OnHotkeyEdit", "OnHotkeyDelete",
                    "OnRefreshVisibility", "OnAnnotationAddAllActive", "OnColorAddAllActive",
                },
            },
        }),
        new SectionDef("clients.characters.colors", new PageItem[]
        {
            new SettingItem(Setting.Toggle("clients.characters.customColors", s => s.CustomColorsActive, ApplyScope.Thumbnails) with
            {
                Replaces = new[] { "ChkCustomColorsActive" },
            }),
        }),
        new SectionDef("clients.characters.clicks", new PageItem[]
        {
            // Read live by ThumbnailWindow on every click: Save is enough.
            new SettingItem(Setting.Choice("clients.characters.ctrlClick", s => s.CtrlClickAction, ClickOptions()) with { Replaces = new[] { "CmbCtrlClick" } }),
            new SettingItem(Setting.Choice("clients.characters.shiftClick", s => s.ShiftClickAction, ClickOptions()) with { Replaces = new[] { "CmbShiftClick" } }),
        }),
    });

    private static IReadOnlyList<ChoiceOption> ClickOptions() => new[]
    {
        new ChoiceOption(0, "L.S.Opt.ClickSwitch"),
        new ChoiceOption(1, "L.S.Opt.ClickMinimize"),
        new ChoiceOption(2, "L.S.Opt.ClickExclude"),
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
