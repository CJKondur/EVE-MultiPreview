using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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

    public static IReadOnlyList<PageDef> All => new[] { Characters(), Hotkeys(), Groups() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(CharactersId, "table", new[] { "clients.characters.table" }),
        new HelpTopic(CharactersId, "clicks", new[] { "clients.characters.ctrlClick", "clients.characters.shiftClick", "clients.characters.customColors" }),
        new HelpTopic(HotkeysId, "scope", new[] { "clients.hotkeys.scope", "clients.hotkeys.wildcard", "clients.hotkeys.hideKeystrokes" }),
        new HelpTopic(HotkeysId, "held", new[] { "clients.hotkeys.carryModifiers", "clients.hotkeys.carryKeys", "clients.hotkeys.hideHeld", "clients.hotkeys.broadcastHud" }),
        new HelpTopic(HotkeysId, "cycling", new[]
        {
            "clients.hotkeys.cycleNext", "clients.hotkeys.cyclePrevious", "clients.hotkeys.cycleWhileHeld", "clients.hotkeys.cycleDelay",
            "clients.hotkeys.wrapSound", "clients.hotkeys.wrapSoundFile", "clients.hotkeys.includeLogins",
        }),
        new HelpTopic(HotkeysId, "app", new[]
        {
            "clients.hotkeys.suspend", "clients.hotkeys.clickThrough", "clients.hotkeys.hideAll", "clients.hotkeys.hidePrimary",
            "clients.hotkeys.hidePip", "clients.hotkeys.hideCrops", "clients.hotkeys.quickSwitch", "clients.hotkeys.lockPositions",
            "clients.hotkeys.undo", "clients.hotkeys.redo", "clients.hotkeys.profileNext", "clients.hotkeys.profilePrevious", "clients.hotkeys.closeAll",
        }),
        new HelpTopic(HotkeysId, "conflicts", new[] { "clients.hotkeys.conflicts" }),
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

    /// <summary>Sound files MediaPlayer plays (the wrap-around sound).</summary>
    private static string SoundFilter =>
        LocalizationService.Str("L.S.Filter.Sounds", "Sound files") + " (*.wav, *.mp3)|*.wav;*.mp3|*.*|*.*";

    private static SettingItem Toggle(string id, Expression<Func<AppSettings, bool>> prop, params string[] replaces) =>
        new(Setting.Toggle(id, prop) with { Replaces = replaces });

    private static SettingItem Hotkey(string id, Expression<Func<AppSettings, string>> prop, params string[] replaces) =>
        new(Setting.Hotkey(id, prop) with { Replaces = replaces });

    // Hotkey settings are read when App re-registers on Applied (RegisterFromSettings), or live
    // (PropagateHeld*, ShowBroadcastKeyHud, CycleDelayMs, IncludeLoginScreensInCycle): Save for all.
    private static PageDef Hotkeys() => new(HotkeysId, NavGroup.Clients, new[]
    {
        new SectionDef("clients.hotkeys.behaviour", new PageItem[]
        {
            new SettingItem(Setting.Segmented("clients.hotkeys.scope", s => s.GlobalHotkeys, new[]
            {
                new ChoiceOption(true, "L.S.Opt.ScopeEverywhere"),
                new ChoiceOption(false, "L.S.Opt.ScopeEve"),
            }) with { Replaces = new[] { "CmbHotkeyScope" } }),
            Toggle("clients.hotkeys.wildcard", s => s.HotkeyWildcardModifiers, "ChkHotkeyWildcard"),
            Toggle("clients.hotkeys.hideKeystrokes", s => s.HideHotkeyKeystrokes, "ChkHideHotkeyKeystrokes"),
            Toggle("clients.hotkeys.carryModifiers", s => s.PropagateHeldModifiers, "ChkPropagateModifiers"),
            Toggle("clients.hotkeys.carryKeys", s => s.PropagateHeldKeys, "ChkPropagateKeys"),
            Toggle("clients.hotkeys.hideHeld", s => s.HideHeldKeysOnSwitch, "ChkHideHeldKeys"),
            Toggle("clients.hotkeys.broadcastHud", s => s.ShowBroadcastKeyHud, "ChkBroadcastHud"),
        }),
        new SectionDef("clients.hotkeys.cycling", new PageItem[]
        {
            Hotkey("clients.hotkeys.cycleNext", s => s.GlobalCycleForwardHotkey),
            Hotkey("clients.hotkeys.cyclePrevious", s => s.GlobalCycleBackwardHotkey),
            Toggle("clients.hotkeys.cycleWhileHeld", s => s.CycleWhileHeld, "ChkCycleWhileHeld"),
            new SettingItem(Setting.Number("clients.hotkeys.cycleDelay", s => s.CycleDelayMs, 25, 2000, "L.S.Unit.Ms") with
            {
                ShowIf = s => s.CycleWhileHeld,
                Replaces = new[] { "TxtCycleDelay" },
            }),
            Toggle("clients.hotkeys.wrapSound", s => s.CycleWrapSoundEnabled),
            new SettingItem(Setting.File("clients.hotkeys.wrapSoundFile", s => s.CycleWrapSoundFile) with
            {
                ShowIf = s => s.CycleWrapSoundEnabled,
                FileFilter = SoundFilter,
            }),
            Toggle("clients.hotkeys.includeLogins", s => s.IncludeLoginScreensInCycle),
        }),
        new SectionDef("clients.hotkeys.app", new PageItem[]
        {
            Hotkey("clients.hotkeys.suspend", s => s.SuspendHotkey, "TxtSuspendHotkey", "OnCaptureHotkey"),
            Hotkey("clients.hotkeys.clickThrough", s => s.ClickThroughHotkey, "TxtClickThroughHotkey"),
            Hotkey("clients.hotkeys.hideAll", s => s.HideShowThumbnailsHotkey, "TxtHideShowHotkey"),
            Hotkey("clients.hotkeys.hidePrimary", s => s.HidePrimaryHotkey, "TxtHidePrimaryHotkey"),
            Hotkey("clients.hotkeys.hidePip", s => s.HideSecondaryHotkey, "TxtHideSecondaryHotkey"),
            Hotkey("clients.hotkeys.hideCrops", s => s.HideShowCropsHotkey, "TxtHideCropsHotkey"),
            Hotkey("clients.hotkeys.quickSwitch", s => s.QuickSwitchHotkey, "TxtQuickSwitchHotkey"),
            Hotkey("clients.hotkeys.lockPositions", s => s.LockPositionsHotkey),
            Hotkey("clients.hotkeys.undo", s => s.UndoLayoutHotkey, "TxtUndoLayoutHotkey"),
            Hotkey("clients.hotkeys.redo", s => s.RedoLayoutHotkey, "TxtRedoLayoutHotkey"),
            Hotkey("clients.hotkeys.profileNext", s => s.ProfileCycleForwardHotkey, "TxtProfileCycleForward"),
            Hotkey("clients.hotkeys.profilePrevious", s => s.ProfileCycleBackwardHotkey, "TxtProfileCycleBackward"),
            Hotkey("clients.hotkeys.closeAll", s => s.CloseAllClientsHotkey, "TxtCloseAllClientsHotkey"),
        }),
        new SectionDef("clients.hotkeys.check", new PageItem[]
        {
            new EditorItem("clients.hotkeys.conflicts", ctx => new HotkeyConflictsEditor(ctx)) { Replaces = new[] { "OnRefreshHotkeyAudit" } },
        }),
    });

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
