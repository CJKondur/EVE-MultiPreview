using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EveMultiPreview.Models;

/// <summary>
/// Root settings model — compatible with the AHK JSON format.
/// Maps to "EVE MultiPreview.json".
/// Property names match AHK Propertys.ahk exactly for cross-format compatibility.
/// </summary>
public class AppSettings
{
    // ── Layout ──────────────────────────────────────────────────────
    public ThumbnailRect ThumbnailStartLocation { get; set; } = new() { X = 20, Y = 20, Width = 280, Height = 180 };
    public ThumbnailSize ThumbnailMinimumSize { get; set; } = new() { Width = 50, Height = 50 };
    // The minimum size is the drag-resize floor only once this is on (IDEAS P26). Files store 50 x 50, EVE-O imports
    // 192 x 108 and old AHK files 100 x 60 without ever having had an effect, so off keeps today's fixed 80 x 50.
    public bool ThumbnailMinimumSizeEnabled { get; set; } = false;
    public bool ThumbnailSnap { get; set; } = true;
    public int ThumbnailSnapDistance { get; set; } = 20;
    // The gap between thumbnails MultiPreview places itself: new clients with no saved place and Reset positions
    // (ThumbnailManager.FlowSlot, IDEAS P27). Drag snapping is flush.
    public int ThumbnailGutter { get; set; } = 8;
    // When on, dragging a thumbnail keeps it on the monitor it started on (Ctrl is
    // already taken by drag-all, so this is a setting rather than a modifier).
    public bool ConfineDragsToMonitor { get; set; } = false;
    public bool IndividualThumbnailResize { get; set; } = false;
    /// <summary>Keep the dragged thumbnail's own width-to-height ratio while the mouse resizes it (IDEAS P7).
    /// New key: off in a file without it.</summary>
    public bool LockResizeAspect { get; set; } = false;
    /// <summary>When the monitors change, apply the layout saved for the arrangement that is there now (IDEAS #10). Read by
    /// ThumbnailManager's MonitorLayoutFollower. New key: off in a file without it.</summary>
    public bool AutoLayoutOnMonitorChange { get; set; } = false;
    /// <summary>Streamer mode (IDEAS #19): character names show as "Client N" and labels and system names are hidden
    /// wherever MultiPreview draws them (StreamerMask). New key: off in a file without it.</summary>
    public bool StreamerMode { get; set; } = false;
    /// <summary>Thumbnail text template (IDEAS P2): "{name} · {system} · {timer} · {label}" and the like; empty = the name,
    /// label, system and timer as separate lines, as always. At most TextTemplate.MaxLength characters (a longer one is
    /// cut when a file is read). New key: empty in a file without it.</summary>
    public string ThumbnailTextTemplate { get; set; } = "";
    public bool LockPositions { get; set; } = false;
    public int PreferredMonitor { get; set; } = 1;
    public string ThumbnailBackgroundColor { get; set; } = "#57504E";
    public bool ColorBlindMode { get; set; } = false;
    /// <summary>The colours turning colour-blind mode on replaced, so turning it off gives them back (IDEAS P29). JSON text
    /// written by ColorBlindPalette; "" when nothing is kept (off, or turned on before this key existed).</summary>
    public string ColorBlindSavedColors { get; set; } = "";

    // ── Behavior ────────────────────────────────────────────────────
    public bool HideActiveThumbnail { get; set; } = false;
    public int MinimizeDelay { get; set; } = 100;

    /// <summary>Minimum interval, in milliseconds, between successive cycle-hotkey
    /// fires while a cycle key/button is held. Acts as the keyboard hotkey
    /// repeat throttle, the mouse-button auto-repeat interval, and the floor
    /// inside CycleGroup. Lower = faster cycling, higher = slower. Default 100ms.</summary>
    public int CycleDelayMs { get; set; } = 100;

    /// <summary>When true (default), holding a cycle hotkey auto-repeats the cycle
    /// at the CycleDelayMs cadence. When false, each press cycles exactly one
    /// client no matter how long the key/button is held — one cycle per push
    /// (issue #59).</summary>
    public bool CycleWhileHeld { get; set; } = true;
    public bool EnableKeyBlockGuard { get; set; } = false;
    public bool GlobalHotkeys { get; set; } = true;

    // Hotkeys wildcard-expand over all 16 modifier combinations, so a bare "V"
    // binding also claims Ctrl+V / Shift+V / Win+V and paste never reaches EVE
    // (#105). ON (default) keeps that AHK-style behaviour, which EVE players rely
    // on - Ctrl is held to lock targets, Shift to add, and cycling must still work.
    // OFF registers the exact combination only, so Ctrl+V passes through.
    public bool HotkeyWildcardModifiers { get; set; } = true;

    // Carry a held Ctrl/Shift/Alt into the client you switch to (#108). ON is what
    // makes hold-Ctrl-and-lock work across a cycle on most setups. On some it does not
    // help AND the synthetic key events appear to interfere with a modifier the client
    // can already see. OFF by default since 2.3.42 (opt-in): the physically-held key
    // is left alone, as before 2.3.30.
    public bool PropagateHeldModifiers { get; set; } = false;

    // Carry held NON-modifier keys (letters, digits, F-keys, Enter, Space, mouse
    // buttons) into the client you switch to (#112): hold F1 and cycle to fire it on
    // each client. OFF by default since 2.3.42 (opt-in): a module key tapped just
    // before a switch would otherwise fire on the next client, and the player's own
    // press then toggles it back off.
    public bool PropagateHeldKeys { get; set; } = false;

    // Hide a key still held across a switch from the client you land on (#115): its
    // release otherwise counts there as a tap - held A (align) made the next client
    // align to its selected item. Ordinary keys only; overrides PropagateHeldKeys.
    public bool HideHeldKeysOnSwitch { get; set; } = false;

    // Hide a hotkey's whole keystroke from the clients (#108). RegisterHotKey swallows
    // the key-down but lets the key-UP through to whatever is foreground - after a
    // client-switching hotkey that is the client we just switched INTO. A low-level
    // hook hides both halves, as AutoHotkey (EVE-X-Preview) does. Off = hook not
    // installed; RegisterHotKey alone, exactly the pre-2.3.38 behaviour.
    public bool HideHotkeyKeystrokes { get; set; } = true;

    // What Ctrl+click / Shift+click on a thumbnail do (#115): 0 = switch to the client
    // like a plain click, 1 = minimize it, 2 = toggle cycle exclusion. Defaults keep the
    // EVE-X-style behaviour; Ctrl is also EVE's lock-target key, hence the choice.
    public int CtrlClickAction { get; set; } = 1;
    public int ShiftClickAction { get; set; } = 2;
    public string SuspendHotkey { get; set; } = "";
    public string ClickThroughHotkey { get; set; } = "";
    public string HideShowThumbnailsHotkey { get; set; } = "";
    public string HidePrimaryHotkey { get; set; } = "";
    public string HideSecondaryHotkey { get; set; } = "";

    /// <summary>Dedicated keybind to hide/show all crop popups (issue #66).</summary>
    public string HideShowCropsHotkey { get; set; } = "";
    public string ProfileCycleForwardHotkey { get; set; } = "";
    public string ProfileCycleBackwardHotkey { get; set; } = "";
    public string LockPositionsHotkey { get; set; } = "";
    public string GlobalCycleForwardHotkey { get; set; } = "";
    public string GlobalCycleBackwardHotkey { get; set; } = "";
    // Optional global layout undo/redo hotkeys (empty = unbound; pick non-EVE keys).
    public string UndoLayoutHotkey { get; set; } = "";
    public string RedoLayoutHotkey { get; set; } = "";
    // Ends every tracked EVE client process after one confirmation (#115).
    public string CloseAllClientsHotkey { get; set; } = "";
    /// <summary>Back to the client used before this one (IDEAS #56). A new key: unbound in a file without it.</summary>
    public string PreviousClientHotkey { get; set; } = "";
    /// <summary>To the client behind the most severe alert not looked at yet (IDEAS #17). A new key: unbound in a
    /// file without it.</summary>
    public string GoToLastAlertHotkey { get; set; } = "";
    public bool HideStatsOnLostFocus { get; set; } = false;
    public bool IncludeLoginScreensInCycle { get; set; } = false;
    /// <summary>Cycle filters (IDEAS #40), read by ThumbnailManager at each cycle press. New keys, all off: a file
    /// without them cycles as it always did.</summary>
    public bool CycleOnlySameSystem { get; set; } = false;
    public bool CycleOnlyAlerting { get; set; } = false;
    public bool CycleSkipMuted { get; set; } = false;
    public bool ShowSessionTimer { get; set; } = false;
    public bool ShowSystemName { get; set; } = true;
    public bool SimpleMode { get; set; } = false;

