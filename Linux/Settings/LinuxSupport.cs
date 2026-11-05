using System.Diagnostics;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>What the Linux settings window shows of the shared page definitions (SettingsRegistry). Every card of every page
/// is classified here by id; a card shows only when it is Supported, and a section or page left without a card is left
/// out (in the pages, the search and the help). The Linux-only cards of the old Linux window (the Proton log-folder note,
/// the commands example, the import from the Windows app …) are added to the sections they belong to, and a few cards
/// say what they do on Linux (desktop notifications, not the Windows alert hub). Nothing here changes the Windows app.
///
/// "Supported" was decided by what the Linux app reads: the setting's property is read by the Linux code outside its
/// settings windows (tests/LinuxCheck follows the IL from every Linux type; LinuxSupportTests keeps the table honest).
/// A new card on Windows is unclassified until it is added here: it then shows (as before S3) and the check fails.</summary>
internal static class LinuxSupport
{
    public enum Support
    {
        /// <summary>The Linux app reads it (or the card works on Linux by itself): shown.</summary>
        Supported,
        /// <summary>Windows technology Linux has no counterpart for (DWM, RTSS, the registry, input injection, XInput,
        /// Windows notifications and themes, the Elgato plugin, the installer): hidden.</summary>
        WindowsOnly,
        /// <summary>Could work on Linux, but the Linux app does not read it yet (or has no editor for it): hidden.</summary>
        NotYet,
        /// <summary>Stored, but read by nothing on either platform: hidden. (None at S3.)</summary>
        Orphan,
    }

    private const Support S = Support.Supported, W = Support.WindowsOnly, N = Support.NotYet;

