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
        ["home.monitor"] = N,                 // PreferredMonitor: Linux places thumbnails without a chosen monitor
        ["home.alertAttack"] = S, ["home.alertScram"] = S, ["home.alertDecloak"] = S, ["home.alertFleet"] = S, ["home.alertSound"] = S,
        ["home.quickActions"] = S,            // Linux/Settings/MoreEditors.cs (S3b)
        ["home.startWithWindows"] = W,        // the Windows Run registry key

        // ── Clients › Characters ──
        ["clients.characters.table"] = S, ["clients.characters.customColors"] = S,
        ["clients.characters.ctrlClick"] = S, ["clients.characters.shiftClick"] = S,   // Previews.ClickThumbnail (S3b)

        // ── Clients › Hotkeys ──
        ["clients.hotkeys.scope"] = S,
        ["clients.hotkeys.wildcard"] = N,     // the X11 grabs take the exact modifiers
        ["clients.hotkeys.hideKeystrokes"] = W, ["clients.hotkeys.carryModifiers"] = W, ["clients.hotkeys.carryKeys"] = W,
        ["clients.hotkeys.hideHeld"] = W, ["clients.hotkeys.broadcastHud"] = W,          // low-level keyboard hook, SendInput
        ["clients.hotkeys.cycleNext"] = S, ["clients.hotkeys.cyclePrevious"] = S,
        ["clients.hotkeys.cycleWhileHeld"] = N, ["clients.hotkeys.cycleDelay"] = N,       // X11 key repeat is not followed
        ["clients.hotkeys.wrapSound"] = S, ["clients.hotkeys.wrapSoundFile"] = S,         // Previews.PlayWrapSound (S3b)
        ["clients.hotkeys.includeLogins"] = N,   // Linux makes no thumbnail for a client at the login screen
        ["clients.hotkeys.cycleSameSystem"] = S, ["clients.hotkeys.cycleAlerting"] = S, ["clients.hotkeys.cycleSkipMuted"] = S,   // CycleFilter (S3b)
        ["clients.hotkeys.backToPrevious"] = S, ["clients.hotkeys.lastAlert"] = S,
        ["clients.hotkeys.suspend"] = S, ["clients.hotkeys.clickThrough"] = S, ["clients.hotkeys.hidePrimary"] = S,   // Previews.BindHotkeys (S3b)
        ["clients.hotkeys.quickSwitch"] = N, ["clients.hotkeys.undo"] = N, ["clients.hotkeys.redo"] = N,   // no quick-switch sheet, no layout history
        ["clients.hotkeys.profileNext"] = S, ["clients.hotkeys.profilePrevious"] = S, ["clients.hotkeys.closeAll"] = S,   // (S3b)
        ["clients.hotkeys.sheet"] = N,        // no hotkey sheet window on Linux
        ["clients.hotkeys.openSettings"] = S, // (S3b)
        ["clients.hotkeys.hideAll"] = S, ["clients.hotkeys.hidePip"] = S, ["clients.hotkeys.hideCrops"] = S,
        ["clients.hotkeys.lockPositions"] = S, ["clients.hotkeys.commandPalette"] = S,
        ["clients.hotkeys.gamepad"] = W, ["clients.hotkeys.gamepadBindings"] = W,       // XInput
        ["clients.hotkeys.conflicts"] = N,    // no Linux editor (Linux shows clashes on each key)
        ["clients.hotkeys.refused"] = W,      // keys RegisterHotKey refused

        // ── Clients › Groups ──
        ["clients.groups.showBorders"] = S, ["clients.groups.editor"] = S, ["clients.groups.focusKey"] = S,
        ["clients.groups.focusGroup"] = S, ["clients.groups.focusAlerts"] = S,

        // ── Clients › Windows & positions: the Linux app minimizes the other clients (S3b), but does not move or size EVE's windows ──
        ["clients.windows.minimizeInactive"] = S, ["clients.windows.minimizeDelay"] = S, ["clients.windows.neverMinimize"] = S,   // Previews.ActivateClient
        ["clients.windows.positionMode"] = N, ["clients.windows.positionX"] = N, ["clients.windows.positionY"] = N,
        ["clients.windows.alwaysMaximize"] = N, ["clients.windows.trackPositions"] = N,
        ["clients.windows.coverTaskbar"] = W, // the Windows taskbar
        ["clients.windows.slotEditor"] = N, ["clients.windows.slotsResize"] = N,
        ["clients.windows.charSelectCycle"] = N, ["clients.windows.charSelectNext"] = N, ["clients.windows.charSelectPrevious"] = N,

        // ── Clients › Audio ──
        ["clients.audio.autoSolo"] = S, ["clients.audio.muteNoneActive"] = S,   // Previews.FollowAudioSolo (S3b)
        ["clients.audio.volume"] = S,         // MoreEditors.Volumes (S3b), pactl

        // ── Thumbnails › Appearance ──
        ["thumbs.appearance.resizeAll"] = S,  // MoreEditors.ResizeAll (S3b)
        ["thumbs.appearance.individualResize"] = S, ["thumbs.appearance.lockAspect"] = S, ["thumbs.appearance.minSizeOn"] = S,
        ["thumbs.appearance.minWidth"] = S, ["thumbs.appearance.minHeight"] = S, ["thumbs.appearance.opacity"] = S,
        ["thumbs.appearance.opacityOnHover"] = S, ["thumbs.appearance.hoverZoom"] = S, ["thumbs.appearance.hoverScale"] = S,
        ["thumbs.appearance.background"] = N,

        // ── Thumbnails › Text & borders ──
        ["thumbs.text.showText"] = S, ["thumbs.text.showSystem"] = S, ["thumbs.text.showTimer"] = S,   // Previews.Extras (S3b)
        ["thumbs.text.template"] = N,         // no Linux editor; {jumps} and {week} have no Linux source
        ["thumbs.text.streamer"] = S, ["thumbs.text.color"] = S, ["thumbs.text.font"] = S, ["thumbs.text.size"] = S,
        ["thumbs.text.marginX"] = S, ["thumbs.text.marginY"] = S,   // Previews.Extras (S3b)
        ["thumbs.text.showStats"] = N, ["thumbs.text.statsSize"] = N,   // no process stats on Linux
        ["thumbs.text.highlight"] = S,        // Previews.Look honours it (S3b), as ThumbnailManager.ApplyActiveBorders
        ["thumbs.text.highlightColor"] = S, ["thumbs.text.highlightWidth"] = S, ["thumbs.text.highlightInGroup"] = S,
        ["thumbs.text.inactiveColor"] = S, ["thumbs.text.inactiveWidth"] = S,
        ["thumbs.text.cornerRadius"] = S, ["thumbs.text.autoBorders"] = S, ["thumbs.text.groupBorders"] = S,   // corner radius: Previews.Extras (S3b)
        ["thumbs.text.badgePosition"] = S,    // ThumbnailWindow.SetCycleBadge (S3b)
        ["thumbs.text.notResponding"] = S, ["thumbs.text.disconnected"] = S, ["thumbs.text.disconnectSeconds"] = S,

        // ── Thumbnails › Layout ──
        ["thumbs.layout.monitor"] = N,
        ["thumbs.layout.identifyRow"] = W,    // MonitorIdentify: a badge window per Windows monitor
        ["thumbs.layout.startX"] = S, ["thumbs.layout.startY"] = S, ["thumbs.layout.startWidth"] = S, ["thumbs.layout.startHeight"] = S,
        ["thumbs.layout.gap"] = S,            // Previews.Place, ThumbnailManager.FlowSlot (S3b)
        ["thumbs.layout.snap"] = N, ["thumbs.layout.snapDistance"] = N, ["thumbs.layout.confine"] = N,
        ["thumbs.layout.lock"] = S, ["thumbs.layout.autoOnMonitorChange"] = N, ["thumbs.layout.presets"] = N,
        ["thumbs.layout.align"] = N, ["thumbs.layout.history"] = N,

        // ── Thumbnails › Visibility ──
        ["thumbs.visibility.alwaysOnTop"] = S, ["thumbs.visibility.aboveClients"] = N,
        ["thumbs.visibility.hideOnLostFocus"] = S,   // Previews.Tick (S3b)
        ["thumbs.visibility.hideActive"] = S, ["thumbs.nameBar"] = S,
        ["thumbs.visibility.notLoggedIn"] = N, ["thumbs.visibility.notLoggedInColor"] = N,
        ["thumbs.visibility.pipTable"] = S,   // MoreEditors.PipTable (S3b)

        // ── Thumbnails › Crop windows ──
        ["thumbs.crops.enabled"] = S, ["thumbs.crops.off"] = S, ["thumbs.crops.editor"] = S,

        // ── Alerts › Events ──
        ["alerts.events.chatLog"] = S, ["alerts.events.chatFolder"] = S, ["alerts.events.gameLog"] = S, ["alerts.events.gameFolder"] = S,
        ["alerts.events.pve"] = S,            // Linux/Alerts.cs ApplyMonitorOptions (S3)
        ["alerts.events.underFire"] = N, ["alerts.events.underFireTime"] = N,
        ["alerts.events.repeatWait"] = S, ["alerts.events.repeatBySeverity"] = S,
        ["alerts.events.table"] = S, ["alerts.events.ewarNote"] = S, ["alerts.events.stragglerWait"] = S,
        ["alerts.events.dpsThreshold"] = S, ["alerts.events.dpsSeconds"] = S, ["alerts.events.repsSeconds"] = S, ["alerts.events.combatNote"] = S,
        ["alerts.events.memoryLimit"] = S, ["alerts.events.memoryGrowth"] = S, ["alerts.events.memoryMinutes"] = S,
        ["alerts.events.clientNote"] = S, ["alerts.events.idleMinutes"] = S,
        ["alerts.events.groups"] = N,         // read, but no Linux editor
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
        ["alerts.sounds.escalate"] = N, ["alerts.sounds.escalateAfter"] = N, ["alerts.sounds.escalateRepeats"] = N,
        ["alerts.sounds.speak"] = S, ["alerts.sounds.speakFrom"] = S, ["alerts.sounds.speechVolume"] = S, ["alerts.sounds.voices"] = S,
        ["alerts.sounds.table"] = S,          // MoreEditors.SoundTable; Linux/Alerts.cs keeps the per-character Wait (S3b)
        ["alerts.sounds.groups"] = N,

        // ── Alerts › Alert hub: on Linux the hub is the desktop's notifications (notify-send) ──
        ["alerts.hub.enabled"] = S, ["alerts.hub.skipActive"] = S,
        ["alerts.hub.duration"] = N, ["alerts.hub.direction"] = N, ["alerts.hub.autoHide"] = N, ["alerts.hub.autoHideAfter"] = N,
        ["alerts.hub.positionRow"] = N,       // the hub window's place
        ["alerts.hub.notify"] = W, ["alerts.hub.notifyFrom"] = W, ["alerts.hub.notifyTest"] = W,   // Windows notifications
        ["alerts.hub.push"] = S, ["alerts.hub.pushFrom"] = S, ["alerts.hub.pushMention"] = S, ["alerts.hub.pushAway"] = S,
        ["alerts.hub.pushWebhook"] = S, ["alerts.hub.historyRow"] = S, ["alerts.hub.keepHistory"] = S,
        ["alerts.hub.historyDays"] = S, ["alerts.hub.historyFolder"] = S,

        // ── Alerts › Custom alerts: the Linux app does not load the player's rules yet ──
        ["alerts.custom.editor"] = N, ["alerts.custom.file"] = N,

        // ── Performance ──
        ["perf.cpu.manage"] = N, ["perf.cpu.autoBalance"] = N, ["perf.cpu.coreTable"] = N,
        ["perf.gpu.presetRow"] = N,           // sets two switches the Linux app does not read
        ["perf.gpu.static"] = N, ["perf.gpu.suspend"] = N,
        ["perf.fps.rtssMissing"] = W, ["perf.fps.rtssFound"] = W, ["perf.fps.background"] = W, ["perf.fps.applyRow"] = W,
        ["perf.fps.show"] = W, ["perf.fps.insetX"] = W, ["perf.fps.insetY"] = W, ["perf.fps.size"] = W,
        ["perf.fps.target"] = W, ["perf.fps.manualRow"] = W,   // RivaTuner Statistics Server

        // ── Tools › Stats overlay: not on Linux yet ──
        ["tools.stats.enabled"] = N, ["tools.stats.hideOnLostFocus"] = N, ["tools.stats.metrics"] = N, ["tools.stats.countNpc"] = N,
        ["tools.stats.characterTable"] = N, ["tools.stats.fontSize"] = N, ["tools.stats.opacity"] = N, ["tools.stats.background"] = N,
        ["tools.stats.textColor"] = N, ["tools.stats.csv"] = N, ["tools.stats.logFolder"] = N, ["tools.stats.noFolder"] = N,
        ["tools.stats.keepDays"] = N, ["tools.stats.keepZero"] = N,
        // ── Tools › EVE settings ──
        ["tools.eve.editor"] = N,

        // ── App › General ──
        ["app.general.language"] = S,
        ["app.general.scale"] = N,            // S4: the settings window's text size
        ["app.general.startupSettings"] = S,  // Previews.Start (S3b)
        ["app.general.startWithWindows"] = W,
        ["app.general.keepAwake"] = S, ["app.general.downtime"] = S,   // LinuxKeepAwake (systemd-inhibit), Previews.FollowDowntime (S3b)
        ["app.general.tranquility"] = N, ["app.general.tranquilityWaiting"] = N,   // the status check is not wired on either platform yet
        ["app.general.acceptCommands"] = S,
        ["app.general.allowPairingRequests"] = W, ["app.general.buttons"] = W,   // the Elgato Stream Deck plugin
        ["app.general.acceptCommandsUnelevated"] = W,                            // Windows elevation
        ["app.general.recent"] = S,

        // ── App › Look: S4 brings the looks; following Windows is Windows-only ──
        ["app.look.gallery"] = N,
        ["app.look.followTheme"] = W, ["app.look.themeOn"] = W, ["app.look.followContrast"] = W, ["app.look.contrastOn"] = W,

        // ── App › Profiles ──
        ["app.profiles.current"] = S,         // Linux/Settings/ProfileEditors.cs over the shared ProfileActions (S3b)
        ["app.profiles.autoSelect"] = S, ["app.profiles.autoCharacters"] = S,   // Previews.FollowAutoProfile (S3b)
        ["app.profiles.folderText"] = S, ["app.profiles.backup"] = S,

        // ── App › About ──
        ["app.about.versionText"] = S, ["app.about.links"] = S, ["app.about.checkOnStartup"] = S, ["app.about.preRelease"] = S,
        ["app.about.installVersion"] = N,     // no Linux editor
        ["app.about.useOldWindow"] = W,       // the old WPF settings window
        ["app.about.fonts"] = N,
        ["app.about.uninstallRow"] = W,       // registry, shortcuts, the Windows install

        // ── App › Diagnostics ──
        ["app.diagnostics.errorCount"] = S, ["app.diagnostics.errorLog"] = S,
        ["app.diagnostics.uncleanExit"] = N,  // the Linux app writes no run marker
        ["app.diagnostics.bugReport"] = N,    // the report window is WPF
        ["app.diagnostics.trackPlaytime"] = N, ["app.diagnostics.playtimeHistory"] = N,
        ["app.diagnostics.note"] = S, ["app.diagnostics.cycling"] = S, ["app.diagnostics.alerts"] = S, ["app.diagnostics.folder"] = S,   // GlobalSettings set (S3b)
        ["app.diagnostics.injection"] = W, ["app.diagnostics.windowHooks"] = W, ["app.diagnostics.dwm"] = W,   // the Linux app writes no debug logs

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
        ["character.core"] = N, ["character.coreStale"] = N,   // no CPU affinity on Linux yet
        ["character.volume"] = S,
        ["character.statsOff"] = N, ["character.statsNote"] = N, ["character.statsDamage"] = N, ["character.statsLogi"] = N,
        ["character.statsMining"] = N, ["character.statsRatting"] = N, ["character.statsPicker"] = N,
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
            var items = section.Items.Where(i => Shows(i.Id)).Select(Linuxify).ToList();
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
        // S3b: the editors' Windows words (a volume "in Windows", an undo Linux has not, the alert hub's toasts).
        ["L.S.clients.audio.volume.Desc"] = "L.Linux.VolumeDesc",
        ["L.S.thumbs.appearance.resizeAll.Desc"] = "L.Linux.ResizeAllDesc",
        ["L.S.Severity.ColHub"] = "L.Linux.Notifications",
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
            if (File.Exists(path)) XdgOpen(path);
            else if (Path.GetDirectoryName(path) is { Length: > 0 } dir) OpenFolder(dir);
        },
    };

    private static PageItem Linuxify(PageItem item) =>
        item is ActionRow row && row.Actions.Any(a => Runs.ContainsKey(a.Id))
            ? row with { Actions = row.Actions.Select(a => Runs.TryGetValue(a.Id, out var run) ? a with { Run = run } : a).ToList() }
            : item;

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        XdgOpen(dir);
    }

    /// <summary>The desktop's program for a file or folder, as the Linux update dialog opens its release page. The path is one argument.</summary>
    private static void XdgOpen(string path)
    {
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }   // no xdg-open
    }
}