    /// <summary>Open the old settings window instead of the new one (settings UI rework, spec §12). False: the new
    /// window is the default (Plan 3d); About › "Use the old settings window" sets it for a release or two.</summary>
    public bool UseLegacySettingsWindow { get; set; } = false;

    /// <summary>The new settings window's help pane is open (spec §9: closed until ? is pressed).</summary>
    public bool SettingsHelpOpen { get; set; } = false;

    /// <summary>The look of the app's windows (UI styles spec §10): the id of a look ("windows11", "classic"). App shows it
    /// at start and after every settings change; App settings › Look writes it. A missing, empty or unknown id shows the
    /// default look (UiStyles.Find), and an unknown one is kept in the file until the player picks (owner decision O24).</summary>
    public string UiStyle { get; set; } = DefaultUiStyle;

    /// <summary>The look of a new file and of a file without the key: Classic, the old window's navy (spec §10).</summary>
    public const string DefaultUiStyle = "classic";

    /// <summary>High contrast while a Windows contrast theme is on (UI styles spec §12). Its switch and its rule come in
    /// plan S4; the key is in the file from S2, on.</summary>
    public bool UiStyleFollowContrast { get; set; } = true;
    public bool SetupCompleted { get; set; } = false;

    /// <summary>Controls whether the Settings window auto-opens when the app launches.</summary>
    public StartupSettingsMode StartupSettings { get; set; } = StartupSettingsMode.Off;

    // ── Debug Logging ───────────────────────────────────────────────
    public bool EnableDebugLogging_Injection { get; set; } = false;
    public bool EnableDebugLogging_Cycling { get; set; } = false;
    public bool EnableDebugLogging_WindowHooks { get; set; } = false;
    public bool EnableDebugLogging_DWM { get; set; } = false;
    public bool EnableDebugLogging_Alerts { get; set; } = false;

    // ── Alert Settings ──────────────────────────────────────────────

    public bool PveMode { get; set; } = false;
    public bool EnableAlertSounds { get; set; } = false;
    public int AlertSoundVolume { get; set; } = 100;

    /// <summary>Do-not-disturb (IDEAS #32): alert sounds are muted while it is on; borders, badges and toasts go on.
    /// The hotkey flips it, the quiet hours turn it on by the PC's clock every day (a span over midnight works), and
    /// a critical alert still plays unless DoNotDisturbCriticalPlays is off. New keys: a file without them has no
    /// hotkey and no quiet hours.</summary>
    public string DoNotDisturbHotkey { get; set; } = "";
    public bool QuietHoursEnabled { get; set; } = false;
    public string QuietHoursFrom { get; set; } = "23:00";
    public string QuietHoursTo { get; set; } = "07:00";
    public bool DoNotDisturbCriticalPlays { get; set; } = true;

    /// <summary>Escalation (IDEAS #29): a critical alert whose client has not come to the front has its sound played
    /// again after EscalationAfterSeconds (5 to 600), louder each time, EscalationRepeats times (1 to 10). New keys:
    /// off in a file without them.</summary>
    public bool EscalationEnabled { get; set; } = false;
    public int EscalationAfterSeconds { get; set; } = 20;
    public int EscalationRepeats { get; set; } = 3;

    /// <summary>"Left behind" (IDEAS #5): how many seconds a group's jump wave must have been still before the members
    /// that did not jump are flagged (5 to 300). The event itself is switched on in EnabledAlertTypes under the key
    /// "straggler", and a file without that key has it off. New key: 10 in a file without it.</summary>
    public int StragglerWaitSeconds { get; set; } = 10;

    /// <summary>Spoken alerts (IDEAS #26), read by SpokenWay for every alert that was shown: on or off; from which
    /// severity (0 critical only, 1 critical and warning, 2 all); the voice's volume (0 to 100); and the voice the
    /// player chose for a severity, as one line of JSON text ("" = Automatic for all three; SpeechVoiceChoice reads
    /// it). New keys: a file without them has spoken alerts off.</summary>
    public bool SpeakAlerts { get; set; } = false;
    public int SpeakAlertsFrom { get; set; } = 0;
    public int SpeechVolume { get; set; } = 100;
    public string SpeechVoices { get; set; } = "";

    /// <summary>Windows notifications (IDEAS #33), read by NotificationsWay for every alert that was shown, and by App
    /// at startup and after every settings change (the registry key follows the switch): on or off, and from which
    /// severity (0 critical only, 1 critical and warning, 2 all). New keys: off in a file without them.</summary>
    public bool WindowsNotifications { get; set; } = false;
    public int WindowsNotificationsFrom { get; set; } = 0;

    /// <summary>Enlarging a thumbnail on an alert (IDEAS #13), read by AlertEnlarge at every alert that was shown and
    /// at its tick: on or off; from which severity (0 critical only, 1 critical and warning, 2 all); the size in
    /// percent of the thumbnail's own (110 to 400); and for how many seconds (1 to 60). New keys: off in a file
    /// without them.</summary>
    public bool EnlargeOnAlert { get; set; } = false;
    public int EnlargeOnAlertFrom { get; set; } = 0;
    public int EnlargeOnAlertPercent { get; set; } = 200;
    public int EnlargeOnAlertSeconds { get; set; } = 5;

    /// <summary>Alerts per group (IDEAS #31): which events a group of Clients › Groups switches on or off for its
    /// members, as one line of JSON text ({"Miners":{"attack":false}}; GroupAlertChoices reads it). An event a group
    /// says nothing about, and a character in no group, has the switch for everyone (EnabledAlertTypes). Read by App,
    /// which hands AlertProfiles its picture and the log monitor its table. New key: "" in a file without it, which
    /// is "no group has a choice".
    /// <para>In the settings file the key holds that text as ONE JSON STRING, with the text's own quotes escaped, as
    /// every key of the file is one line. A player who writes the choices by hand as what they are, a JSON object
    /// ("GroupAlertProfiles": {"Miners":{"attack":false}}), is accepted: the object is read as its text, and the
    /// next save writes the string again. A number, true, an array or null behind the key is "no choices" and does
    /// not fail the file; an object that is never closed fails the whole file, as any broken JSON does. This key
    /// alone reads so (GroupAlertChoicesTextConverter): for every other key a value of the wrong kind still fails
    /// the whole file.</para></summary>
    [JsonConverter(typeof(EveMultiPreview.Converters.GroupAlertChoicesTextConverter))]
    public string GroupAlertProfiles { get; set; } = "";

    /// <summary>Sounds per group (IDEAS #58): the sound file a group of Clients › Groups plays for its members' alerts,
    /// as one line of JSON text ({"Miners":{"*":"C:\\...\\ding.wav","attack":"C:\\...\\hit.wav"}}; GroupSoundChoices reads it).
    /// "*" is the group's sound for every event; an event's own key is the sound for that event alone. Read by App,
    /// which hands GroupSounds its picture. New key: "" in a file without it, which is "no group has a sound". Kept
    /// as one JSON string in the file, and read from an object written by hand, as GroupAlertProfiles is.</summary>
    [JsonConverter(typeof(EveMultiPreview.Converters.GroupAlertChoicesTextConverter))]
    public string GroupAlertSounds { get; set; } = "";