    /// <summary>Every card id of SettingsRegistry.AllPages (the character page's cards by their template ids).</summary>
    public static IReadOnlyDictionary<string, Support> Items { get; } = new Dictionary<string, Support>(StringComparer.Ordinal)
    {
        // ── Home ──
        ["home.simpleBanner"] = S, ["home.showAllRow"] = S, ["home.tiles"] = S, ["home.groupKeys"] = S,
        ["home.next"] = S, ["home.previous"] = S, ["home.size"] = S, ["home.opacity"] = S,
        ["home.monitor"] = S,                 // PreferredMonitor: Previews.Place / TargetWorkArea (S6)
        ["home.alertAttack"] = S, ["home.alertScram"] = S, ["home.alertDecloak"] = S, ["home.alertFleet"] = S, ["home.alertSound"] = S,
        ["home.quickActions"] = S,            // Linux/Settings/MoreEditors.cs (S3b)
        ["home.startWithWindows"] = W,        // the Windows Run registry key

        // ── Clients › Characters ──
        ["clients.characters.table"] = S, ["clients.characters.customColors"] = S,
        ["clients.characters.ctrlClick"] = S, ["clients.characters.shiftClick"] = S,   // Previews.ClickThumbnail (S3b)

        // ── Clients › Hotkeys ──
        ["clients.hotkeys.scope"] = S,
        ["clients.hotkeys.wildcard"] = S,     // S6: GlobalHotkeys grabs each key with every other modifier mix too
        ["clients.hotkeys.hideKeystrokes"] = W, ["clients.hotkeys.carryModifiers"] = W, ["clients.hotkeys.carryKeys"] = W,
        ["clients.hotkeys.hideHeld"] = W, ["clients.hotkeys.broadcastHud"] = W,          // low-level keyboard hook, SendInput
        ["clients.hotkeys.cycleNext"] = S, ["clients.hotkeys.cyclePrevious"] = S,
        ["clients.hotkeys.cycleWhileHeld"] = S, ["clients.hotkeys.cycleDelay"] = S,       // S6: GlobalHotkeys tells X key repeat from a press
        ["clients.hotkeys.wrapSound"] = S, ["clients.hotkeys.wrapSoundFile"] = S,         // Previews.PlayWrapSound (S3b)
        ["clients.hotkeys.includeLogins"] = S,   // S6: login-screen clients have thumbnails; Previews.Cycle walks them in window order
        ["clients.hotkeys.cycleSameSystem"] = S, ["clients.hotkeys.cycleAlerting"] = S, ["clients.hotkeys.cycleSkipMuted"] = S,   // CycleFilter (S3b)
        ["clients.hotkeys.backToPrevious"] = S, ["clients.hotkeys.lastAlert"] = S,
        ["clients.hotkeys.suspend"] = S, ["clients.hotkeys.clickThrough"] = S, ["clients.hotkeys.hidePrimary"] = S,   // Previews.BindHotkeys (S3b)
        ["clients.hotkeys.quickSwitch"] = S,   // S8: Linux/QuickSwitchWindow.cs over the shared QuickSwitchLayout (Previews.ToggleQuickSwitch)
        ["clients.hotkeys.undo"] = S, ["clients.hotkeys.redo"] = S,   // Previews.UndoLayout / RedoLayout (S6)
        ["clients.hotkeys.profileNext"] = S, ["clients.hotkeys.profilePrevious"] = S, ["clients.hotkeys.closeAll"] = S,   // (S3b)
        ["clients.hotkeys.sheet"] = S,        // S6: Linux/HotkeySheetWindow.cs
        ["clients.hotkeys.openSettings"] = S, // (S3b)
        ["clients.hotkeys.hideAll"] = S, ["clients.hotkeys.hidePip"] = S, ["clients.hotkeys.hideCrops"] = S,
        ["clients.hotkeys.lockPositions"] = S, ["clients.hotkeys.commandPalette"] = S,
        ["clients.hotkeys.gamepad"] = W, ["clients.hotkeys.gamepadBindings"] = W,       // XInput
        ["clients.hotkeys.conflicts"] = S,    // S6: Linux/Settings/HotkeyEditors.cs
        ["clients.hotkeys.refused"] = W,      // keys RegisterHotKey refused

        // ── Clients › Groups ──
        ["clients.groups.showBorders"] = S, ["clients.groups.editor"] = S, ["clients.groups.focusKey"] = S,
        ["clients.groups.focusGroup"] = S, ["clients.groups.focusAlerts"] = S,

        // ── Clients › Windows & positions: the Linux app minimizes the other clients (S3b) and places them (S5) ──
        ["clients.windows.minimizeInactive"] = S, ["clients.windows.minimizeDelay"] = S, ["clients.windows.neverMinimize"] = S,   // Previews.ActivateClient
        // S5: Previews.ApplyFixedClientPosition / RestoreClientPosition over EWMH (_NET_MOVERESIZE_WINDOW, _NET_WM_STATE); the
        // tray has Save / Restore positions. S6: fixed slots (mode 3), Previews.ApplyClientSlot (_NET_FRAME_EXTENTS for the
        // visible frame), Linux/Settings/SlotEditors.cs, tray "Snap to slots".
        ["clients.windows.positionMode"] = S, ["clients.windows.positionX"] = S, ["clients.windows.positionY"] = S,
        ["clients.windows.alwaysMaximize"] = S, ["clients.windows.trackPositions"] = S,
        ["clients.windows.coverTaskbar"] = W, // the Windows taskbar
        ["clients.windows.slotEditor"] = S, ["clients.windows.slotsResize"] = S,
        ["clients.windows.charSelectCycle"] = S, ["clients.windows.charSelectNext"] = S, ["clients.windows.charSelectPrevious"] = S,   // Previews.CycleCharSelect (S6)

        // ── Clients › Audio ──
        ["clients.audio.autoSolo"] = S, ["clients.audio.muteNoneActive"] = S,   // Previews.FollowAudioSolo (S3b)
        ["clients.audio.volume"] = S,         // MoreEditors.Volumes (S3b), pactl

        // ── Thumbnails › Appearance ──
        ["thumbs.appearance.resizeAll"] = S,  // MoreEditors.ResizeAll (S3b)
        ["thumbs.appearance.individualResize"] = S, ["thumbs.appearance.lockAspect"] = S, ["thumbs.appearance.minSizeOn"] = S,
        ["thumbs.appearance.minWidth"] = S, ["thumbs.appearance.minHeight"] = S, ["thumbs.appearance.opacity"] = S,
        ["thumbs.appearance.opacityOnHover"] = S, ["thumbs.appearance.hoverZoom"] = S, ["thumbs.appearance.hoverScale"] = S,
        ["thumbs.appearance.background"] = S,   // S7: ThumbnailWindow.SetBackground, shown until the first picture

        // ── Thumbnails › Text & borders ──
        ["thumbs.text.showText"] = S, ["thumbs.text.showSystem"] = S, ["thumbs.text.showTimer"] = S,   // Previews.Extras (S3b)
        ["thumbs.text.template"] = S,         // S8: Linux/Settings/TextEditors.cs; Previews.TemplateLook ({jumps}: Alerts.Jumps, {week}: the playtime ledger)
        ["thumbs.text.streamer"] = S, ["thumbs.text.color"] = S, ["thumbs.text.font"] = S, ["thumbs.text.size"] = S,
        ["thumbs.text.marginX"] = S, ["thumbs.text.marginY"] = S,   // Previews.Extras (S3b)
        ["thumbs.text.showStats"] = S, ["thumbs.text.statsSize"] = S,   // S6: Previews.FollowProcessStats from /proc, ThumbnailWindow.SetStats
        ["thumbs.text.highlight"] = S,        // Previews.Look honours it (S3b), as ThumbnailManager.ApplyActiveBorders
        ["thumbs.text.highlightColor"] = S, ["thumbs.text.highlightWidth"] = S, ["thumbs.text.highlightInGroup"] = S,
        ["thumbs.text.inactiveColor"] = S, ["thumbs.text.inactiveWidth"] = S,
        ["thumbs.text.cornerRadius"] = S, ["thumbs.text.autoBorders"] = S, ["thumbs.text.groupBorders"] = S,   // corner radius: Previews.Extras (S3b)
        ["thumbs.text.badgePosition"] = S,    // ThumbnailWindow.SetCycleBadge (S3b)
        ["thumbs.text.notResponding"] = S, ["thumbs.text.disconnected"] = S, ["thumbs.text.disconnectSeconds"] = S,

        // ── Thumbnails › Layout ──
        ["thumbs.layout.monitor"] = S,        // S6: Linux/Settings/LayoutEditors.cs, Previews.TargetWorkArea
        ["thumbs.layout.identifyRow"] = W,    // MonitorIdentify: a badge window per Windows monitor
        ["thumbs.layout.startX"] = S, ["thumbs.layout.startY"] = S, ["thumbs.layout.startWidth"] = S, ["thumbs.layout.startHeight"] = S,
        ["thumbs.layout.gap"] = S,            // Previews.Place, ThumbnailManager.FlowSlot (S3b)
        ["thumbs.layout.snap"] = S, ["thumbs.layout.snapDistance"] = S, ["thumbs.layout.confine"] = S,   // Previews.SnapThumbnail, ThumbnailWindow.Confined (S5)
        // S6 (Linux/PreviewsLayout.cs, Linux/Settings/LayoutEditors.cs): saved layouts, the layout for the monitors (asked at
        // the 2 s client look), align / distribute / nudge, undo / redo / reset.
        ["thumbs.layout.lock"] = S, ["thumbs.layout.autoOnMonitorChange"] = S, ["thumbs.layout.presets"] = S,
        ["thumbs.layout.align"] = S, ["thumbs.layout.history"] = S,

        // ── Thumbnails › Visibility ──
        ["thumbs.visibility.alwaysOnTop"] = S, ["thumbs.visibility.aboveClients"] = S,   // S7: Previews.ThumbnailsTopmost (topmost only while EVE or MultiPreview is in front)
        ["thumbs.visibility.hideOnLostFocus"] = S,   // Previews.Tick (S3b)
        ["thumbs.visibility.hideActive"] = S, ["thumbs.nameBar"] = S,
        ["thumbs.visibility.notLoggedIn"] = S, ["thumbs.visibility.notLoggedInColor"] = S,   // Previews.LoginLook / LoginMark (S6)
        ["thumbs.visibility.pipTable"] = S,   // MoreEditors.PipTable (S3b)

        // ── Thumbnails › Crop windows ──
        ["thumbs.crops.enabled"] = S, ["thumbs.crops.off"] = S, ["thumbs.crops.editor"] = S,

        // ── Alerts › Events ──
        ["alerts.events.chatLog"] = S, ["alerts.events.chatFolder"] = S, ["alerts.events.gameLog"] = S, ["alerts.events.gameFolder"] = S,
        ["alerts.events.pve"] = S,            // Linux/Alerts.cs ApplyMonitorOptions (S3)
        ["alerts.events.underFire"] = S, ["alerts.events.underFireTime"] = S,   // S6: Alerts.UnderFire, Previews.UnderFireLook
        ["alerts.events.repeatWait"] = S, ["alerts.events.repeatBySeverity"] = S,
        ["alerts.events.table"] = S, ["alerts.events.ewarNote"] = S, ["alerts.events.stragglerWait"] = S,
        ["alerts.events.dpsThreshold"] = S, ["alerts.events.dpsSeconds"] = S, ["alerts.events.repsSeconds"] = S, ["alerts.events.combatNote"] = S,
        ["alerts.events.memoryLimit"] = S, ["alerts.events.memoryGrowth"] = S, ["alerts.events.memoryMinutes"] = S,
        ["alerts.events.clientNote"] = S, ["alerts.events.idleMinutes"] = S,
        ["alerts.events.groups"] = S,         // AlertProfiles; Linux/Settings/GroupAlertEditors.cs (S5)
        ["alerts.events.snoozed"] = S, ["alerts.events.keepSnoozes"] = S,

        // ── Alerts › Look ──
        ["alerts.look.pulse"] = S, ["alerts.look.borderWidth"] = S,
        ["alerts.look.badge"] = S, ["alerts.look.text"] = S, ["alerts.look.textPosition"] = S,   // Previews.Extras (S3b)
        ["alerts.look.enlarge"] = S, ["alerts.look.enlargeFrom"] = S, ["alerts.look.enlargePercent"] = S, ["alerts.look.enlargeSeconds"] = S,
        ["alerts.look.severityTable"] = S,    // MoreEditors.SeverityTable (S3b)
        ["alerts.look.waitBySeverity"] = S, ["alerts.look.waitCritical"] = S, ["alerts.look.waitWarning"] = S, ["alerts.look.waitInfo"] = S,
        ["alerts.look.colorBlind"] = S,       // MoreEditors.ColorBlind over the shared ColorBlindSwitch (S3b)

        // ── Alerts › Sounds & voice ──
        ["alerts.sounds.enabled"] = S, ["alerts.sounds.volume"] = S, ["alerts.sounds.dndHotkey"] = S,
        ["alerts.sounds.dndState"] = S,       // MoreEditors.DoNotDisturbLine (S3b)
        ["alerts.sounds.dndCritical"] = S, ["alerts.sounds.quietHours"] = S, ["alerts.sounds.quietFrom"] = S, ["alerts.sounds.quietTo"] = S,
        ["alerts.sounds.escalate"] = S, ["alerts.sounds.escalateAfter"] = S, ["alerts.sounds.escalateRepeats"] = S,   // Alerts.LookEscalation (S5)
        ["alerts.sounds.speak"] = S, ["alerts.sounds.speakFrom"] = S, ["alerts.sounds.speechVolume"] = S, ["alerts.sounds.voices"] = S,
        ["alerts.sounds.table"] = S,          // MoreEditors.SoundTable; Linux/Alerts.cs keeps the per-character Wait (S3b)
        ["alerts.sounds.groups"] = S,         // Alerts.PlaySound asks GroupSounds; Linux/Settings/GroupAlertEditors.cs (S5)

        // ── Alerts › Alert hub: on Linux the hub is the desktop's notifications (notify-send) ──
        ["alerts.hub.enabled"] = S, ["alerts.hub.skipActive"] = S,
        ["alerts.hub.duration"] = S,          // the notification's expire time (S3b)
        // The Windows hub window (its toasts' side, hiding when quiet, its place): Linux shows desktop notifications, which the
        // desktop places and stacks (S5).
        ["alerts.hub.direction"] = W, ["alerts.hub.autoHide"] = W, ["alerts.hub.autoHideAfter"] = W,
        ["alerts.hub.positionRow"] = W,
        ["alerts.hub.notify"] = W, ["alerts.hub.notifyFrom"] = W, ["alerts.hub.notifyTest"] = W,   // Windows notifications
        ["alerts.hub.push"] = S, ["alerts.hub.pushFrom"] = S, ["alerts.hub.pushMention"] = S, ["alerts.hub.pushAway"] = S,
        ["alerts.hub.pushWebhook"] = S, ["alerts.hub.historyRow"] = S, ["alerts.hub.keepHistory"] = S,
        ["alerts.hub.historyDays"] = S, ["alerts.hub.historyFolder"] = S,

        // ── Alerts › Custom alerts: the Linux app reads the player's rules (S3b, Alerts.CustomAlerts); the editor is
        //    Linux/Settings/CustomRulesEditors.cs (S5) ──
        ["alerts.custom.editor"] = S, ["alerts.custom.file"] = S,

        // ── Performance ──
        // Windows-only: an unprivileged Linux process may lower a client's priority (nice) but never raise it back
        // (RLIMIT_NICE), so the client in front could not get its normal priority again; and an affinity set on a Wine
        // process holds for its main thread only.
        ["perf.cpu.manage"] = W, ["perf.cpu.autoBalance"] = W, ["perf.cpu.coreTable"] = W,
        // S6: Previews.Tick draws the main thumbnails once a second / not while EVE and MultiPreview are behind.
        ["perf.gpu.presetRow"] = S, ["perf.gpu.static"] = S, ["perf.gpu.suspend"] = S,
        ["perf.fps.rtssMissing"] = W, ["perf.fps.rtssFound"] = W, ["perf.fps.background"] = W, ["perf.fps.applyRow"] = W,
        ["perf.fps.show"] = W, ["perf.fps.insetX"] = W, ["perf.fps.insetY"] = W, ["perf.fps.size"] = W,
        ["perf.fps.target"] = W, ["perf.fps.manualRow"] = W,   // RivaTuner Statistics Server

        // ── Tools › Stats overlay (S7: Linux/StatsWindow.cs + PreviewsStats.cs over the shared StatTrackerService; editors in
        // Linux/Settings/StatsEditors.cs) ──
        ["tools.stats.enabled"] = S, ["tools.stats.hideOnLostFocus"] = S, ["tools.stats.metrics"] = S, ["tools.stats.countNpc"] = S,
        ["tools.stats.characterTable"] = S, ["tools.stats.fontSize"] = S, ["tools.stats.opacity"] = S, ["tools.stats.background"] = S,
        ["tools.stats.textColor"] = S, ["tools.stats.csv"] = S, ["tools.stats.logFolder"] = S, ["tools.stats.noFolder"] = S,
        ["tools.stats.keepDays"] = S, ["tools.stats.keepZero"] = S,
        // ── Tools › EVE settings ──
        ["tools.eve.editor"] = S,   // S8: Linux/Settings/EveEditors.cs over the shared EveSettingsModel; EVE's settings found in the Proton prefix (EveLogs.FindEveDir)

        // ── App › General ──
        ["app.general.language"] = S,
        ["app.general.scale"] = S,            // LinuxLooks.Scale: the settings window's size (S4)
        ["app.general.startupSettings"] = S,  // Previews.Start (S3b)
        ["app.general.startWithWindows"] = W,
        ["app.general.keepAwake"] = S, ["app.general.downtime"] = S,   // LinuxKeepAwake (systemd-inhibit), Previews.FollowDowntime (S3b)
        ["app.general.tranquility"] = S, ["app.general.tranquilityWaiting"] = S,   // S7: Previews.FollowTranquility, the shared watch; no live check on either platform yet (P120)
        ["app.general.acceptCommands"] = S,
        ["app.general.allowPairingRequests"] = W, ["app.general.buttons"] = W,   // the Elgato Stream Deck plugin
        ["app.general.acceptCommandsUnelevated"] = W,                            // Windows elevation
        ["app.general.recent"] = S,

        // ── App › Look (S4): the twelve looks (Linux/Looks, from the Windows look files). Daylight while the desktop's colour
        //    scheme is light (the freedesktop portal, Avalonia's platform colour values) in Linux words; Windows' contrast
        //    themes have no Linux counterpart Avalonia reads, so following them stays Windows-only. ──
        ["app.look.gallery"] = S,
        ["app.look.followTheme"] = S, ["app.look.themeOn"] = S,
        ["app.look.followContrast"] = W, ["app.look.contrastOn"] = W,

        // ── App › Profiles ──
        ["app.profiles.current"] = S,         // Linux/Settings/ProfileEditors.cs over the shared ProfileActions (S3b)
        ["app.profiles.autoSelect"] = S, ["app.profiles.autoCharacters"] = S,   // Previews.FollowAutoProfile (S3b)
        ["app.profiles.folderText"] = S, ["app.profiles.backup"] = S,

        // ── App › About ──
        ["app.about.versionText"] = S, ["app.about.links"] = S, ["app.about.checkOnStartup"] = S, ["app.about.preRelease"] = S,
        ["app.about.installVersion"] = S,     // S8: Linux/Settings/UpdateEditors.cs over the Linux updater (UpdateDialog, UpdateService.Linux.cs)
        ["app.about.useOldWindow"] = W,       // the old WPF settings window
        ["app.about.fonts"] = S,              // Linux/Settings/LookEditors.cs: the carried typefaces and their licences (S4)
        ["app.about.uninstallRow"] = W,       // registry, shortcuts, the Windows install

        // ── App › Diagnostics ──
        ["app.diagnostics.errorCount"] = S, ["app.diagnostics.errorLog"] = S,
        ["app.diagnostics.uncleanExit"] = S,  // Previews.Start writes the run marker (S3b)
        ["app.diagnostics.bugReport"] = S,    // S8: Linux/BugReportWindow.cs + LinuxBugReport.cs over the shared BugReport (a Linux body, LinuxSupport.Runs)
        ["app.diagnostics.trackPlaytime"] = S, ["app.diagnostics.playtimeHistory"] = S,   // S8: Linux/PreviewsText.cs (the template's {week}); Clear empties the app's ledger
        ["app.diagnostics.note"] = S, ["app.diagnostics.cycling"] = S, ["app.diagnostics.alerts"] = S, ["app.diagnostics.folder"] = S,   // GlobalSettings set (S3b)
        ["app.diagnostics.windowHooks"] = S,  // S6: client windows found, closed and placed (fixed slots) go to the window events log
        ["app.diagnostics.injection"] = W, ["app.diagnostics.dwm"] = W,   // no key/click sending, no DWM on Linux

        // ── A character's page ──
        ["character.hotkey"] = S,
        ["character.cycle"] = S,              // MoreEditors.CharacterCycle, Previews._excludedFromCycle (S3b)
        ["character.groups"] = S, ["character.noGroup"] = S, ["character.groupsLink"] = S,
        ["character.show"] = S, ["character.label"] = S, ["character.labelColor"] = S, ["character.labelSize"] = S,
        ["character.size"] = S,               // MoreEditors.CharacterSizeBox (S3b)
        ["character.pip"] = S, ["character.pipOpacity"] = S,
        ["character.colorsOff"] = S, ["character.colorActive"] = S, ["character.colorInactive"] = S, ["character.colorText"] = S,
        ["character.colorsReset"] = S,
        ["character.neverMinimize"] = S,      // Previews.ActivateClient (S3b)
        ["character.core"] = W, ["character.coreStale"] = W,   // CPU priority and affinity: see perf.cpu
        ["character.volume"] = S,
        ["character.statsOff"] = S, ["character.statsNote"] = S, ["character.statsDamage"] = S, ["character.statsLogi"] = S,   // S7
        ["character.statsMining"] = S, ["character.statsRatting"] = S, ["character.statsPicker"] = S,
        ["character.cropCount"] = S, ["character.cropsLink"] = S, ["character.forgetRow"] = S,
    };

