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

    public static IReadOnlyList<PageDef> All => new[] { Characters(), Hotkeys(), Groups(), Windows(), Audio() };

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
        new HelpTopic(HotkeysId, "cycleFilters", new[] { "clients.hotkeys.cycleSameSystem", "clients.hotkeys.cycleAlerting", "clients.hotkeys.cycleSkipMuted" }),
        new HelpTopic(HotkeysId, "back", new[] { "clients.hotkeys.backToPrevious" }),
        new HelpTopic(HotkeysId, "lastAlert", new[] { "clients.hotkeys.lastAlert" }),
        new HelpTopic(HotkeysId, "app", new[]
        {
            "clients.hotkeys.suspend", "clients.hotkeys.clickThrough", "clients.hotkeys.hideAll", "clients.hotkeys.hidePrimary",
            "clients.hotkeys.hidePip", "clients.hotkeys.hideCrops", "clients.hotkeys.quickSwitch", "clients.hotkeys.lockPositions",
            "clients.hotkeys.undo", "clients.hotkeys.redo", "clients.hotkeys.profileNext", "clients.hotkeys.profilePrevious", "clients.hotkeys.closeAll",
            "clients.hotkeys.openSettings",
        }),
        new HelpTopic(HotkeysId, "sheet", new[] { "clients.hotkeys.sheet" }),
        new HelpTopic(HotkeysId, "palette", new[] { "clients.hotkeys.commandPalette" }),
        new HelpTopic(HotkeysId, "gamepad", new[] { "clients.hotkeys.gamepad", "clients.hotkeys.gamepadBindings" }),
        new HelpTopic(HotkeysId, "conflicts", new[] { "clients.hotkeys.conflicts" }),
        new HelpTopic(HotkeysId, "refused", new[] { RefusedHotkeysEditor.ItemId }),
        new HelpTopic(GroupsId, "keys", new[] { "clients.groups.editor" }),
        new HelpTopic(GroupsId, "scope", new[] { "clients.groups.showBorders" }),
        new HelpTopic(GroupsId, "focus", new[] { "clients.groups.focusKey", FocusGroupEditor.ItemId, "clients.groups.focusAlerts" }),
        new HelpTopic(WindowsId, "minimize", new[] { "clients.windows.minimizeInactive", "clients.windows.minimizeDelay", "clients.windows.neverMinimize" }),
        new HelpTopic(WindowsId, "position", new[]
        {
            "clients.windows.positionMode", "clients.windows.positionX", "clients.windows.positionY",
            "clients.windows.alwaysMaximize", "clients.windows.trackPositions", "clients.windows.coverTaskbar",
        }),
        new HelpTopic(WindowsId, "slots", new[] { "clients.windows.slotEditor", "clients.windows.slotsResize" }),
        new HelpTopic(WindowsId, "charSelect", new[] { "clients.windows.charSelectCycle", "clients.windows.charSelectNext", "clients.windows.charSelectPrevious" }),
        new HelpTopic(AudioId, "solo", new[] { "clients.audio.autoSolo", "clients.audio.muteNoneActive" }),
        new HelpTopic(AudioId, "volume", new[] { "clients.audio.volume" }),
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
            Toggle("clients.hotkeys.cycleSameSystem", s => s.CycleOnlySameSystem),
            Toggle("clients.hotkeys.cycleAlerting", s => s.CycleOnlyAlerting),
            Toggle("clients.hotkeys.cycleSkipMuted", s => s.CycleSkipMuted),
        }),
        // IDEAS batch 5: keys that go to one client. Unbound until the player sets them; App re-registers on Applied.
        new SectionDef("clients.hotkeys.jump", new PageItem[]
        {
            Hotkey("clients.hotkeys.backToPrevious", s => s.PreviousClientHotkey),
            Hotkey("clients.hotkeys.lastAlert", s => s.GoToLastAlertHotkey),
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
            Hotkey("clients.hotkeys.sheet", s => s.HotkeySheetHotkey),
            Hotkey("clients.hotkeys.openSettings", s => s.OpenSettingsHotkey),
            Hotkey("clients.hotkeys.commandPalette", s => s.CommandPaletteHotkey),
        }),
        // IDEAS #43: App polls the pad while the switch is on and reads the lines on every settings change (FollowGamepad).
        // A line that is not a button and a command of the list is refused here with a note; GamepadMap leaves it out anyway.
        new SectionDef("clients.hotkeys.gamepad", new PageItem[]
        {
            Toggle("clients.hotkeys.gamepad", s => s.GamepadEnabled),
            new SettingItem(Setting.Text("clients.hotkeys.gamepadBindings", s => s.GamepadBindings) with
            {
                ShowIf = s => s.GamepadEnabled,
                MultiLine = true,
                Validate = v => GamepadMap.Parse(v as string).Errors.Count == 0 ? null : "L.S.Err.GamepadBindings",
            }),
        }),
        new SectionDef("clients.hotkeys.check", new PageItem[]
        {
            new EditorItem("clients.hotkeys.conflicts", ctx => new HotkeyConflictsEditor(ctx)) { Replaces = new[] { "OnRefreshHotkeyAudit" } },
            // IDEAS #42: the hotkeys Windows refused at the last registration (HotkeyService.Refused). App state, not a setting.
            new EditorItem(RefusedHotkeysEditor.ItemId, ctx => new RefusedHotkeysEditor(ctx)),
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
                SearchLabelKey = "L.S.clients.groups.new",
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
        // IDEAS #35: the key is registered with the others on Applied; App reads the other two on its half-second timer.
        new SectionDef("clients.groups.focus", new PageItem[]
        {
            Hotkey("clients.groups.focusKey", s => s.FocusModeHotkey),
            new EditorItem(FocusGroupEditor.ItemId, ctx => new FocusGroupEditor(ctx)) { Binds = new[] { "AppSettings.FocusModeGroup" } },
            Toggle("clients.groups.focusAlerts", s => s.FocusModeShowAlerts),
        }),
    });

    // ThumbnailManager reads every setting here when a client is found or switched to: Save for all.
    private static PageDef Windows() => new(WindowsId, NavGroup.Clients, new[]
    {
        new SectionDef("clients.windows.minimizing", new PageItem[]
        {
            Toggle("clients.windows.minimizeInactive", s => s.MinimizeInactiveClients, "ChkMinimizeInactive"),
            new SettingItem(Setting.Number("clients.windows.minimizeDelay", s => s.MinimizeDelay, 0, 5000, "L.S.Unit.Ms") with
            {
                ShowIf = s => s.MinimizeInactiveClients,
                Replaces = new[] { "TxtMinimizeDelay" },
            }),
            new EditorItem("clients.windows.neverMinimize", ctx => new NeverMinimizeEditor(ctx))
            {
                ShowIf = s => s.MinimizeInactiveClients,
                Binds = new[] { "Profile.DontMinimizeClients" },
                Replaces = new[] { "LvDontMinimize", "OnDontMinAdd", "OnDontMinDelete", "OnDontMinAddAllActive" },
            },
        }),
        new SectionDef("clients.windows.placement", new PageItem[]
        {
            new SettingItem(Setting.Choice("clients.windows.positionMode", s => s.ClientPositionMode, new[]
            {
                new ChoiceOption(0, "L.S.Opt.PositionOff"),
                new ChoiceOption(1, "L.S.Opt.PositionCenter"),
                new ChoiceOption(2, "L.S.Opt.PositionCustom"),
                new ChoiceOption(3, "L.S.Opt.PositionSlots"),
            }) with { Replaces = new[] { "CmbClientPosition" } }),
            new SettingItem(Setting.Number("clients.windows.positionX", s => s.ClientPositionX, -20000, 20000, "L.S.Unit.Px") with
            {
                ShowIf = s => s.ClientPositionMode == 2, Replaces = new[] { "TxtClientPositionX" },
            }),
            new SettingItem(Setting.Number("clients.windows.positionY", s => s.ClientPositionY, -20000, 20000, "L.S.Unit.Px") with
            {
                ShowIf = s => s.ClientPositionMode == 2, Replaces = new[] { "TxtClientPositionY" },
            }),
            // Fixed slots own the window rect, so these two are ignored (the old window greyed them out).
            new SettingItem(Setting.Toggle("clients.windows.alwaysMaximize", s => s.AlwaysMaximize) with
            {
                ShowIf = s => s.ClientPositionMode != 3, Replaces = new[] { "ChkAlwaysMaximize" },
            }),
            new SettingItem(Setting.Toggle("clients.windows.trackPositions", s => s.TrackClientPositions) with
            {
                ShowIf = s => s.ClientPositionMode != 3, Replaces = new[] { "ChkTrackClientPositions" },
            }),
            Toggle("clients.windows.coverTaskbar", s => s.ClientCoverTaskbar, "ChkClientCoverTaskbar"),
        }),
        new SectionDef("clients.windows.slots", new PageItem[]
        {
            new EditorItem("clients.windows.slotEditor", ctx => new SlotsEditor(ctx))
            {
                ShowIf = s => s.ClientPositionMode == 3,
                Binds = new[] { "AppSettings.ClientSlots", "AppSettings.DefaultClientSlotId", "AppSettings.ClientSlotExcluded" },
                Replaces = new[] { "OnSlotAdd", "OnSlotApplyNow" },
            },
            new SettingItem(Setting.Toggle("clients.windows.slotsResize", s => s.ClientSlotsResize) with
            {
                ShowIf = s => s.ClientPositionMode == 3,
            }),
        }),
        new SectionDef("clients.windows.charSelect", new PageItem[]
        {
            // RegisterFromSettings registers the two keys only while this is on (App re-registers on Applied).
            Toggle("clients.windows.charSelectCycle", s => s.CharSelectCyclingEnabled, "ChkCharSelectCycle"),
            new SettingItem(Setting.Hotkey("clients.windows.charSelectNext", s => s.CharSelectForwardHotkey) with
            {
                ShowIf = s => s.CharSelectCyclingEnabled, Replaces = new[] { "TxtCharSelectFwd" },
            }),
            new SettingItem(Setting.Hotkey("clients.windows.charSelectPrevious", s => s.CharSelectBackwardHotkey) with
            {
                ShowIf = s => s.CharSelectCyclingEnabled, Replaces = new[] { "TxtCharSelectBwd" },
            }),
        }),
    });

    private static PageDef Audio() => new(AudioId, NavGroup.Clients, new[]
    {
        new SectionDef("clients.audio.solo", new PageItem[]
        {
            new SettingItem(Setting.Toggle("clients.audio.autoSolo", s => s.AutoSoloClientAudio) with
            {
                // ReapplySettings unmuted everything when this was off; do just that, without a full
                // thumbnail re-apply. On takes effect at the next switch (ApplyAudioSolo on focus change).
                OnChanged = ctx => { if (!ctx.S.AutoSoloClientAudio) ctx.Services.Thumbnails?.UnmuteAllClientAudio(); },
                Replaces = new[] { "ChkAutoSoloAudio" },
            }),
            new SettingItem(Setting.Toggle("clients.audio.muteNoneActive", s => s.MuteAllClientAudioWhenNoneActive) with
            {
                ShowIf = s => s.AutoSoloClientAudio, Replaces = new[] { "ChkMuteAllWhenNoneActive" },
            }),
        }),
        new SectionDef("clients.audio.volumes", new PageItem[]
        {
            new EditorItem("clients.audio.volume", ctx => new VolumeEditor(ctx))
            {
                Binds = new[] { "AppSettings.PerClientAudioVolume" },
                Replaces = new[] { "OnRefreshClientVolumes" },
            },
        }),
    });
}