    /// <summary>"Incoming DPS high" (IDEAS #27): the damage per second, averaged over DpsAlertSeconds, at which the event
    /// is raised (1 to 100,000). The event itself is switched on in EnabledAlertTypes under "dps_high", and a file
    /// without that key has it off. Read by the log monitor (CombatOptions.Of). New key: 400 in a file without it.</summary>
    public int DpsAlertThreshold { get; set; } = 400;

    /// <summary>"Incoming DPS high" (IDEAS #27): how many seconds a character must have been under fire, with no pause
    /// longer than this, and the window the damage is averaged over (3 to 120). New key: 10 in a file without it.</summary>
    public int DpsAlertSeconds { get; set; } = 10;

    /// <summary>"Reps stopped" (IDEAS #28): how many seconds after the last remote repair a hit raises the event
    /// (3 to 120). The event itself is switched on in EnabledAlertTypes under "reps_stopped", and a file without that
    /// key has it off. New key: 10 in a file without it.</summary>
    public int RepsStoppedSeconds { get; set; } = 10;

    /// <summary>Alerts to a Discord webhook, which the Discord app on a phone shows (IDEAS #2), read by PushWay for every
    /// alert that got past the gates: on or off, from which severity (0 critical only, 1 critical and warning, 2 all),
    /// and whether only while Windows saw no input for PushWay.AwayAfter. PushDiscordWebhook is WebhookSecret's kept
    /// text (DPAPI for this Windows user, "dpapi1:" + base64), never a link in plain; an export writes it empty. New
    /// keys: off, critical only, only while away, no link in a file without them.</summary>
    public bool PushAlerts { get; set; } = false;
    public int PushAlertsFrom { get; set; } = 0;
    public bool PushOnlyWhenAway { get; set; } = true;
    public string PushDiscordWebhook { get; set; } = "";

    /// <summary>Keep the PC awake while EVE runs (IDEAS #46): App's KeepAwake holds Windows from sleeping while a
    /// character is logged in; the display may still turn off. New key: off in a file without it.</summary>
    public bool KeepPcAwake { get; set; } = false;

    /// <summary>"Client memory high" (IDEAS #45): the working set in MB at which a client is told once (512 to 65,536),
    /// how far above its lowest per-minute sample it may grow (128 to 32,768 MB) and the time that is measured over
    /// (15 to 720 minutes). The event itself is switched on in EnabledAlertTypes under "client_memory", and a file
    /// without that key has it off. Read by App (MemoryOptions.Of). New keys: 6144, 1024 and 120 in a file without them.</summary>
    public int MemoryAlertMb { get; set; } = 6144;
    public int MemoryGrowthMb { get; set; } = 1024;
    public int MemoryGrowthMinutes { get; set; } = 120;

    /// <summary>The hotkey that opens the hotkey cheat sheet over the thumbnails, and closes it again (IDEAS #16). Read by
    /// HotkeyService.RegisterFromSettings and by the tray menu's hint. New key: unbound in a file without it.</summary>
    public string HotkeySheetHotkey { get; set; } = "";

    /// <summary>The tray menu's countdown line to the daily downtime (11:00 UTC) and App's tray balloons at 15 and 5 minutes
    /// before it, naming the characters that are logged in (IDEAS #9). Read by TrayMenu and by App. New key: off in a
    /// file without it.</summary>
    public bool DowntimeWarnings { get; set; } = false;

    /// <summary>Show "Not responding" on the thumbnail of a client whose window Windows reports as hung (IDEAS #22). Read by
    /// App through ClientWatchOptions.Of at each look of the process monitor. New key: off in a file without it.</summary>
    public bool ClientNotRespondingBadge { get; set; } = false;

    /// <summary>Show "Disconnected" on the thumbnail of a logged-in client that has had no connection to EVE's server for
    /// DisconnectedSeconds (IDEAS #47). Read by App through ClientWatchOptions.Of. New key: off in a file without it.</summary>
    public bool ClientDisconnectedBadge { get; set; } = false;

    /// <summary>Seconds without a server connection before a client is marked Disconnected (IDEAS #47). Read by App through
    /// ClientWatchOptions.Of, which holds it to 20-600. New key: 60 in a file without it.</summary>
    public int DisconnectedSeconds { get; set; } = 60;

    /// <summary>Minutes a character that was working may go without fighting, mining or repairing before "Character idle"
    /// is raised (IDEAS #7). The event itself is switched on in EnabledAlertTypes under "char_idle", off in a file without
    /// it. Read by App through IdleWatch.Minutes, which holds it to 2-120. New key: 5 in a file without it.</summary>
    public int IdleAlertMinutes { get; set; } = 5;

    /// <summary>Focus mode (IDEAS #35): its hotkey (unbound until the player sets one), the group it shows ("" = the group
    /// of the client you are in, else a group of Clients › Groups by its exact name) and whether a client outside the group
    /// with an unseen alert still shows. The key is read by HotkeyService.RegisterFromSettings; the other two by App through
    /// FocusMode.Options while focus mode is on. New keys: unbound, "" and on in a file without them.</summary>
    public string FocusModeHotkey { get; set; } = "";
    public string FocusModeGroup { get; set; } = "";
    public bool FocusModeShowAlerts { get; set; } = true;

    /// <summary>Switch profiles by who is logged in (IDEAS #11): the profile whose characters (Profile.AutoSelectCharacters)
    /// are logged in, 10 s after the last login or logout. Read by App through AutoProfile.IsOn on its half-second timer.
    /// New key: off in a file without it.</summary>
    public bool AutoSelectProfile { get; set; } = false;

    /// <summary>Take commands from other programs (IDEAS #39): while on, MultiPreview listens on a named pipe only this
    /// Windows account can open, for one command of RemoteCommand's allow-list. Read by App at startup and after every
    /// settings change (FollowCommandServer). New key: off in a file without it.</summary>
    public bool AcceptCommands { get; set; } = false;

    /// <summary>Also take commands from programs of this account that are not run as administrator (IDEAS #15). Only
    /// matters while MultiPreview itself runs as administrator: its pipe then also lets in this account (CommandPipe.Security
    /// with alsoUser), which an elevated pipe otherwise keeps out. Read by App's FollowCommandServer, which makes a new
    /// server when it changes. New key: off in a file without it.</summary>
    public bool AcceptCommandsUnelevated { get; set; } = false;

    /// <summary>The hotkey that opens the settings window, or brings it forward when it is open (IDEAS P5). Read by
    /// HotkeyService.RegisterFromSettings, which calls the action it is given (App.OpenSettings). New key: unbound in a
    /// file without it.</summary>
    public string OpenSettingsHotkey { get; set; } = "";

    /// <summary>The hotkey that opens the type-to-switch command palette, or closes it when it is open (IDEAS #14). Read by
    /// HotkeyService.RegisterFromSettings, which raises CommandPaletteRequested (App shows the palette). New key: unbound in
    /// a file without it.</summary>
    public string CommandPaletteHotkey { get; set; } = "";

    /// <summary>Count each character's time on screen per day, for the thumbnail text's {week} (IDEAS P11). Off by default: it
    /// keeps character names in playtime.json in the settings folder. Read by ThumbnailManager's one-second tick; with it off
    /// nothing is read or written. New key: off in a file without it.</summary>
    public bool TrackPlaytime { get; set; } = false;

    /// <summary>Opacity (0-100%) applied to the alert pulse border color across
    /// all alert events. Default 100 = fully opaque (current behavior).
    /// Lower values produce a softer pulse — useful when running 4+ clients
    /// where the full-strength flashing borders feel visually loud (issue #34).</summary>
    public int AlertOpacityPercent { get; set; } = 100;

    /// <summary>Thickness (px) of the nested inner alert border that pulses on an
    /// alert, drawn just inside the main highlight border so the selected client
    /// stays identifiable during a fleet-wide flash (issue #71). Default 3.</summary>
    public int AlertBorderThickness { get; set; } = 3;

    /// <summary>When true, every fired alert paints a small numbered badge
    /// in the corner of the alerting client's thumbnail (severity-coloured,
    /// caps display at 9+). The count clears the moment that EVE client
    /// becomes foreground. Default true.</summary>
    public bool ShowAlertBadgeOnThumbnails { get; set; } = true;