    /// <summary>Unclassified ids show (as before S3); the test makes each new one a decision.</summary>
    public static bool Shows(string itemId) => !Items.TryGetValue(itemId, out var support) || support == Support.Supported;

    // ── The pages as Linux shows them ──

    private static IReadOnlyList<PageDef>? _pages;

    /// <summary>SettingsRegistry.Pages without the cards Linux does not show, and without the sections and pages left empty.</summary>
    public static IReadOnlyList<PageDef> Pages => _pages ??= SettingsRegistry.Pages.Select(Filter).OfType<PageDef>().ToList();

    /// <summary>A page built at run time (a character's), filtered the same way.</summary>
    public static PageDef? Dynamic(string id) => SettingsRegistry.Dynamic(id) is { } page ? Filter(page) : null;

    /// <summary>The settings of the cards Linux shows (the hotkey clash check counts only keys Linux binds).</summary>
    public static IEnumerable<SettingDescriptor> Settings => Pages.SelectMany(p => p.Sections).SelectMany(s => s.Items).OfType<SettingItem>().Select(i => i.Setting);

    public static PageDef? Filter(PageDef page)
    {
        var sections = new List<SectionDef>();
        foreach (var section in page.Sections)
        {
            var items = section.Items.Where(i => Shows(i.Id)).Select(i => Linuxify(i, page.Id)).ToList();
            if (items.Count > 0 || Additions.ContainsKey(section.Id)) sections.Add(section with { Items = items });
        }
        return sections.Count == 0 ? null : page with { Sections = sections };
    }