    /// <summary>Where the cycle-exclusion badge sits on an excluded thumbnail.
    /// Values: TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom,
    /// BottomRight. Default TopLeft. Configurable so it can be moved off
    /// character-name / system-name overlays. Issue #41.</summary>
    public string CycleExclusionBadgePosition { get; set; } = "TopLeft";

    /// <summary>Render every thumbnail from a periodic PrintWindow snapshot
    /// (~1s refresh) instead of the live DWM-composited preview. Trades smooth
    /// motion for a large GPU compositor saving — strongest win when running
    /// many clients. Default off.</summary>
    public bool StaticThumbnails { get; set; } = false;

    /// <summary>Freeze every thumbnail to its last frame when neither EVE nor
    /// MultiPreview is the foreground process; resume live DWM composition
    /// the moment EVE / the app comes back. Invisible during active play,
    /// stops the GPU compositing thumbnails for windows you can't see while
    /// you're working in another app. Default off.</summary>
    public bool SuspendThumbnailsWhenBackground { get; set; } = false;

    /// <summary>Per-event opt-out for the thumbnail badge. Missing key = badge
    /// fires for that event (default-on). Lets the user keep the global
    /// `ShowAlertBadgeOnThumbnails` enabled while quieting specific events
    /// (e.g. only badge on Under Attack, never on Cargo Full).</summary>
    public Dictionary<string, bool> BadgeOnThumbnailAlertTypes { get; set; } = new();

    // Per-event alert display, modelled on EVE-APM Preview (#115). Missing key = the
    // severity default (critical/warning blink, info steady; 8/6/4 s).
    public Dictionary<string, bool> AlertSolidBorder { get; set; } = new();  // true = steady, no blinking
    public Dictionary<string, int> AlertDurations { get; set; } = new();     // seconds; 0 = until you switch to that client

    /// <summary>Write what happened ("Warp scrambled", "Fleet warp") on the alerting
    /// client's thumbnail, in the event's colour, for as long as its alert lasts (#115).</summary>
    public bool ShowAlertTextOnThumbnails { get; set; } = true;
    public string AlertTextPosition { get; set; } = "Bottom";   // one of the 9 anchors, like CycleExclusionBadgePosition

    public Dictionary<string, string> AlertColors { get; set; } = new();
    public Dictionary<string, string> AlertSounds { get; set; } = new();
    public Dictionary<string, int> SoundCooldowns { get; set; } = new();
    public Dictionary<string, bool> EnabledAlertTypes { get; set; } = new()
    {
        ["attack"] = true, ["warp_scramble"] = true, ["decloak"] = true,
        ["fleet_invite"] = true, ["convo_request"] = true, ["system_change"] = true,
        ["mine_cargo_full"] = false, ["mine_asteroid_depleted"] = false,
        ["mine_crystal_broken"] = false, ["mine_module_stopped"] = false
    };
    public Dictionary<string, string> SeverityColors { get; set; } = new()
    {
        ["critical"] = "#FF0000", ["warning"] = "#FFA500", ["info"] = "#4A9EFF"
    };

    /// <summary>16-slot palette of user-defined custom colors for the Windows
    /// color picker dialog. Persisted across sessions and shared between every
    /// alert color picker so a custom color you save once is available
    /// everywhere. Stored as int[] to match WinForms.ColorDialog.CustomColors
    /// directly (BGR-encoded Win32 COLORREF values).</summary>
    public int[]? CustomColorPalette { get; set; }
    public Dictionary<string, int> SeverityCooldowns { get; set; } = new()
    {
        ["critical"] = 5, ["warning"] = 15, ["info"] = 30
    };

    /// <summary>Per-severity quiet times (IDEAS P28). Off: every alert waits AlertCooldown, as it always has; the old
    /// SeverityCooldowns above never took effect and still doesn't (files store 5/15/30 there). On: each severity waits
    /// its own number of seconds, 5 until the player changes them.</summary>
    public bool AlertCooldownBySeverity { get; set; } = false;
    public int AlertCooldownCritical { get; set; } = 5;
    public int AlertCooldownWarning { get; set; } = 5;
    public int AlertCooldownInfo { get; set; } = 5;
    public Dictionary<string, int> SeverityFlashRates { get; set; } = new()
    {
        ["critical"] = 200, ["warning"] = 500, ["info"] = 1000
    };
    public Dictionary<string, bool> SeverityTrayNotify { get; set; } = new()
    {
        ["critical"] = true, ["warning"] = false, ["info"] = false
    };

    // ── Alert Hub ───────────────────────────────────────────────────
    public bool AlertHubEnabled { get; set; } = false;
    public int AlertHubX { get; set; } = 0;
    public int AlertHubY { get; set; } = 0;
    public int AlertToastDirection { get; set; } = 5;
    public int AlertToastDuration { get; set; } = 6;

    /// <summary>When enabled, the AlertHub hexagon is hidden after a quiet
    /// period and only re-shows when a new alert fires. Requested by
    /// @CatsLiKeDogs in issue #42 — keeps the hub from being a permanent
    /// fixture on screen.</summary>
    public bool AlertHubAutoHide { get; set; } = false;
    public int AlertHubAutoHideSeconds { get; set; } = 5;

    /// <summary>When on, no Alert Hub toast is shown for an alert on the EVE
    /// client that is currently in the foreground — you're already looking at
    /// it. Thumbnail flash / badge / sound still fire. Requested by
    /// @CatsLiKeDogs in issue #47. Default off.</summary>
    public bool SuppressAlertHubToastForActiveClient { get; set; } = false;

    /// <summary>The alert history (IDEAS #1): every alert is written to the settings folder's History folder, one file
    /// per day, and kept AlertHistoryDays days (1 to 365). Off records nothing; what is already kept stays until its
    /// days run out or the player clears it. New keys: a file without them loads on, 30 days.</summary>
    public bool AlertHistoryEnabled { get; set; } = true;
    public int AlertHistoryDays { get; set; } = 30;

    /// <summary>Keep the timed snoozes of single events (IDEAS #30) when MultiPreview restarts, in alert_snoozes.json
    /// in the settings folder. A new key, off: a file without it ends every snooze when MultiPreview closes.</summary>
    public bool KeepAlertSnoozes { get; set; } = false;

    // ── Log Monitoring ──────────────────────────────────────────────
    public bool EnableChatLogMonitoring { get; set; } = true;
    public bool EnableGameLogMonitoring { get; set; } = true;
    public string ChatLogDirectory { get; set; } = "";
    public string GameLogDirectory { get; set; } = "";

    // ── Stat Overlay ────────────────────────────────────────────────
    public int StatOverlayFontSize { get; set; } = 8;
    public int StatOverlayOpacity { get; set; } = 200;
    public string StatOverlayBgColor { get; set; } = "#1a1a2e";
    public string StatOverlayTextColor { get; set; } = "#00FF88";
    public bool StatLoggingEnabled { get; set; } = false;
    public string StatLogDirectory { get; set; } = "";
    public int StatLogRetentionDays { get; set; } = 30;
    public Dictionary<string, CharacterStatSettings> PerCharacterStats { get; set; } = new();
    public Dictionary<string, ThumbnailRect> StatWindowPositions { get; set; } = new();

    /// <summary>Global default metric set — bits that are visible for every character
    /// unless overridden. Effective per-character = (Global | ForcedOn) &amp; ~ForcedOff.</summary>
    public StatMetrics GlobalStatMetrics { get; set; } = StatMetrics.None;

    /// <summary>Leave hits from and on NPCs out of the stats overlay's damage numbers (IDEAS P32). A new key, off: every
    /// hit counts, as it always did. The old StatMetrics.IncludeNpc bit can't carry this: every file stores it off while
    /// every hit has always counted.</summary>
    public bool StatsExcludeNpcDamage { get; set; } = false;