    /// <summary>A help topic shows unless every card it explains is hidden.</summary>
    public static bool Shows(HelpTopic topic) => topic.SettingIds.Count == 0 || topic.SettingIds.Any(Shows);

    private static IReadOnlyList<HelpTopic>? _topics;

    /// <summary>The help topics Linux shows, each naming only the cards it shows (the search opens a topic at its first card,
    /// and the help pane follows the cards it names).</summary>
    public static IReadOnlyList<HelpTopic> Topics => _topics ??= HelpTopics.All.Where(Shows)
        .Select(t => t.SettingIds.All(Shows) ? t : t with { SettingIds = t.SettingIds.Where(Shows).ToList() }).ToList();

    // ── Linux wording ──

    /// <summary>Keys whose Windows text says something else on Linux: the alert hub is the desktop's notifications there,
    /// and other programs send commands to a socket (the old Linux window's own texts).</summary>
    private static readonly Dictionary<string, string> KeyOverrides = new(StringComparer.Ordinal)
    {
        ["L.S.Sec.alerts.hub.main"] = "L.Linux.Notifications",
        ["L.S.alerts.hub.enabled"] = "L.Linux.NotifyOn",
        ["L.S.alerts.hub.enabled.Desc"] = "L.Linux.NotifyOn.Desc",
        ["L.S.app.general.acceptCommands.Desc"] = "L.Linux.AcceptCommands.Desc",
        // S3b: the editors' Windows words (a volume "in Windows", the alert hub's toasts). S6: Linux has the layout undo now, so
        // "Apply to all" keeps its Windows description.
        ["L.S.clients.audio.volume.Desc"] = "L.Linux.VolumeDesc",
        ["L.S.Severity.ColHub"] = "L.Linux.Notifications",
        // S4: the looks change the settings window and its dialogs on Linux (not the menus and alerts), and Daylight follows
        // the desktop's colour scheme instead of Windows' app theme.
        ["L.S.app.look.gallery.Desc"] = "L.Linux.LookGallery.Desc",
        ["L.Help.app.look.look.Body"] = "L.Linux.LookHelp.Body",
        ["L.S.Sec.app.look.theme"] = "L.Linux.LookThemeSection",
        ["L.S.app.look.followTheme"] = "L.Linux.LookFollowTheme",
        ["L.S.app.look.followTheme.Desc"] = "L.Linux.LookFollowTheme.Desc",
        ["L.S.app.look.themeOn"] = "L.Linux.LookThemeOn",
        ["L.Help.app.look.theme.Title"] = "L.Linux.LookThemeHelp.Title",
        ["L.Help.app.look.theme.Body"] = "L.Linux.LookThemeHelp.Body",
        // S5: help topics whose Windows text names Windows things (the alert hub, Windows voices and sleep, Task Manager, the
        // Windows display language, Start with Windows, the left Ctrl key), in Linux words.
        ["L.Help.alerts.hub.Intro"] = "L.Linux.Help.HubIntro",
        ["L.Help.alerts.hub.hub.Title"] = "L.Linux.Notifications",
        ["L.Help.alerts.hub.hub.Body"] = "L.Linux.Help.Hub",
        ["L.Help.character.clients.Body"] = "L.Linux.Help.CharacterClients",
        ["L.Help.thumbs.text.clientState.Body"] = "L.Linux.Help.ClientState",
        ["L.Help.alerts.sounds.speech.Body"] = "L.Linux.Help.Speech",
        ["L.Help.app.general.keepAwake.Body"] = "L.Linux.Help.KeepAwake",
        ["L.Help.alerts.events.clientStops.Body"] = "L.Linux.Help.ClientStops",
        ["L.Help.app.general.language.Body"] = "L.Linux.Help.Language",
        ["L.Help.app.general.Intro"] = "L.Linux.Help.GeneralIntro",
        ["L.Help.app.general.startup.Title"] = "L.S.app.general.startupSettings",
        ["L.Help.app.general.startup.Body"] = "L.Linux.Help.Startup",
        ["L.Help.alerts.look.severity.Body"] = "L.Linux.Help.Severity",
        ["L.Help.clients.characters.clicks.Body"] = "L.Linux.Help.Clicks",
        // S7: Linux has no session report window (the Windows StatReportWindow), so the logging help leaves it out.
        ["L.Help.tools.stats.logging.Body"] = "L.Linux.Help.StatsLogging",
        // S8: the Linux updater has no failed-install marker (the Windows tray note within ten minutes), so that paragraph goes.
        ["L.Help.app.about.updates.Body"] = "L.Linux.Help.Updates",
        // S8: the bug report names the Linux kernel, distribution and desktop, and the user name is not a Windows one.
        ["L.Help.app.diagnostics.report.Body"] = "L.Linux.Help.Report",
        ["L.Report.Note"] = "L.Linux.Report.Note",
        // S8: EVE's settings live in the Proton prefix on Linux, not in %LOCALAPPDATA% (Tools › EVE settings).
        ["L.S.Eve.NotFound"] = "L.Linux.Eve.NotFound",
    };