    // ── Settings GUI ────────────────────────────────────────────────
    public int SettingsWindowWidth { get; set; } = 1080;
    public int SettingsWindowHeight { get; set; } = 1080;
    public int SettingsUiFontSize { get; set; } = 12;

    // ── RTSS / Misc ─────────────────────────────────────────────────
    public bool RtssEnabled { get; set; } = false;
    public int RtssFpsLimit { get; set; } = 15;
    public bool ShowRtssFps { get; set; } = false;

    // FPS counter layout (#103). Sentinels keep every existing config
    // pixel-identical: -1 = the fixed 5px inset, 0 = follow ThumbnailTextSize.
    public int FpsOverlayMarginX { get; set; } = -1;
    public int FpsOverlayMarginY { get; set; } = -1;
    public int FpsOverlayTextSize { get; set; } = 0;
    public bool ReceivePreReleaseUpdates { get; set; } = false;
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    // UI language (issue #86). Two-letter code ("en"/"zh"/...); "" = auto-detect from OS.
    public string Language { get; set; } = "";

    // ── Broadcast-key HUD (shows which held key is being propagated to clients) ──
    public bool ShowBroadcastKeyHud { get; set; } = false;
    public int BroadcastHudX { get; set; } = 0;
    public int BroadcastHudY { get; set; } = 0;

    // ── Per-process audio ───────────────────────────────────────────
    // Auto-solo: when you switch to a client, mute all the other EVE clients and
    // unmute the active one (Windows per-process audio, matched by PID).
    public bool AutoSoloClientAudio { get; set; } = false;
    // When no EVE client is active (focus is on another app/window), mute every
    // tracked client instead of leaving the last-active one unmuted. Only takes
    // effect while AutoSoloClientAudio is on.
    public bool MuteAllClientAudioWhenNoneActive { get; set; } = false;

    // ── Under Fire Indicator ────────────────────────────────────────
    public bool EnableUnderFireIndicator { get; set; } = true;
    public int UnderFireTimeoutSeconds { get; set; } = 5;

    // ── Process Monitor ─────────────────────────────────────────────
    public bool ShowProcessStats { get; set; } = false;
    public string ProcessStatsTextSize { get; set; } = "9";

    // ── Quick-Switch Wheel ──────────────────────────────────────────
    public string QuickSwitchHotkey { get; set; } = "";
    public List<string> QuickSwitchCardOrder { get; set; } = new();

    // ── Character Select Cycling ────────────────────────────────────
    public bool CharSelectCyclingEnabled { get; set; } = false;
    public string CharSelectForwardHotkey { get; set; } = "";
    public string CharSelectBackwardHotkey { get; set; } = "";

    // ── Thumbnail Groups (global) ───────────────────────────────────
    public List<ThumbnailGroup> ThumbnailGroups { get; set; } = new();

    // ── Thumbnail Annotations ───────────────────────────────────────
    // Per-character custom label text (e.g. "Scout", "DPS", "Logi").
    public Dictionary<string, string> ThumbnailAnnotations { get; set; } = new();

    // Per-character overrides for label color + size (2.0.6). Keys match
    // ThumbnailAnnotations; missing entries fall back to the global thumbnail
    // text color / size. Empty Color or Size==0 means "use default".
    public Dictionary<string, ThumbnailLabelStyle> ThumbnailLabelStyles { get; set; } = new();

    // ── EVE Manager ─────────────────────────────────────────────────
    public bool EveManagerUseESI { get; set; } = true;
    public string EveBackupDir { get; set; } = "";
    public string EveSettingsDir { get; set; } = "";
    public AhkEveManager? EveManager { get; set; }

    /// <summary>Account→character associations for the EVE Manager
    /// Account Copy panel. Keyed by EVE account (user) ID; value is the set of
    /// character IDs linked to that account by Detect Account (Tools › EVE
    /// settings). Lets the Account Copy list label an otherwise-anonymous user
    /// id by its characters' names. Issue #45 follow-up.</summary>
    public Dictionary<string, List<string>> AccountCharacterMap { get; set; } = new();

    /// <summary>User-assigned friendly names for EVE accounts, keyed by
    /// account (user) ID. Overrides the auto-derived character-name label in
    /// the Account Copy list when set.</summary>
    public Dictionary<string, string> AccountLabels { get; set; } = new();

    // ── Profiles ────────────────────────────────────────────────────
    public string LastUsedProfile { get; set; } = "Default";
    public Dictionary<string, Profile> Profiles { get; set; } = new()
    {
        ["Default"] = new Profile()
    };

    // ═══════════════════════════════════════════════════════════════════
    // Convenience accessors — proxy to the active profile so consuming
    // code can use s.PropertyName without knowing about profile nesting.
    // ═══════════════════════════════════════════════════════════════════

    [JsonIgnore]
    private Profile _cp => Profiles.GetValueOrDefault(LastUsedProfile) ?? Profiles.Values.FirstOrDefault() ?? new Profile();

    // Per-profile Thumbnail Settings proxies
    [JsonIgnore] public bool ShowAllColoredBorders { get => _cp.ShowAllColoredBorders; set => _cp.ShowAllColoredBorders = value; }
    [JsonIgnore] public bool AutoCharacterBorders { get => _cp.AutoCharacterBorders; set => _cp.AutoCharacterBorders = value; }
    [JsonIgnore] public bool HideThumbnailsOnLostFocus { get => _cp.HideThumbnailsOnLostFocus; set => _cp.HideThumbnailsOnLostFocus = value; }
    [JsonIgnore] public bool ShowThumbnailsAlwaysOnTop { get => _cp.ShowThumbnailsAlwaysOnTop; set => _cp.ShowThumbnailsAlwaysOnTop = value; }
    [JsonIgnore] public bool KeepThumbnailsAboveClients { get => _cp.KeepThumbnailsAboveClients; set => _cp.KeepThumbnailsAboveClients = value; }
    [JsonIgnore] public int ThumbnailOpacity { get => _cp.ThumbnailOpacity; set => _cp.ThumbnailOpacity = value; }
    [JsonIgnore] public bool OpacityOnHover { get => _cp.OpacityOnHover; set => _cp.OpacityOnHover = value; }
    [JsonIgnore] public int ClientHighlightBorderThickness { get => _cp.ClientHighlightBorderThickness; set => _cp.ClientHighlightBorderThickness = value; }
    [JsonIgnore] public string ClientHighlightColor { get => _cp.ClientHighlightColor; set => _cp.ClientHighlightColor = value; }
    [JsonIgnore] public bool ShowClientHighlightBorder { get => _cp.ShowClientHighlightBorder; set => _cp.ShowClientHighlightBorder = value; }
    [JsonIgnore] public bool HighlightOverridesGroupColor { get => _cp.HighlightOverridesGroupColor; set => _cp.HighlightOverridesGroupColor = value; }
    [JsonIgnore] public string ThumbnailTextFont { get => _cp.ThumbnailTextFont; set => _cp.ThumbnailTextFont = value; }
    [JsonIgnore] public string ThumbnailTextSize { get => _cp.ThumbnailTextSize; set => _cp.ThumbnailTextSize = value; }
    [JsonIgnore] public string ThumbnailTextColor { get => _cp.ThumbnailTextColor; set => _cp.ThumbnailTextColor = value; }
    [JsonIgnore] public bool ShowThumbnailTextOverlay { get => _cp.ShowThumbnailTextOverlay; set => _cp.ShowThumbnailTextOverlay = value; }
    [JsonIgnore] public ThumbnailMargins ThumbnailTextMargins { get => _cp.ThumbnailTextMargins; set => _cp.ThumbnailTextMargins = value; }
    [JsonIgnore] public int InactiveClientBorderThickness { get => _cp.InactiveClientBorderThickness; set => _cp.InactiveClientBorderThickness = value; }
    [JsonIgnore] public string InactiveClientBorderColor { get => _cp.InactiveClientBorderColor; set => _cp.InactiveClientBorderColor = value; }
    [JsonIgnore] public string NotLoggedInIndicator { get => _cp.NotLoggedInIndicator; set => _cp.NotLoggedInIndicator = value; }
    [JsonIgnore] public string NotLoggedInColor { get => _cp.NotLoggedInColor; set => _cp.NotLoggedInColor = value; }