    /// <summary>The key Linux shows for <paramref name="key"/>.</summary>
    public static string Key(string key) => KeyOverrides.GetValueOrDefault(key, key);

    // ── Linux-only cards ──

    public enum AdditionKind { Note, Commands, Import }

    /// <summary>A card of the old Linux window shown at the end of a shared section. TitleKey names it in the search (a note
    /// has none).</summary>
    public sealed record Addition(string Id, AdditionKind Kind, string TextKey, string? TitleKey = null);

    public static IReadOnlyDictionary<string, IReadOnlyList<Addition>> Additions { get; } = new Dictionary<string, IReadOnlyList<Addition>>(StringComparer.Ordinal)
    {
        ["alerts.events.logs"] = new[] { new Addition("linux.logFolders", AdditionKind.Note, "L.Linux.FolderNote") },
        ["thumbs.appearance.size"] = new[] { new Addition("linux.resize", AdditionKind.Note, "L.Linux.ResizeNote") },
        ["alerts.hub.push"] = new[] { new Addition("linux.pushAway", AdditionKind.Note, "L.Linux.PushAwayNote") },
        ["app.general.control"] = new[] { new Addition("linux.commands", AdditionKind.Commands, "L.Linux.CommandsNote", "L.Linux.CommandsTitle") },
        ["app.profiles.backups"] = new[] { new Addition("linux.import", AdditionKind.Import, "L.Linux.Import", "L.Linux.ImportTitle") },
    };

    /// <summary>The Linux-only cards with a title, for the search (on the page and section they are drawn in).</summary>
    public static IEnumerable<SearchEntry> SearchEntries(Func<string, string> resolve)
    {
        foreach (var page in Pages)
            foreach (var section in page.Sections)
                if (Additions.TryGetValue(section.Id, out var cards))
                    foreach (var card in cards.Where(c => c.TitleKey != null))
                        yield return new SearchEntry(SearchKind.Setting, resolve(card.TitleKey!), LocalizationService.EnglishText(card.TitleKey!),
                            resolve("L.S.Nav." + page.Group) + " › " + resolve(page.TitleKey), page.Id, card.Id);
    }

    // ── Linux bodies for shared buttons ──

    /// <summary>Buttons whose shared body does nothing on Linux (it opens Explorer): the folder or file opens with xdg-open.</summary>
    private static readonly Dictionary<string, Action<SettingsContext>> Runs = new(StringComparer.Ordinal)
    {
        ["app.profiles.openFolder"] = _ => OpenFolder(AppPaths.ConfigDir),
        ["app.diagnostics.openLogs"] = _ => OpenFolder(AppPaths.LogsDir),
        ["app.diagnostics.openErrorLog"] = _ =>
        {
            var path = ErrorLog.Shared.FilePath;
            if (File.Exists(path)) OpenWithDesktop(path);
            else if (Path.GetDirectoryName(path) is { Length: > 0 } dir) OpenFolder(dir);
        },
        // S8: PlaytimeActions.Clear empties what the running app holds through ThumbnailManager, which Linux has not: the Linux
        // app's ledger instead (else its next save would write the history back).
        // S8: the shared body opens the WPF report window; Linux's is Linux/BugReportWindow.cs over LinuxBugReport.Gather.
        ["app.diagnostics.createReport"] = ctx => (ctx.Host as SharedSettingsWindow)?.OpenBugReport(),
        ["app.diagnostics.clearPlaytime"] = ctx =>
        {
            if (App(ctx) is { } app) app.ClearPlaytime();
            else new PlaytimeStore(ctx.Service.ConfigDirectory).Delete();
            ctx.Host.Toast(SettingsContext.Str("L.S.Playtime.Cleared"));
        },
    };