    // Per-profile Client Settings proxies
    [JsonIgnore] public bool MinimizeInactiveClients { get => _cp.MinimizeInactiveClients; set => _cp.MinimizeInactiveClients = value; }
    [JsonIgnore] public bool AlwaysMaximize { get => _cp.AlwaysMaximize; set => _cp.AlwaysMaximize = value; }
    [JsonIgnore] public bool TrackClientPositions { get => _cp.TrackClientPositions; set => _cp.TrackClientPositions = value; }
    // Fixed client spawn position (issue #85): 0=Off, 1=Center on monitor, 2=Custom X/Y, 3=Fixed slots
    [JsonIgnore] public int ClientPositionMode { get => _cp.ClientPositionMode; set => _cp.ClientPositionMode = value; }
    [JsonIgnore] public int ClientPositionX { get => _cp.ClientPositionX; set => _cp.ClientPositionX = value; }
    [JsonIgnore] public int ClientPositionY { get => _cp.ClientPositionY; set => _cp.ClientPositionY = value; }
    // Let the ACTIVE client sit above the Windows taskbar (issue #99). Off by default.
    [JsonIgnore] public bool ClientCoverTaskbar { get => _cp.ClientCoverTaskbar; set => _cp.ClientCoverTaskbar = value; }
    // Fixed client slots (ClientPositionMode = 3)
    [JsonIgnore] public List<ClientSlot> ClientSlots { get => _cp.ClientSlots; set => _cp.ClientSlots = value; }
    [JsonIgnore] public string DefaultClientSlotId { get => _cp.DefaultClientSlotId; set => _cp.DefaultClientSlotId = value; }
    [JsonIgnore] public List<string> ClientSlotExcluded { get => _cp.ClientSlotExcluded; set => _cp.ClientSlotExcluded = value; }
    [JsonIgnore] public bool ClientSlotsResize { get => _cp.ClientSlotsResize; set => _cp.ClientSlotsResize = value; }

    // Per-profile Custom Colors proxies
    [JsonIgnore] public bool CustomColorsActive { get => _cp.CustomColorsActive; set => _cp.CustomColorsActive = value; }
    [JsonIgnore] public Dictionary<string, CustomColorEntry> CustomColors { get => _cp.CustomColors; set => _cp.CustomColors = value; }

    // Per-profile Thumbnail Visibility proxy
    [JsonIgnore] public Dictionary<string, int> ThumbnailVisibility { get => _cp.ThumbnailVisibility; set => _cp.ThumbnailVisibility = value; }

    // Per-profile secondary thumbnails proxy
    [JsonIgnore] public Dictionary<string, SecondaryThumbnailSettings> SecondaryThumbnails { get => _cp.SecondaryThumbnails; set => _cp.SecondaryThumbnails = value; }

    // Per-profile performance settings proxies
    [JsonIgnore] public bool ManageAffinity { get => _cp.ManageAffinity; set => _cp.ManageAffinity = value; }
    [JsonIgnore] public bool AutoBalanceCores { get => _cp.AutoBalanceCores; set => _cp.AutoBalanceCores = value; }
    [JsonIgnore] public Dictionary<string, int> PerClientCores { get => _cp.PerClientCores; set => _cp.PerClientCores = value; }

    // Per-profile hotkey groups proxy
    [JsonIgnore] public Dictionary<string, HotkeyGroup> HotkeyGroups { get => _cp.HotkeyGroups; set => _cp.HotkeyGroups = value; }

    // Per-profile crop proxies
    [JsonIgnore] public bool CropEnabled { get => _cp.CropEnabled; set => _cp.CropEnabled = value; }
    [JsonIgnore] public Dictionary<string, List<CropDefinition>> Crops { get => _cp.Crops; set => _cp.Crops = value; }
    [JsonIgnore] public Dictionary<string, int> PerClientAudioVolume { get => _cp.PerClientAudioVolume; set => _cp.PerClientAudioVolume = value; }

    // Backwards-compat: SettingsWindowSize as an object (for code that uses SettingsWindowSize.Width/Height)
    [JsonIgnore] public WindowSize SettingsWindowSize => new() { Width = SettingsWindowWidth, Height = SettingsWindowHeight };

    // Properties that were removed but may still be referenced — keep as simple props
    public bool ResizeThumbnailsOnHover { get; set; } = false;
    public double HoverScale { get; set; } = 1.5;
    public string AlertFlashColor { get; set; } = "0xff0000";
    public int AlertCooldown { get; set; } = 5;
    public string AlertSoundFile { get; set; } = "";

    // Cycle-wrap sound (issue #24) — plays when a cycle hotkey rolls from the
    // last client back to the first (or first back to the last when reversing).
    public bool CycleWrapSoundEnabled { get; set; } = false;
    public string CycleWrapSoundFile { get; set; } = "";
    public bool StatOverlayEnabled { get; set; } = true;

    // ── Color Parsing Utility ────────────────────────────────────────
    /// <summary>
    /// Parse color strings in multiple formats: rgb(r,g,b), #RRGGBB, 0xRRGGBB.
    /// Returns a WPF Color. Matches AHK Propertys.ahk convertToHex behavior.
    /// </summary>
    public static System.Windows.Media.Color ParseColor(string input, System.Windows.Media.Color fallback = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback;
        input = input.Trim();

        // rgb(r,g,b) format
        var rgbMatch = Regex.Match(input, @"^rgb\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$", RegexOptions.IgnoreCase);
        if (rgbMatch.Success)
        {
            return System.Windows.Media.Color.FromRgb(
                byte.Parse(rgbMatch.Groups[1].Value),
                byte.Parse(rgbMatch.Groups[2].Value),
                byte.Parse(rgbMatch.Groups[3].Value));
        }

        // 0xRRGGBB format → convert to #RRGGBB
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            input = "#" + input[2..];

        // Ensure # prefix
        if (!input.StartsWith("#"))
            input = "#" + input;

        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(input);
        }
        catch
        {
            return fallback;
        }
    }
}

// ── Sub-Models ──────────────────────────────────────────────────────

/// <summary>Controls what happens to the Settings window when the app starts.
/// Persisted as an integer (0/1/2) for AHK compatibility.</summary>
public enum StartupSettingsMode
{
    /// <summary>App starts to the tray — Settings stays closed until the user opens it.</summary>
    Off = 0,
    /// <summary>App automatically opens the Settings window on launch.</summary>
    Open = 1,
    /// <summary>App automatically opens the Settings window, already minimized to the taskbar.</summary>
    OpenMinimized = 2,
}

public class ThumbnailRect
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public class ThumbnailSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public class ThumbnailMargins
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

public class WindowSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public class CustomColorEntry
{
    public string Border { get; set; } = "FFFFFF";
    public string Text { get; set; } = "FFFFFF";
    public string InactiveBorder { get; set; } = "FFFFFF";

    /// <summary>Whether <paramref name="text"/> is a text colour the player chose. The two values an entry gets without a
    /// choice are not: this class's default (also what an AHK file short of text colours loads) and the 0xfac57a both
    /// settings windows seed a new entry with. Text was never painted until the ideas batch, so a file holding them
    /// keeps the profile's text colour.</summary>
    public static bool HasOwnText(string? text)
    {
        var t = text?.Trim() ?? "";
        return t.Length > 0 && !t.Equals("FFFFFF", StringComparison.OrdinalIgnoreCase)
               && !t.Equals("0xfac57a", StringComparison.OrdinalIgnoreCase);
    }
}

public class SecondaryThumbnailSettings
{
    [JsonPropertyName("enabled")]
    public int Enabled { get; set; } = 1;

    [JsonIgnore]
    public bool IsEnabled { get => Enabled != 0; set => Enabled = value ? 1 : 0; }

    [JsonPropertyName("opacity")]
    public int Opacity { get; set; } = 180;

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; } = 200;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 120;
}