    /// <summary>The Linux app behind the settings window a card runs in, or null (a test's host).</summary>
    private static ISettingsHost? App(SettingsContext ctx) => (ctx.Host as SharedSettingsWindow)?.App;

    private static PageItem Linuxify(PageItem item, string pageId) => item switch
    {
        ActionRow row when row.Actions.Any(a => Runs.ContainsKey(a.Id))
            => row with { Actions = row.Actions.Select(a => Runs.TryGetValue(a.Id, out var run) ? a with { Run = run } : a).ToList() },
        ActionRow row when row.Id == "character.forgetRow" && pageId.StartsWith(CharacterPage.IdFor(""), StringComparison.Ordinal)
            => row with { Actions = row.Actions.Select(a => a.Id == "character.forget" ? a with { Run = ctx => Forget(ctx, pageId[CharacterPage.IdFor("").Length..]) } : a).ToList() },
        _ => item,
    };

    /// <summary>S8: CharacterForget.ForgetInteractive with the Linux app in ThumbnailManager's place (keep in sync): asks first,
    /// purges every entry for the name (the shared Purge), takes it out of the running app's playtime ledger and jump count
    /// (else the next save writes it back), saves with a re-apply, says so and returns to Characters.</summary>
    private static void Forget(SettingsContext ctx, string name)
    {
        if (!ctx.Host.Confirm(SettingsContext.Str("L.S.Character.ForgetTitle"),
                string.Format(SettingsContext.Str("L.S.Character.ForgetConfirm"), name))) return;
        CharacterForget.Purge(ctx.S, name);
        bool playtimeGone = App(ctx) is { } app ? app.ForgetPlaytime(name) : PlaytimeActions.Forget(ctx, name);
        ctx.Applier.ApplyNow(ApplyScope.Thumbnails | ApplyScope.Crops);
        ctx.Host.Toast(string.Format(SettingsContext.Str(playtimeGone ? "L.S.Character.Forgotten" : "L.S.Character.ForgottenPlaytimeKept"), name));
        ctx.Host.Navigate(ClientsPages.CharactersId);
    }

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        OpenWithDesktop(dir);
    }

    /// <summary>The desktop's program for a file, folder or link, as the Linux update dialog opens its release page. The target is
    /// one argument. Its output is not redirected: xdg-open and the program it runs would write to pipes nobody reads.</summary>
    internal static void OpenWithDesktop(string target)
    {
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(target);
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }   // no xdg-open
    }
}