/// <summary>
/// A named profile containing per-character positions, per-profile settings, and hotkey bindings.
/// In AHK JSON, these are under _Profiles.{name} with nested sub-objects.
/// After adapter conversion, per-profile settings are flattened here.
/// </summary>
public class Profile
{
    // ── Per-character data ──────────────────────────────────────────
    public Dictionary<string, ThumbnailRect> ThumbnailPositions { get; set; } = new();
    public Dictionary<string, ClientPosition> ClientPositions { get; set; } = new();
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new();
    public Dictionary<string, HotkeyGroup> HotkeyGroups { get; set; } = new();
    public Dictionary<string, SecondaryThumbnailSettings> SecondaryThumbnails { get; set; } = new();
    public Dictionary<string, ThumbnailGroup> Groups { get; set; } = new();
    public Dictionary<string, int> ThumbnailVisibility { get; set; } = new();
    public List<string> DontMinimizeClients { get; set; } = new();

    /// <summary>Name of the layout preset last applied to this profile, so switching
    /// profiles can show which layout is in effect (#94). Display only — it does not
    /// re-apply the preset by itself.</summary>
    public string AppliedLayoutPreset { get; set; } = "";

    /// <summary>The characters that pick this profile while "Switch profiles by who is logged in" is on (IDEAS #11): names
    /// joined by commas (EVE names have none), "" = none. Read by AutoProfile.Candidates. Kept in the profile's "Thumbnail
    /// Settings" in the file. A full copy of another profile keeps this profile's own (ProfileCopy). New key: "".</summary>
    public string AutoSelectCharacters { get; set; } = "";

    // ── Per-profile Thumbnail Settings (from AHK "Thumbnail Settings" sub-object) ──
    public bool ShowAllColoredBorders { get; set; } = false;
    /// <summary>IDEAS P19: every character without a colour of its own or from a group gets one from a built-in palette
    /// (AutoBorderColors). Kept in the profile's "Thumbnail Settings" in the file. New key: false.</summary>
    public bool AutoCharacterBorders { get; set; } = false;
    public bool HideThumbnailsOnLostFocus { get; set; } = false;
    public bool ShowThumbnailsAlwaysOnTop { get; set; } = true;
    public bool KeepThumbnailsAboveClients { get; set; } = false;
    public int ThumbnailOpacity { get; set; } = 80;
    public bool OpacityOnHover { get; set; } = false;
    public int ClientHighlightBorderThickness { get; set; } = 4;
    public string ClientHighlightColor { get; set; } = "#E36A0D";
    public bool ShowClientHighlightBorder { get; set; } = true;
    // Group colours paint a group's active AND inactive clients alike, so the one you
    // are in cannot be told apart at a glance. On = active client uses ClientHighlightColor (#115).
    public bool HighlightOverridesGroupColor { get; set; } = false;
    public string ThumbnailTextFont { get; set; } = "Gill Sans MT";
    public string ThumbnailTextSize { get; set; } = "12";
    public string ThumbnailTextColor { get; set; } = "#FAC57A";
    public bool ShowThumbnailTextOverlay { get; set; } = true;
    public ThumbnailMargins ThumbnailTextMargins { get; set; } = new() { X = 5, Y = 5 };
    public int InactiveClientBorderThickness { get; set; } = 2;
    public string InactiveClientBorderColor { get; set; } = "#8A8A8A";
    public string NotLoggedInIndicator { get; set; } = "text";
    public string NotLoggedInColor { get; set; } = "#555555";

    // ── Per-profile Client Settings (from AHK "Client Settings" sub-object) ──
    public bool MinimizeInactiveClients { get; set; } = false;
    public bool AlwaysMaximize { get; set; } = false;
    public bool TrackClientPositions { get; set; } = false;
    // Fixed client spawn position (issue #85): 0=Off, 1=Center on monitor, 2=Custom X/Y
    public int ClientPositionMode { get; set; } = 0;
    public int ClientPositionX { get; set; } = 0;
    public int ClientPositionY { get; set; } = 0;
    // Issue #99: when true, the focused EVE client is raised into the topmost band so a
    // full-height fixed window covers the taskbar (ISBoxer-style), and dropped back out
    // of it on blur so the taskbar stays usable. Centering also uses the whole screen
    // instead of the work area, so a screen-height window is not pushed down.
    public bool ClientCoverTaskbar { get; set; } = false;

    // Fixed client slots (ClientPositionMode = 3). See Models/ClientSlot.cs.
    public List<ClientSlot> ClientSlots { get; set; } = new();
    /// <summary>Slot for clients not listed in any slot ("" = leave them alone).</summary>
    public string DefaultClientSlotId { get; set; } = "";
    /// <summary>Characters never positioned, even when a default slot exists.</summary>
    public List<string> ClientSlotExcluded { get; set; } = new();
    /// <summary>Resize clients to their slot (true) or only move them. Hidden fallback.</summary>
    public bool ClientSlotsResize { get; set; } = true;

    // ── Per-profile Custom Colors (from AHK "Custom Colors" sub-object) ──
    public bool CustomColorsActive { get; set; } = false;
    public Dictionary<string, CustomColorEntry> CustomColors { get; set; } = new();

    // ── Per-profile Performance Settings ──
    public bool ManageAffinity { get; set; } = false;
    public bool AutoBalanceCores { get; set; } = true;
    public Dictionary<string, int> PerClientCores { get; set; } = new();

    // ── Per-profile Crops (multiple named crops per character) ──
    public bool CropEnabled { get; set; } = false;
    public Dictionary<string, List<CropDefinition>> Crops { get; set; } = new();
    // Per-character audio volume (0-100; 100 = default/unchanged). Applied via Windows
    // per-process audio and re-applied when a client is activated.
    public Dictionary<string, int> PerClientAudioVolume { get; set; } = new();
}

/// <summary>
/// A single cropped DWM thumbnail popup definition.
/// Source rect = region of the EVE client window to capture (rcSource).
/// Popup rect = on-screen position and size of the popup window displaying that region.
/// </summary>
public class CropDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Crop";

    [JsonPropertyName("sourceX")]
    public int SourceX { get; set; } = 0;

    [JsonPropertyName("sourceY")]
    public int SourceY { get; set; } = 0;

    [JsonPropertyName("sourceWidth")]
    public int SourceWidth { get; set; } = 400;

    [JsonPropertyName("sourceHeight")]
    public int SourceHeight { get; set; } = 300;

    [JsonPropertyName("popupX")]
    public int PopupX { get; set; } = 100;

    [JsonPropertyName("popupY")]
    public int PopupY { get; set; } = 100;

    [JsonPropertyName("popupWidth")]
    public int PopupWidth { get; set; } = 320;

    [JsonPropertyName("popupHeight")]
    public int PopupHeight { get; set; } = 240;

    [JsonPropertyName("showLabel")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool ShowLabel { get; set; } = true;

    /// <summary>Crop popup opacity as a percent (10-100). 100 = fully opaque
    /// (previous behavior). Applied to the DWM preview content (issue #62).</summary>
    [JsonPropertyName("opacity")]
    public int Opacity { get; set; } = 100;

    /// <summary>When true, the crop popup ignores mouse input entirely (click-
    /// through) so clicks land on whatever is behind it. Disables drag/resize of
    /// the popup until turned back off (issue #62).</summary>
    [JsonPropertyName("clickThrough")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool ClickThrough { get; set; } = false;
}

public class ClientPosition
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonPropertyName("IsMaximized")]
    public double IsMaximized { get; set; }
}

public class HotkeyBinding
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";
}

public class ThumbnailGroup
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#4fc3f7";

    [JsonPropertyName("members")]
    public List<string> Members { get; set; } = new();
}

/// <summary>
/// Per-character override for thumbnail annotation text style. Color uses the
/// usual hex format ("#RRGGBB" or "0xRRGGBB"); empty string means "use default
/// thumbnail text color". Size is the label's point size; 0 means "use default".
/// </summary>
public class ThumbnailLabelStyle
{
    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("size")]
    public int Size { get; set; } = 0;
}

public class HotkeyGroup
{
    [JsonPropertyName("ForwardsHotkey")]
    public string ForwardsHotkey { get; set; } = "";

    [JsonPropertyName("BackwardsHotkey")]
    public string BackwardsHotkey { get; set; } = "";

    [JsonPropertyName("Characters")]
    public List<string> Characters { get; set; } = new();
}

/// <summary>
/// Custom converter that reads both AHK array format [{"CharName": "F1"}]
/// and C# dictionary format {"CharName": {"key": "F1"}}.
/// Always writes in C# dictionary format.
/// </summary>
public class HotkeyDictionaryConverter : JsonConverter<Dictionary<string, HotkeyBinding>>
{
    public override Dictionary<string, HotkeyBinding> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, HotkeyBinding>();

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // AHK format: [{"CharName": "F1"}, {"CharName2": "F2"}]
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            string charName = reader.GetString()!;
                            reader.Read();
                            string hotkey = reader.GetString() ?? "";
                            result[charName] = new HotkeyBinding { Key = hotkey };
                        }
                    }
                }
            }
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // C# format: {"CharName": {"key": "F1", ...}}
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string charName = reader.GetString()!;
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        // Simple string value
                        result[charName] = new HotkeyBinding { Key = reader.GetString() ?? "" };
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        var binding = JsonSerializer.Deserialize<HotkeyBinding>(ref reader, options)
                                      ?? new HotkeyBinding();
                        result[charName] = binding;
                    }
                }
            }
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, HotkeyBinding> value, JsonSerializerOptions options)
    {
        // Write in AHK-compatible array format for backwards compatibility
        writer.WriteStartArray();
        foreach (var kv in value)
        {
            writer.WriteStartObject();
            writer.WriteString(kv.Key, kv.Value.Key);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Per-metric visibility flags for the stat overlay. One bit per metric (plus
/// <see cref="IncludeNpc"/>). Resolution for a character:
/// <c>effective = (GlobalStatMetrics | ForcedOn) &amp; ~ForcedOff</c>.
/// </summary>
[Flags]
public enum StatMetrics : uint
{
    None = 0,

    // DPS
    DpsOut = 1u << 0, // Out = Dmg/s Out
    DpsIn  = 1u << 1, // In = Dmg/s In
    Tdi    = 1u << 2, // TDI = Total Dmg In
    Tdo    = 1u << 3, // TDO = Total Dmg Out

    // Logi
    Arps = 1u << 4, // ARPS = Armor Rep/s
    Srps = 1u << 5, // SRPS = Shield Rep/s
    Ctps = 1u << 6, // CTPS = Cap Transf/s
    Taro = 1u << 7, // TARO = Total Armor Rep Out
    Tari = 1u << 8, // TARI = Total Armor Rep In
    Tsro = 1u << 9, // TSRO = Total Shield Rep Out
    Tsri = 1u << 10, // TSRI = Total Shield Rep In

    // Mining
    Ompc = 1u << 11, // OMPC = Ore Mined/Cycle
    Omph = 1u << 12, // OMPH = Ore Mined/Hour
    Gmpc = 1u << 13, // GMPC = Gas Mined/Cycle
    Gmph = 1u << 14, // GMPH = Gas Mined/Hour
    Imph = 1u << 15, // IMPH = Ice Mined/Hour

    // Ratting
    Tipt = 1u << 16, // TIPT = Total ISK/Tick
    Tiph = 1u << 17, // TIPH = Total ISK/Hour
    Tips = 1u << 18, // TIPS = Total ISK/Session

    // Meta — affects what counts as DPS, not a metric itself
    IncludeNpc = 1u << 19,

    // Breakdown of incoming damage by damage type (issue #11)
    DpsInByType = 1u << 20,

    // Category masks (not stored on their own, used by the UI for group operations)
    DpsMask  = DpsOut | DpsIn | Tdi | Tdo | DpsInByType,
    LogiMask = Arps | Srps | Ctps | Taro | Tari | Tsro | Tsri,
    MineMask = Ompc | Omph | Gmpc | Gmph | Imph,
    RatMask  = Tipt | Tiph | Tips,
    AllMetrics = DpsMask | LogiMask | MineMask | RatMask,
}

/// <summary>
/// Per-character override masks for the stat overlay. Each bit is tri-state:
///   forced on  → set in <see cref="ForcedOn"/>, always visible for this character
///   forced off → set in <see cref="ForcedOff"/>, always hidden for this character
///   inherit    → absent from both masks, follows <see cref="AppSettings.GlobalStatMetrics"/>
///
/// A bit should never be set in both masks; <see cref="Resolve"/> treats ForcedOff as stronger.
/// </summary>
public class CharacterStatSettings
{
    [JsonPropertyName("forcedOn")]
    public StatMetrics ForcedOn { get; set; } = StatMetrics.None;

    [JsonPropertyName("forcedOff")]
    public StatMetrics ForcedOff { get; set; } = StatMetrics.None;

    public static StatMetrics Resolve(StatMetrics global, CharacterStatSettings? overrides)
    {
        if (overrides == null) return global;
        return (global | overrides.ForcedOn) & ~overrides.ForcedOff;
    }

    /// <summary>Tri-state view for a specific bit — null = inherit, true = ForcedOn, false = ForcedOff.</summary>
    public bool? GetOverrideState(StatMetrics bit)
    {
        if ((ForcedOff & bit) != 0) return false;
        if ((ForcedOn & bit) != 0) return true;
        return null;
    }

    /// <summary>Set a tri-state override for the given bit.</summary>
    public void SetOverrideState(StatMetrics bit, bool? state)
    {
        ForcedOn &= ~bit;
        ForcedOff &= ~bit;
        if (state == true) ForcedOn |= bit;
        else if (state == false) ForcedOff |= bit;
    }

    public int OverrideCount => System.Numerics.BitOperations.PopCount((uint)(ForcedOn | ForcedOff));
}

/// <summary>Static metadata for a single stat metric — code, category, human label.</summary>
public readonly record struct StatMetricDef(StatMetrics Bit, string Code, string Category, string Label);

public static class StatMetricCatalog
{
    public static readonly StatMetricDef[] All =
    {
        new(StatMetrics.DpsOut, "Out",  "DPS",  "Dmg/s Out"),
        new(StatMetrics.DpsIn,  "In",   "DPS",  "Dmg/s In"),
        new(StatMetrics.Tdi,    "TDI",  "DPS",  "Total Dmg In"),
        new(StatMetrics.Tdo,    "TDO",  "DPS",  "Total Dmg Out"),

        new(StatMetrics.Arps, "ARPS", "Logi", "Armor Rep/s"),
        new(StatMetrics.Srps, "SRPS", "Logi", "Shield Rep/s"),
        new(StatMetrics.Ctps, "CTPS", "Logi", "Cap Transf/s"),
        new(StatMetrics.Taro, "TARO", "Logi", "Total Armor Rep Out"),
        new(StatMetrics.Tari, "TARI", "Logi", "Total Armor Rep In"),
        new(StatMetrics.Tsro, "TSRO", "Logi", "Total Shield Rep Out"),
        new(StatMetrics.Tsri, "TSRI", "Logi", "Total Shield Rep In"),

        new(StatMetrics.Ompc, "OMPC", "Mine", "Ore Mined/Cycle"),
        new(StatMetrics.Omph, "OMPH", "Mine", "Ore Mined/Hour"),
        new(StatMetrics.Gmpc, "GMPC", "Mine", "Gas Mined/Cycle"),
        new(StatMetrics.Gmph, "GMPH", "Mine", "Gas Mined/Hour"),
        new(StatMetrics.Imph, "IMPH", "Mine", "Ice Mined/Hour"),

        new(StatMetrics.Tipt, "TIPT", "Rat", "Total ISK/Tick"),
        new(StatMetrics.Tiph, "TIPH", "Rat", "Total ISK/Hour"),
        new(StatMetrics.Tips, "TIPS", "Rat", "Total ISK/Session"),
    };
}

