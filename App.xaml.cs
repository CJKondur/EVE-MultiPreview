using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;
using EveMultiPreview.Views;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace EveMultiPreview;

/// <summary>
/// Application entry point. Wires up all services:
///   SettingsService → WindowDiscoveryService → ThumbnailManager → HotkeyService → LogMonitor → StatTracker → AlertHub
/// Full tray menu matching AHK TrayMenu.ahk.
/// Per-feature debug logging with [App:*] tags.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private NotifyIcon? _trayIcon;
    private TrayIcons? _trayIcons;           // IDEAS P4: the icons the tray shows; made on first use, let go of at exit
    private TrayAlertKind? _trayIconShown;   // which of them the NotifyIcon has now
    // Built by Views/TrayMenu; it re-reads its labels on every open, so a language change applies at once (issue #86).
    private TrayMenu? _trayMenu;
    private SettingsService? _settings;
    private WindowDiscoveryService? _discovery;
    private WinEventHookService? _winEvents;
    private ThumbnailManager? _thumbnailManager;
    private HotkeyService? _hotkeyService;
    private LogMonitorService? _logMonitor;
    private StatTrackerService? _statTracker;
    private AlertHub? _alertHub;
    private AlertHistory? _alertHistory;
    /// <summary>Alert events snoozed for one character (IDEAS #30). Empty until the player snoozes one.</summary>
    private readonly EventSnoozes _snoozes = new();
    /// <summary>IDEAS #46: holds Windows from sleeping while a character is logged in and the switch is on. Asked on the
    /// window's thread only (the execution state belongs to the calling thread); the real call is passed here, once.</summary>
    private readonly KeepAwake _keepAwake = new KeepAwake(KeepAwake.SetThreadExecutionState, ex => LogError(ex));
    private EventSnoozeFile? _snoozeFile;
    /// <summary>IDEAS #9: which downtime warnings were told for the next downtime.</summary>
    private readonly DowntimeWarnings _downtimeWarnings = new();
    /// <summary>IDEAS #34, P120: tells when Tranquility is back up. Its fetch is the "not allowed" one until the owner approves
    /// the live status check (TranquilityWatch.LiveCheckApproved), so it never reaches the network today.</summary>
    private readonly TranquilityWatch _tranquilityWatch = new TranquilityWatch(TranquilityWatch.NotAllowedFetch);
    /// <summary>Do-not-disturb (IDEAS #32): alert sounds are muted while it is on. What the hotkey or the tray chose is
    /// forgotten when MultiPreview closes; the quiet hours are settings.</summary>
    private readonly DoNotDisturb _doNotDisturb = new();
    private bool DoNotDisturbOn => _doNotDisturb.IsOn(_settings?.Settings, DateTime.Now);
    /// <summary>Escalation (IDEAS #29), and the one timer that asks it once a second while it is switched on.</summary>
    private readonly AlertEscalation _escalation = new();
    private DispatcherTimer? _alertTimer;
    /// <summary>The further ways an alert reaches the player (IDEAS batch 6): spoken, as a Windows notification, by an
    /// enlarged thumbnail. Empty until one of them is added at startup; told of every alert that was shown.</summary>
    private readonly AlertWays _alertWays = new(LogError);
    /// <summary>Alerts per group (IDEAS #31): for which characters an event is on. ApplyAlertSwitches hands it the
    /// settings; the dispatcher asks it first, for every alert. While no group has a choice it answers "on" at once.</summary>
    private readonly AlertProfiles _alertProfiles = new();
    /// <summary>Sounds per group (IDEAS #58): the sound file a character's groups give an alert. ApplyAlertSwitches
    /// hands it the settings; PlayAlertSound asks it for every sound. While no group has a sound it answers null at once.</summary>
    private readonly GroupSounds _groupSounds = new();
    /// <summary>Spoken alerts (IDEAS #26): the voice of Windows, and the way that tells it what to say. Made at startup;
    /// the way says nothing while "Speak alerts" is off.</summary>
    private WindowsSpeaker? _speaker;
    private SpokenWay? _spokenWay;
    /// <summary>Windows notifications (IDEAS #33): the way that shows them, and the one registry key Windows needs for
    /// a program without an installer. Made at startup; while "Show alerts as Windows notifications" is off the way
    /// shows nothing and the key is not there.</summary>
    private NotificationsWay? _notificationsWay;
    private PushWay? _pushWay;
    private NotificationRegistration? _notificationRegistration;
    /// <summary>What a click on a notification does, and how the switch is followed: App's parts as functions, in a
    /// class a test can run (NotificationActions).</summary>
    private NotificationActions? _notificationActions;
    /// <summary>"Left behind" (IDEAS #5): told every system change, asked on the one-second timer while the event is on.</summary>
    private readonly StragglerWatch _stragglers = new();
    /// <summary>The look for it and the alert it raises, with what App has for both (set at startup).</summary>
    private StragglerAlerts? _stragglerAlerts;
    /// <summary>"Client memory high" (IDEAS #45): fed what the process monitor last read for each client, asked on the
    /// window's thread (LookAtMemory).</summary>
    private readonly MemoryWatch _memoryWatch = new();
    /// <summary>IDEAS #22, #47: what ClientWatch judged of each client. The window's thread only.</summary>
    private readonly ClientWatch _clientWatch = new();
    /// <summary>"Character idle" (IDEAS #7): noted on the log thread while the event is on, asked on the half-second
    /// timer (FollowIdle).</summary>
    private readonly IdleWatch _idleWatch = new();

    /// <summary>IDEAS #35: focus mode is on. Not saved: off at every start, as Hide all is. The window's thread only.</summary>
    private bool _focusModeOn;
    private readonly FocusArrivals _focusArrivals = new();   // IDEAS P124: a client that logs in while focus mode is on
    /// <summary>IDEAS #11: which profile the characters logged in point to. Asked twice a second while "Switch profiles by who
    /// is logged in" is on; it forgets who it saw while the switch is off. The window's thread only.</summary>
    private readonly AutoProfile _autoProfile = new();
    /// <summary>True only around a switch AutoProfile asked for, so ProfileSwitched does not count it as the player's own.</summary>
    private bool _switchingProfileAutomatically;
    /// <summary>The question shown when the player switched profiles himself a short while ago; a newer one replaces it.</summary>
    private StartupDialog? _autoProfileQuestion;
    /// <summary>IDEAS #39: the pipe other programs send commands through, while "Take commands from other programs" is on.</summary>
    private CommandServer? _commandServer;
    /// <summary>Plan 39 Ruling 6: the rate limit of the Allow/Deny window (one at a time, a pause after Deny, three per run).</summary>
    private readonly PairingDesk _pairing = new();
    /// <summary>Plan 39 Ruling 5: the Allow/Deny window that is open now; null while none is.</summary>
    private ChoiceDialog? _pairPrompt;
    /// <summary>IDEAS #39: the command this start was given (a shortcut with --profile, say), run once the deferred startup is
    /// done; null without one.</summary>
    private RemoteCommand? _startupCommand;
    /// <summary>IDEAS P130 (b): runs from the first time the start-up command was looked at (a monotonic clock).</summary>
    private Stopwatch? _startupCommandClock;
    /// <summary>What the probe on the process monitor's thread asks Windows: set by LookAtClients on the window's thread
    /// (the settings are read there only), read by ProbeClients.</summary>
    private volatile ClientProbeWants _clientProbeWants;
    // IDEAS P117: the monitor's timer can start a probe while the last one still runs; one at a time, or a pid that never
    // left would be pruned by the other probe's Look and told to the error log again.
    private int _probing;
    /// <summary>IDEAS #22: the process of each client, held only while "Client closed unexpectedly" is on, so its end is read
    /// with its exit code. Tracked and looked at on the probe's pool thread; let go when the event is off and at exit.</summary>
    private readonly ClientExits _clientExits = new(WindowsClientProcess.Open, (pid, error) => LogError(new InvalidOperationException(ClientExits.NotWatchableText(pid, error))));
    /// <summary>IDEAS Plan 20, P119: a crash seen before the straggler alerts exist (the first seconds after start-up) is held
    /// here and told once by the first look after they do. The window's thread only (LookAtClients).</summary>
    private readonly ClientExitBacklog _exitBacklog = new();
    /// <summary>IDEAS #22, ruling 5: the one character a crashed client's alert is raised for, counted as having a window by
    /// the dispatcher's gate. Set and cleared on the window's thread around that one Raise (RaiseClosed).</summary>
    private volatile string? _closingCharacter;
    /// <summary>IDEAS #42: each hotkey Windows refused is told once a session in a tray note; a click on that note opens
    /// the hotkeys page. A click on any other balloon does not: ShowBalloon says which one is up.</summary>
    private readonly RefusalAnnouncer _refusalAnnouncer = new();
    private readonly RefusalNote _refusalNote = new();
    private AlertHistoryWindow? _historyWindow;
    private StatReportWindow? _statReportWindow;   // IDEAS #20: the session reports window, while it is open
    private FleetBoardWindow? _fleetBoardWindow;   // IDEAS #18: the fleet board, while it is open
    private bool _streamerMaskDrawn;   // IDEAS #19: the mode the history window was last drawn for; it is redrawn when this flips
    private Views.HotkeySheetWindow? _hotkeySheet;  // IDEAS #16: the hotkey cheat sheet, while it shows
    private Views.NameBarWindow? _nameBar;  // IDEAS #38: the compact name bar, while NameBarMode is on
    private bool _nameBarFailed;            // IDEAS #38: making it failed; no new try until the switch goes off and on again
    private DispatcherTimer? _gamepadTimer;   // IDEAS #43: exists only while "Switch with a game pad" is on; stopped and dropped otherwise and at exit
    private GamepadPoller? _gamepadPoller;
    private IReadOnlyDictionary<GamepadButton, RemoteCommand> _gamepadBindings = new Dictionary<GamepadButton, RemoteCommand>();
    private const int GamepadPollMs = 50;
    private Views.CommandPaletteWindow? _commandPalette;  // IDEAS #14: the command palette, while it shows
    private CustomAlertStore? _customAlerts;   // IDEAS #8: the player's own alert rules
    private ProcessMonitorService? _processMonitor;
    private CropManager? _cropManager;
    private Window? _settingsWindow;

    // IDEAS #57: where the settings live this run, decided first thing in OnStartup.
    private SettingsHomeResult? _home;
    // IDEAS #55: the marker that tells an unexpected end from a clean exit, and one queued tray tooltip refresh.
    private RunMarker? _runMarker;
    private DateTime? _previousRunStart;
    private int _trayTextQueued;

    // Per-event sound cooldowns — keyed `{character}_{alertType}` so simultaneous
    // alerts on different characters each get their own sound (the cooldown
    // semantics still hold per-character).
    private readonly Dictionary<string, DateTime> _soundCooldowns = new();
    // The table above is read and written on the log reader's thread (an alert's sound) and on the window's thread
    // (a repeat of escalation, the "Left behind" alert). This lock is held round the table alone, one statement at a
    // time: never while a sound is started, never round a call into another class.
    private readonly object _soundCooldownLock = new();

    // Sound player for cycle-wrap chime (single instance is fine here — wraps
    // can't overlap meaningfully).
    private MediaPlayer? _soundPlayer;

    // Active alert MediaPlayer instances — kept alive until MediaEnded fires so a
    // second alert in the same tick can't cancel the first one's playback.
    private readonly object _soundPlayerLock = new();
    private readonly List<MediaPlayer> _activeSoundPlayers = new();

    private bool _isShuttingDown = false;

    // ── Startup perf logging ──
    private static readonly string _perfLogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "evemultipreview_perf.log");
    internal static void PerfLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Debug.WriteLine(line);
        try { System.IO.File.AppendAllText(_perfLogPath, line + Environment.NewLine); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A sign-in launch (Start with Windows, #113) starts in System32, not next to
        // the exe as a double-click does: relative paths keep resolving beside the exe.
        Environment.CurrentDirectory = AppPaths.ExeDir;
        // Single-instance guard
        _singleInstanceMutex = new Mutex(true, "EveMultiPreview_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // We allocated a Mutex object pointing at the existing kernel
            // object but don't own it (Mutex(true, ...) only grants
            // ownership when createdNew is true). Disposing here clears the
            // handle so OnExit's release path can't trip ApplicationException
            // by calling ReleaseMutex on an unowned mutex.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            // IDEAS #39: a second start with a command hands it to the MultiPreview that runs and ends with what came of it
            // as its exit code (0 done, 1 refused, 2 not found, 3 nothing took it). No window for 0 to 2: scripts and a
            // Stream Deck start it, and a window would take the front from the game.
            var forwarded = RemoteCommandLine.Forward(e.Args, command =>
                CommandPipe.Send(CommandPipe.CurrentName(), command, CommandPipe.SendTime, letTakeTheFront: command.TakesTheFront));
            if (forwarded is RemoteOutcome outcome)
            {
                // Plan 39: the pipe also exists with commands off and then refuses the command; hello tells which it was
                if (RemoteCommandLine.SaysCommandsOff(e.Args, outcome, () =>
                        CommandPipe.Query(CommandPipe.CurrentName(), new RemoteCommand(RemoteVerb.Hello, ""), CommandPipe.ConnectTime)))
                    TellCommandsOff();
                Shutdown((int)outcome);
                return;
            }
            TellAlreadyRunning();
            Shutdown();
            return;
        }
        _startupCommand = RemoteCommand.FromArgs(e.Args);   // IDEAS #39: run once startup is done, whatever the switch says

        // IDEAS #57: where the settings live, decided (and on the first start, moved to AppData) before anything writes
        // a file. Only the instance that holds the single-instance lock gets here.
        _home = SettingsHome.Resolve(AppPaths.ExeDir, AppPaths.AppDataDir);
        AppPaths.Use(_home.ConfigDir);
        // A Logs folder that is a link gets no log files: MultiPreview never writes through one.
        if (AppPaths.LogsWritable)
        {
            try { Directory.CreateDirectory(AppPaths.LogsDir); } catch { }
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Path.Combine(AppPaths.LogsDir, "debug_log.txt")));
        }
        System.Diagnostics.Trace.AutoFlush = true;

        // IDEAS #55: a marker the last session left behind means it ended without closing.
        _runMarker = new RunMarker(AppPaths.ConfigDir);
        _previousRunStart = _runMarker.Begin(DateTime.Now);
        RunMarker.PreviousUncleanStart = _previousRunStart;
        SessionEnding += (_, _) => _runMarker?.End();   // Windows is signing out or shutting down: a clean end

        base.OnStartup(e);
        StartWithWindows.RefreshPath();
        // IDEAS P94: the file of the last start goes (an older one carried character names); this start writes counts and timings only.
        PerfLogFile.Reset(_perfLogPath);

        var startupSw = Stopwatch.StartNew();
        PerfLog("🚀 OnStartup entered");

        // ── Global Error Handler ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        ErrorLog.Shared.Changed += QueueTrayText;   // IDEAS #55: the tray tooltip counts handled errors
        SettingsHome.Report(_home, ErrorLog.Shared, DateTime.Now);   // what moved, or why the settings stay beside the exe
        // Counted once (the tray shows it), though with no log file to write it to.
        if (!AppPaths.LogsWritable)
            LogError(new IOException($"{AppPaths.LogsDir} is a link, and MultiPreview never writes through one: no log files this session."));

        // ── JSON Migration ──
        // Not while the missing-file prompt is due: that question comes first, and a settings file made here would hide it.
        if (!StartupSettings.MissingWithCopies(AppPaths.SettingsPath, CopyBesideExe()))
            CheckJsonMigration(AppPaths.ExeDir, AppPaths.SettingsPath);
        PerfLog($"JSON migration check: {startupSw.ElapsedMilliseconds}ms");

        // Reset Settings diagnostic log each run
        try { System.IO.File.WriteAllText(SettingsDiagLogPath, $"--- Session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}"); } catch { }

        // 1. Load settings (from the settings folder, IDEAS #57), with the missing-file prompt before it when the file is
        // gone, then the restore prompt, the newer-file question, the startup copy and the note. Nothing in it can stop
        // startup (StartupSettings contains every throw).
        _settings = new SettingsService(AppPaths.SettingsPath);
        StreamerMask.Follow(_settings);   // IDEAS #19: streamer mode is whatever the saved setting says, asked where a name is drawn
        StartupSettings.Open(_settings, _home, AppPaths.ExeDir, CopyBesideExe(), AskAtStartup, LocalizationService.Str,
            DateTime.Now, LogError, Thread.Sleep,
            // Apply UI language before any window is shown (issue #86). "" auto-detects from the OS; falls back to
            // English for unsupported languages. The look the settings file names (UI styles spec §10) first: it never
            // throws, so a language that fails to apply can't keep the startup questions and the setup wizard from it.
            () => { ApplySavedLook(); Services.LocalizationService.SetLanguage(_settings.Settings.Language); }, MovedNoteText);

        DiagnosticsService.Initialize();
        DiagnosticsService.GlobalSettings = _settings.Settings;

        PerfLog($"Settings loaded: {startupSw.ElapsedMilliseconds}ms");

        // ── Setup Wizard gate ──
        // Not after a load that failed (started with defaults, IDEAS #49).
        if (StartupSettings.NeedsSetupWizard(_settings))
        {
            var wizard = new SetupWizard(_settings);
            wizard.ShowDialog();
            PerfLog($"Setup wizard: {startupSw.ElapsedMilliseconds}ms");
        }

        // 2. Window event hooks (single OS-level subscription shared across services)
        //    and window discovery. Hooks must be installed on the UI thread —
        //    OnStartup runs on it, so create here before anything backgrounds off.
        _winEvents = new WinEventHookService();
        _winEvents.Start();

        _discovery = new WindowDiscoveryService();

        // 3. Create thumbnail manager
        _thumbnailManager = new ThumbnailManager(_discovery, _settings);
        // D6: a thumbnail's "Open ‹character›'s settings" opens settings on that character's page (Plan 4). Deferred, as
        // the tray's Settings item is, so the right-click menu has closed before the window shows.
        _thumbnailManager.CharacterSettingsRequested += name =>
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                OpenSettings(startMinimized: false, pageId: SettingsUi.CharacterPage.IdFor(name))));

        // 4. Start stat tracker (needed before thumbnails fire)
        _statTracker = new StatTrackerService();
        // Wire CSV stat-logging from settings (#settings-audit). SetCsvLogging was never
        // called, so the "Enable Logging" checkbox + dir + retention did nothing. Applied
        // here and again after every settings change (applyLiveSettings, IDEAS P31).
        _statTracker.SetCsvLogging(
            _settings.Settings.StatLoggingEnabled,
            _settings.Settings.StatLogDirectory,
            _settings.Settings.StatLogRetentionDays);
        _statTracker.CountNpcDamage = !_settings.Settings.StatsExcludeNpcDamage;   // IDEAS P32
        _thumbnailManager.SetStatTracker(_statTracker);

        // 4b. Process monitor (CPU/RAM per EVE client)
        _processMonitor = new ProcessMonitorService();
        _thumbnailManager.SetProcessMonitor(_processMonitor);
        _processMonitor.Start();
        // IDEAS #45: the monitor reports every 3 s from its pool thread; the settings are read on the window's thread
        // only, so the look is queued there also while the event is off.
        _processMonitor.StatsUpdated += () => Dispatcher.BeginInvoke(new Action(LookAtMemory), DispatcherPriority.Background);
        // IDEAS #22, #47: the probe asks Windows on the monitor's pool thread (never on the window's thread: the lesson of
        // P46), and the window's thread judges and marks.
        _processMonitor.StatsUpdated += ProbeClients;
        // IDEAS #22: a client MultiPreview ends itself (Close client) raises no "Client closed unexpectedly".
        _thumbnailManager.EndingClient += pid => _clientExits.EndedByMultiPreview(pid);
        _thumbnailManager.CharacterReady += RaiseClientReady;
        // An end Windows refused (EVE run as administrator) is taken back: a later crash of that client is still told.
        _thumbnailManager.ClientEndRefused += pid => _clientExits.NotEndedByMultiPreview(pid);
        _thumbnailManager.EndFrozenClientRequested += EndFrozenClient;   // IDEAS #22
        // IDEAS #10: a layout applied for the monitors does what the layouts editor's Apply does: the crop windows follow it
        // (a layout saved with visibility turns them on or off) and the profile remembers its name (#94).
        _thumbnailManager.MonitorLayoutApplied += name =>
        {
            _cropManager?.Refresh();
            if (_settings == null) return;
            _settings.CurrentProfile.AppliedLayoutPreset = name;
            _settings.Save();
        };

        // 4c. Crop manager (spawns per-character cropped DWM popups)
        _cropManager = new CropManager(_discovery, _settings);
        _cropManager.AttachThumbnailManager(_thumbnailManager);
        if (_winEvents != null) _cropManager.AttachWinEvents(_winEvents);
        // Apply a profile switch from ONE place, no matter who triggered it (#101).
        // SwitchProfile raises this for the tray menu, the cycle hotkey and the
        // Settings dropdown alike. Previously each caller re-applied by hand and the
        // tray menu's copy forgot the crop refresh, so switching profiles from the
        // tray left the old profile's crops on screen. (The Settings window's own
        // handler covered it, but only while that window was open — which is why it
        // "worked" from inside the app and not from the tray.)
        _settings.ProfileSwitched += name =>
        {
            _thumbnailManager?.ReapplySettings();
            if (_settings.Settings.ClientPositionMode == 3)
                _thumbnailManager?.ApplyAllClientSlots();
            else if (_settings.Settings.TrackClientPositions)
                _thumbnailManager?.RestoreAllClientPositions();
            // IDEAS #31: a group's members can differ per profile, so for whom an event is on is worked out again.
            ApplyAlertSwitches();
            // And so is whether anything has to look once a second: "Left behind" may be on only for a group that
            // has members in the profile now in use. After the switches: the timer asks what they handed out.
            FollowAlertTimer();
            // Recovery net (issue #81): never carry a stuck crops-hidden state across a switch.
            _cropManager?.ShowCrops();
            _cropManager?.Refresh();
            _hotkeyService?.RegisterFromSettings(
                _settings.Settings, _settings.CurrentProfile,
                _thumbnailManager!, OpenSettings);
            Debug.WriteLine($"[App:Profile] 🔄 Applied profile: {name}");
        };
        // IDEAS #11: a switch the player made (the tray, a hotkey, the settings window, a command) makes the automatic one
        // ask first for a while; the automatic one is not such a switch.
        _settings.ProfileSwitched += _ => { if (!_switchingProfileAutomatically) _autoProfile.ManualSwitch(DateTime.UtcNow); };

        PerfLog($"Core services created: {startupSw.ElapsedMilliseconds}ms");

        // ── START DISCOVERY IMMEDIATELY — thumbnails appear ASAP ──
        _discovery.Start(_winEvents);
        _thumbnailManager.StartFocusTracking(_winEvents);
        PerfLog($"Discovery + FocusTracking started: {startupSw.ElapsedMilliseconds}ms");

        // ── DEFER slower startup tasks so thumbnail BeginInvoke runs first ──
        var deferTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        deferTimer.Tick += (_, _) =>
        {
            deferTimer.Stop();
            var deferSw = Stopwatch.StartNew();

            // IDEAS P93: each section below is a step of its own. One that throws is reported by its type and costs only
            // itself, not the alert handler, the log monitor, the tray or the hub after it. The order is the old one.
            // (The sections keep their indentation, so that the text the older plans' tests look for keeps its shape.)
            void Step(string name, Action run) => StartupSteps.Run(name, run, LogError);
            AlertTargets alertTargets = null!;   // made by the "Alert ways" step, read by the steps after it
            // A step reads what an earlier step made (the hub, the log monitor). If that step failed, this one throws
            // a NullReferenceException of its own and reports it of its own; the compiler can't follow it across steps.
#pragma warning disable CS8602, CS8604

            Step("Hotkeys", () =>
            {
            // 5. Initialize hotkey service
            _hotkeyService = new HotkeyService();
            _hotkeyService.Initialize();
            // Foreground-gated registration so EVE-only hotkeys pass through to other
            // apps when EVE isn't in front (issue #68).
            if (_winEvents != null)
                _hotkeyService.AttachWinEvents(_winEvents,
                    () => _thumbnailManager?.HasTrackedClients() == true);
            var profile = _settings.CurrentProfile;
            PerfLog(PerfLogFile.HotkeyLine(_settings.Settings, profile));   // IDEAS P94: counts, never whose
            _hotkeyService.RegisterFromSettings(
                _settings.Settings, profile,
                _thumbnailManager, OpenSettings);
            PerfLog($"[Deferred] Hotkeys registered: {deferSw.ElapsedMilliseconds}ms");
            });

            Step("Alert hub", () =>
            {
            // 6. Create alert hub
            _alertHub = new AlertHub(_settings.Settings);
            _alertHub.FocusCharacterRequested += (charName) =>
            {
                Debug.WriteLine($"[AlertHub:Focus] 🎯 Focus requested for '{charName}'");
                _thumbnailManager.ActivateEveWindow(IntPtr.Zero, charName);
            };
            _alertHub.SaveRequested += () => _settings.SaveDelayed();
            _alertHub.SystemLookup = _thumbnailManager.GetCharacterSystem;   // "Jita · click to switch" on toasts (Plan 4)
            // Toasts pause with the hotkeys on every path: the pause hotkey, Home and the tray (IDEAS P37).
            _alertHub.FollowPauseOf(_hotkeyService);
            // Deferred, as the tray's items are: the hub's menu has closed before the window shows.
            _alertHub.HistoryRequested += () => Application.Current?.Dispatcher.BeginInvoke(new Action(OpenAlertHistory));
            // IDEAS #30: a toast's menu snoozes its event for its character, and says so near the cursor. The thumbnails
            // and the settings window list what is snoozed and end it. The timed snoozes are read from their own file
            // only when the player turned "Keep snoozes" on, and never in a session whose settings could not be read.
            _alertHub.SnoozeRequested += (charName, alertType, length) =>
            {
                SnoozeEvent(charName, alertType, length);
                _thumbnailManager.ShowTooltipFeedback(SnoozeText.Done(charName, alertType));
            };
            _thumbnailManager.Snoozes = _snoozes;
            _snoozeFile = new EventSnoozeFile(AppPaths.ConfigDir, ErrorLog.Shared);
            if (_settings.LoadedSuccessfully && _settings.Settings.KeepAlertSnoozes) _snoozeFile.Load(_snoozes);
            _snoozes.Changed += FollowSnoozeSwitch;
            PerfLog($"[Deferred] AlertHub created: {deferSw.ElapsedMilliseconds}ms");
            });

            Step("Log monitor", () =>
            {
            // 7. Start log monitor and wire events
            _logMonitor = new LogMonitorService();
            _logMonitor.IsOnFor = _alertProfiles.IsOn;   // IDEAS P88: an event off for the character's group uses up none of the quiet time
            _logMonitor.CanReach = (who, type) => alertTargets == null || AlertDispatcher.CanReach(alertTargets, who, type);   // P67: a muted, snoozed or window-less listener does not claim a line
            _logMonitor.PveMode = _settings.Settings.PveMode;
            _logMonitor.SetCooldown(_settings.Settings.AlertCooldown);
            _logMonitor.SetSettings(_settings.Settings);

            ApplyAlertSwitches();   // IDEAS #31: which events are on, for the log monitor and for the gate
            if (_settings.Settings.SeverityCooldowns != null)
                _logMonitor.SetEventCooldowns(_settings.Settings.SeverityCooldowns);
            _logMonitor.SetSeverityCooldowns(LogMonitorService.SeverityWaits(_settings.Settings));   // IDEAS P28

            // ── Damage received (incoming) → stat tracker + alert ──
            _logMonitor.DamageReceived += (dmg) =>
            {
                _statTracker.RecordDamage(dmg.CharacterName, dmg.Amount, true, dmg.IsNpc, damageType: dmg.Type);
                _thumbnailManager?.SignalUnderFire(dmg.CharacterName);
                Debug.WriteLine($"[App:Alert] 🔴 Damage received: {dmg.Amount} from '{dmg.SourceName}' to '{dmg.CharacterName}' (NPC={dmg.IsNpc})");
                _idleWatch.Active(dmg.CharacterName, DateTime.UtcNow);   // IDEAS #7
            };
            _logMonitor.DamageDealt += (dmg) =>
            {
                _statTracker.RecordDamage(dmg.CharacterName, dmg.Amount, false, dmg.IsNpc);
                _idleWatch.Active(dmg.CharacterName, DateTime.UtcNow);   // IDEAS #7
            };
            _logMonitor.MiningYield += (mining) =>
            {
                _statTracker.RecordMining(mining.CharacterName, mining.Amount, mining.MineType);
                _idleWatch.Active(mining.CharacterName, DateTime.UtcNow);   // IDEAS #7
            };
            _logMonitor.RepairReceived += (repair) =>
            {
                _statTracker.RecordRepair(repair.CharacterName, repair.Amount, repair.IsIncoming, repair.RepairType);
                _idleWatch.Active(repair.CharacterName, DateTime.UtcNow);   // IDEAS #7
            };
            _logMonitor.BountyReceived += (bounty) =>
            {
                _statTracker.RecordBounty(bounty.CharacterName, bounty.Amount);
                _idleWatch.Active(bounty.CharacterName, DateTime.UtcNow);   // IDEAS #7
            };
            _logMonitor.SystemChanged += (charName, systemName) =>
            {
                Debug.WriteLine($"[App:Alert] 🌍 System change: '{charName}' → '{systemName}'");
                // IDEAS #5: every move, whether the event is on or not. First: a line below that throws is recorded by the log
                // reader and ends this handler, and it must not cost the watch a move.
                _stragglers.Moved(charName, systemName, DateTime.UtcNow);
                _idleWatch.Moved(charName, DateTime.UtcNow);   // IDEAS #7: a jump into another system starts the count again
                _thumbnailManager.UpdateCharacterSystem(charName, systemName);
            };
            // IDEAS P10: the jump count for {jumps}. Its own event, raised before SystemChanged, because it needs where the
            // character was (a late login has no first sighting here) and whether this is only a re-statement (a backfill).
            _logMonitor.SystemMoved += (charName, from, to, backfill) => _thumbnailManager.Jumps.Moved(charName, to, from, backfill);

            // Cycle-wrap sound (issue #24) — plays whenever a cycle hotkey rolls
            // from the last client back to the first (or vice-versa reversing).
            _thumbnailManager.CycleWrapped += () =>
            {
                var s = _settings?.Settings;
                if (s == null || !s.CycleWrapSoundEnabled) return;
                // IDEAS P76: no alert, and started by MultiPreview on its own: do-not-disturb holds it, critical alerts
                // or not (so the property, not Silences, which lets a critical alert through).
                if (DoNotDisturbOn) return;
                var wrap = AlertSoundChoice.WrapSound(s);
                if (wrap.Length == 0 || !System.IO.File.Exists(wrap)) return;
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        _soundPlayer?.Open(new Uri(wrap));
                        _soundPlayer!.Volume = s.AlertSoundVolume / 100.0;
                        _soundPlayer.Play();
                    });
                    Debug.WriteLine($"[CycleWrap:Sound] 🔊 Playing '{System.IO.Path.GetFileName(wrap)}'");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CycleWrap:Sound] ❌ {ex.Message}");
                }
            };
            });

            Step("Alert ways", () =>
            {
            // What an alert is shown with. AlertDispatcher makes the decision this handler used to make inline (the same
            // checks in the same order, the same calls) and says what became of the alert (IDEAS #1).
            alertTargets = new AlertTargets
            {
                // IDEAS #31: asked first. An event that is off for this character (its group's choice) is no alert for it.
                IsOn = (charName, alertType) => _alertProfiles.IsOn(charName, alertType),
                // IDEAS #22: the window of a crashed client is gone; while its alert is raised, its character counts.
                ActiveCharacters = () => ClientExits.WithClosing(_thumbnailManager.GetActiveCharacterNames(), _closingCharacter),
                IsMuted = charName => _thumbnailManager.IsCharacterAlertMuted(charName),
                IsSnoozed = (charName, alertType) => _snoozes.IsSnoozed(charName, alertType),
                Flash = (charName, severity, alertType) => _thumbnailManager.SetAlertFlash(charName, severity, alertType),
                Badge = (charName, severity, alertType) => _thumbnailManager.IncrementAlertBadge(charName, severity, alertType),
                Unseen = (charName, alertType, severity) => _thumbnailManager.NoteUnseenAlert(charName, alertType, severity),
                WindowOf = charName => _thumbnailManager.GetHwndForCharacter(charName),
                ForegroundWindow = () => EveMultiPreview.Interop.User32.GetForegroundWindow(),
                Toast = (charName, alertType, severity, soundHeld) => _alertHub.ShowToast(charName, alertType, severity, soundHeld),
                // P81: asked once per alert, before its toast and its sound; the toast's crossed bell and the history's
                // mark come from this one answer. PlayAlertSound keeps its own gate, in the order it always had: the
                // master switch, then do-not-disturb. With the sounds off nothing is held by do-not-disturb: nothing would play.
                SoundHeld = severity => _settings.Settings.EnableAlertSounds && _doNotDisturb.Silences(_settings.Settings, severity, DateTime.Now),
                Sound = PlayAlertSound,
                // P90: a toast or a sound that threw costs the alert nothing else; only its type is written, never a message.
                Failed = (way, ex) => ErrorLog.Shared.Record(new InvalidOperationException($"An alert's {way} failed: " + ex.GetType().Name), "AlertDispatcher", DateTime.Now),
                // IDEAS batch 6: after every gate. Which window is in front is asked there once, and only while a
                // further way is switched on. It is asked a moment after Unseen asked it (the toast and the sound are
                // only posted to the window's thread, P57, so that moment is short), and the player may have switched away in that moment: an alert
                // that was seen as it arrived is held by nobody as unseen, so no look would ever take its Windows
                // notification away again. "In front" is therefore also: the attention table holds nothing for it.
                Ways = (charName, alertType, severity, raisedUtc) => _alertWays.Raised(_settings.Settings, charName, alertType, severity,
                    () => ThumbnailManager.InFront(_thumbnailManager.GetHwndForCharacter(charName), EveMultiPreview.Interop.User32.GetForegroundWindow())
                        || !_thumbnailManager.Attention.Has(charName), raisedUtc,
                        clientGone: alertType == ClientWatch.ClosedEvent && ClientExits.IsClosing(_closingCharacter, charName)),
            };
            // IDEAS #26: spoken alerts. The way is told of every alert that was shown, decides what is said, and
            // says it on a thread of its own. Speech is a sound: do-not-disturb is asked for it as for the sound.
            // The last word of a chain of repeats (IDEAS #29) is spoken once more.
            // A sentence may wait ten seconds for its turn, and then the way asks whether each of its alerts is
            // still wanted: not when the character is muted or the event snoozed for it meanwhile, and not when
            // the player has looked at that client since. An alert on the client in front is not spoken at all
            // (FY-n1; its sound plays). Asked on the voice's thread: all three answers may be asked there.
            // A look at a client forgets what the voice holds against saying its alerts again (FY-n2).
            // FY-m3: the three ways are made in one try. A failure is reported by its type, and the start-up goes on to
            // the alert handler, the monitor, the tray and the hub: a way that could not be made is simply not there.
            try
            {
            _speaker = new WindowsSpeaker();
            _spokenWay = new SpokenWay(_speaker, () => _settings.Settings,
                (s, severity) => _doNotDisturb.Silences(s, severity, DateTime.Now),
                AlertHub.EventDisplayName, LocalizationService.Str, () => LocalizationService.CurrentLanguage, LogError,
                stillWanted: alert => !_thumbnailManager.IsCharacterAlertMuted(alert.Character) && !_snoozes.IsSnoozed(alert.Character, alert.EventKey)
                    && AlertWays.StillUnseen(alert, _thumbnailManager.Attention.Has));
            _alertWays.Add(_spokenWay);
            _thumbnailManager.Attention.Looked += _spokenWay.Looked;
            _spokenWay.Follow(_settings.Settings.SpeakAlerts);
            _escalation.HandOff = alert => { _spokenWay?.HandedOff(alert); _pushWay?.HandedOff(alert); };
            // IDEAS #33: Windows notifications. The way is told of every alert that got past the gates and of every
            // client that was looked at; a click on a notification comes back on a thread of Windows' and is done on
            // the window's: "Switch to" as a toast's click does it, "Snooze" as a toast's menu does it
            // (NotificationActions has both, and the hop). The registry key follows the switch, now and after every
            // settings change. A notification names where a character is only when that is the alert itself ("Left
            // behind": where the others went): it is seen over any program and on the lock screen.
            _notificationActions = new NotificationActions(() => _isShuttingDown, () => _settings.LoadedSuccessfully,
                () => _settings.Settings.WindowsNotifications, SwitchToCharacter,
                (who, type, length) => SnoozeEvent(who, type, length), _thumbnailManager.ShowTooltipFeedback,
                action => Dispatcher.BeginInvoke(action), LocalizationService.Str);
            _notificationRegistration = new NotificationRegistration(new UserRegistry(), () => NotificationIcon.Ensure(AppPaths.ConfigDir), LogError,
                () => NotificationIcon.Remove(AppPaths.ConfigDir));
            _notificationsWay = new NotificationsWay(new WindowsToastNotifier(NotificationRegistration.AppId),
                entry => NotificationsWay.Texts(entry, AlertHub.EventDisplayName, (who, type) => _stragglerAlerts?.PlaceOf(who, type), LocalizationService.Str),
                _notificationActions.SwitchTo, _notificationActions.Snooze, _notificationActions.OnWindowThread, LogError);
            _alertWays.Add(_notificationsWay);
            _thumbnailManager.Attention.Looked += _notificationsWay.Looked;
            _thumbnailManager.AlertMuted += _notificationsWay.Muted;
            FollowNotifications(starting: true);
            // IDEAS #2: alerts to a Discord webhook, which the Discord app on a phone shows. The link is read from the
            // settings file, encrypted for this Windows user, and goes nowhere but discord.com. The way asks
            // do-not-disturb as the sound does, and "away from the PC" by when Windows last saw input.
            _pushWay = new PushWay(new DiscordSender(DiscordSender.RealHandler()), stored => WebhookSecret.Unprotect(stored),
                (s, severity) => _doNotDisturb.Silences(s, severity, DateTime.Now),
                () => PushWay.Idle(Environment.TickCount, EveMultiPreview.Interop.User32.LastInputTick()),
                AlertHub.EventDisplayName, LocalizationService.Str, failed: LogError);
            _pushWay.Follow(_settings.Settings);
            _alertWays.Add(_pushWay);
            // IDEAS #13: a thumbnail grows for a few seconds on an alert. The manager owns the decision and its tick.
            _alertWays.Add(_thumbnailManager.Enlarge);
            }
            catch (Exception ex)
            {
                LogError(AlertWays.Failure("Alert ways: making them at start-up", ex));
            }
            });

            Step("Alert history", () =>
            {
            // IDEAS #1: every alert that got past its switch and its quiet time is kept, with what became of it. The
            // history writes on a thread of its own; old days are deleted here, at startup, and never while running.
            // A session whose settings could not be read runs on default values and saves nothing: its keep time is
            // not the player's, so it deletes no old day, and it records nothing.
            _alertHistory = new AlertHistory(AppPaths.HistoryDir, ErrorLog.Shared)
            {
                Enabled = _settings.LoadedSuccessfully && _settings.Settings.AlertHistoryEnabled,
                KeepDays = _settings.Settings.AlertHistoryDays,
            };
            if (_settings.LoadedSuccessfully) _alertHistory.DeleteOldDays();
            // IDEAS #5: "Left behind" is looked for and raised by a class of its own, which is handed App's parts: the
            // watch, the settings, the characters that have a client, where the log monitor has each, what an alert
            // is shown with, and the history. Its toast says where the others went ("Jita → Perimeter 3/4") in the
            // system's place: the hub asks that class.
            _stragglerAlerts = new StragglerAlerts(_stragglers, () => _settings.Settings, () => _thumbnailManager.GetActiveCharacterNames(),
                _logMonitor.GetCharacterSystem, alertTargets, _alertHistory, LogError, LeftBehindOn);
            _alertHub.PlaceLookup = _stragglerAlerts.PlaceOf;
            // IDEAS #17: "Go to the last alert" walks back through the history's memory list once nothing is unseen.
            _thumbnailManager.RecentAlerts = () => _alertHistory.Recent();
            // Windows signing out or shutting down: WPF only queues its shutdown then, and OnExit may not get to run
            // before the process is ended. A settings change still waiting for its delayed save (a position moved a
            // moment ago) is written first, and only written, not let go of by Dispose: OnExit still does that if it gets to
            // run. WPF ends the app as soon as Windows asks the session to end (WM_QUERYENDSESSION) and nothing here vetoes
            // it, so a shutdown that is cancelled afterwards leaves MultiPreview closed (P95, P96).
            SessionEnding += (_, _) => _settings?.Flush();
            // Then what the history has waiting is written, for two seconds at most.
            SessionEnding += (_, _) => _alertHistory?.Flush(TimeSpan.FromSeconds(2));
            // And what this session showed would stay in the Notification Center with buttons that can do nothing: it
            // is taken away before the app closes (WPF closes it, as nothing here cancels the session's end). Behind
            // the history (FY-m1): the player's data first, because Windows may take seconds to answer.
            SessionEnding += (_, _) => _notificationsWay?.SessionEnding();
            });

            Step("Alert handler", () =>
            {
            _logMonitor.AlertTriggered += (charName, alertType, severity) =>
            {
                // What a custom rule matched is there only while its alert is being raised: taken first (IDEAS #8).
                // The rule's name is taken with it: the toast and the sound are posted to the window's thread and show
                // or play later, and the player may rename or delete the rule meanwhile. The history keeps the name the alert was
                // raised under. For a built-in alert both are null.
                var matched = _logMonitor.RaisingCustomText;
                var rule = CustomAlertTypes.Find(alertType)?.Name;
                // So is the time: the entry says when the alert was raised, not when the window's thread got to it.
                // The dispatcher hands the same time on to the further ways (AlertNotice.RaisedUtc, IDEAS batch 6).
                var raised = DateTime.UtcNow;
                var outcome = AlertDispatcher.Dispatch(_settings.Settings, alertTargets, charName, alertType, severity, raised, out bool soundHeld);
                // With the history switched off no entry is made at all. (Record asks for itself as well.)
                // Neither is one for an event that is off for this character (IDEAS #31): it was no alert for it.
                if (outcome == AlertOutcome.Off) return;
                if (!_alertHistory.Enabled) return;
                _alertHistory.Record(AlertHistory.Entry(raised, charName, alertType, severity,
                    _logMonitor.GetCharacterSystem(charName), outcome, rule, matched, soundHeld));
            };
            _logMonitor.AlertTriggered += (charName, alertType, _) => { if (_alertProfiles.IsOn(charName, alertType)) _idleWatch.Alerted(charName, alertType, DateTime.UtcNow); };   // IDEAS #7: fleet warps, and stops Miner stopped already told; only an alert this character's group has on
            });

            Step("Custom alert rules", () =>
            {
            // IDEAS #8: the player's own alert rules, from custom_alerts.json in the settings folder. A file that can't
            // be used is set aside and reported, and MultiPreview runs without rules. The log monitor gets the rules
            // that are on before it starts reading, and again whenever the rules are saved.
            _customAlerts = new CustomAlertStore(AppPaths.ConfigDir, ErrorLog.Shared);
            _customAlerts.Load();
            _customAlerts.Changed += ApplyCustomRules;
            ApplyCustomRules();
            });

            Step("Alert timer", () =>
            {
            // IDEAS #29: one timer for everything that has to look once a second. It runs only while something needs it.
            _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _alertTimer.Tick += (_, _) => AlertTick();
            FollowAlertTimer();

            _discovery.WindowTitleChanged += (window, oldTitle) =>
            {
                if (!string.IsNullOrEmpty(window.CharacterName))
                {
                    var refreshTimer2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    refreshTimer2.Tick += (_, _) =>
                    {
                        refreshTimer2.Stop();
                        _logMonitor.Refresh();
                    };
                    refreshTimer2.Start();
                }
            };
            });

            Step("Log monitor start", () =>
            {
            var chatLogDir = _settings.Settings.ChatLogDirectory;
            var gameLogDir = _settings.Settings.GameLogDirectory;
            _logMonitor.Start("", chatLogDir, gameLogDir,
                _settings.Settings.EnableChatLogMonitoring,
                _settings.Settings.EnableGameLogMonitoring);
            PerfLog($"[Deferred] LogMonitor started: {deferSw.ElapsedMilliseconds}ms");
            });

            Step("Tray icon", () =>
            {
            // 8. Set up system tray
            SetupTrayIcon();
            PerfLog($"[Deferred] Tray icon: {deferSw.ElapsedMilliseconds}ms");
            });

            Step("Alert hub show", () =>
            {
            _alertHub.Show();
            PerfLog($"[Deferred] AlertHub.Show: {deferSw.ElapsedMilliseconds}ms");
            });

            Step("Hotkey events", () =>
            {
            // Wire suspend icon toggle
            _hotkeyService.SuspendToggled += () =>
            {
                if (_trayIcon != null)
                    _trayIcon.Text = TrayMenu.TooltipText(_hotkeyService.IsSuspended, ErrorLog.Shared.Count, DoNotDisturbOn, _focusModeOn);
                ApplyTrayIcon();
                _thumbnailManager?.ShowSuspendTooltip(_hotkeyService.IsSuspended);
            };
            _hotkeyService.ProfileCycleForward += () => CycleProfile(forward: true);
            _hotkeyService.ProfileCycleBackward += () => CycleProfile(forward: false);
            _hotkeyService.DoNotDisturbToggled += ToggleDoNotDisturb;   // IDEAS #32
            _hotkeyService.HotkeySheetToggled += ToggleHotkeySheet;   // IDEAS #16
            _hotkeyService.FocusModeToggled += ToggleFocusMode;   // IDEAS #35
            _hotkeyService.CommandPaletteRequested += ToggleCommandPalette;   // IDEAS #14
            // IDEAS #42: said after the registration is done, not from inside it. The pause key was registered before
            // the tray icon existed, and the other keys are asked for within the next half second. Told at once, the
            // pause key's note would be replaced by the note about the others: one second from now, one note names
            // every key that is refused by then.
            _hotkeyService.Refused.Changed += () => Dispatcher.BeginInvoke(new Action(TellRefusedHotkeys));
            var refusedAtStartup = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            refusedAtStartup.Tick += (_, _) =>
            {
                refusedAtStartup.Stop();
                TellRefusedHotkeys();
            };
            refusedAtStartup.Start();
            });

            Step("Sound player and presence poll", () =>
            {
            // Initialize sound player
            _soundPlayer = new MediaPlayer();

            // ── EVE window presence → hotkey activate/deactivate ──
            // Unregisters hotkeys from OS when no EVE windows are open so keys work
            // normally in other apps; re-registers them when EVE windows appear.
            var hotkeyToggleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            bool _lastHotkeyToggleState = false;
            hotkeyToggleTimer.Tick += (_, _) =>
            {
                bool hasWindows = _thumbnailManager?.HasTrackedClients() == true;
                if (hasWindows != _lastHotkeyToggleState)
                {
                    PerfLog($"[Hotkey:Toggle] EVE windows present: {hasWindows} → re-evaluate registration");
                    _lastHotkeyToggleState = hasWindows;
                }
                // Safety-net poll. EvaluateRegistration also factors in EVE-only
                // scope + current foreground (issue #68); foreground-change events
                // drive the responsive path, this catches anything they miss.
                _hotkeyService?.EvaluateRegistration();
                FollowDoNotDisturb();
                FollowKeepAwake();
                FollowDowntime();
                FollowTranquility();
                FollowIdle();
                FollowFocusMode();
                FollowAutoProfile();
                FollowNameBar();          // IDEAS #38
                FollowStartupCommand();   // IDEAS P130 (b)
            };
            hotkeyToggleTimer.Start();
            });

            PerfLog($"[Deferred] ✅ All deferred startup complete: {deferSw.ElapsedMilliseconds}ms total");

            // IDEAS #55: once, after a session that ended without closing. (Plan 10 guards the call itself; this step guards
            // the line above it from the one below.)
            Step("Unclean exit notice", () =>
            {
            if (_previousRunStart is DateTime previousStart) ShowUncleanExitNotice(previousStart);
            });

            // IDEAS #39: the command this start was given, now that everything runs.
            Step("Start-up command", () =>
            {
            if (StartupCommandWaits()) return;   // IDEAS P130 (b): a --focus waits for discovery; the 500 ms tick below asks again
            if (_startupCommand is RemoteCommand first) { _startupCommand = null; RunCommand(first); }
            });

            // IDEAS P60 (4)(5): an update that was started and did not happen is told once, and a downgrade that failed gives
            // the update check back. After the tray icon exists; the marker is deleted every time it is read.
            Step("Update marker", () =>
            {
            var update = PendingUpdate.Settle(AppPaths.ConfigDir, SettingsUi.AboutActions.VersionText, DateTime.UtcNow, _settings.Settings, _settings.LoadedSuccessfully, _settings.Save);
            if (update.Verdict == PendingUpdate.Verdict.Failed)
                ShowBalloon(10000, PendingUpdate.Note(update, LocalizationService.Str), ToolTipIcon.Warning);
            });
#pragma warning restore CS8602, CS8604

            // ── Auto-Update Check (fire-and-forget, non-blocking) ──
            // Gated on CheckForUpdatesOnStartup (About tab toggle). When off, no
            // network call and no popup — users can still check via Settings → About.
            if (_settings?.Settings?.CheckForUpdatesOnStartup ?? true)
            _ = Task.Run(async () =>
            {
                try
                {
                    var updateService = new UpdateService();
                    bool allowPreRelease = _settings?.Settings?.ReceivePreReleaseUpdates ?? false;
                    bool hasUpdate = await updateService.CheckForUpdateAsync(allowPreRelease);
                    if (hasUpdate)
                    {
                        PerfLog($"[Update] ⬆ Update available: v{updateService.LatestVersion}");
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            // Non-modal: a modal ShowDialog() runs a nested message loop
                            // that swallows global hotkeys until dismissed. Show() lets the
                            // user keep playing while the "update available" prompt sits open,
                            // and it no longer takes focus from the game (Plan 4).
                            var dialog = UpdateDialog.ForStartupCheck(updateService);
                            dialog.Show();
                        });
                    }
                    else
                    {
                        PerfLog($"[Update] ✅ Up to date (v{updateService.CurrentVersion})");
                    }
                }
                catch (Exception ex)
                {
                    PerfLog($"[Update] ⚠ Auto-check failed (non-fatal): {ex.GetType().Name}");
                }
            });
        };
        deferTimer.Start();
        FollowCommandServer();   // IDEAS #39
        FollowGamepad();         // IDEAS #43

        PerfLog($"✅ OnStartup complete (discovery running): {startupSw.ElapsedMilliseconds}ms total");

        // ── Reopen settings after Apply reload (AHK: reopen_settings.flag) ──
        var reopenFlag = Path.Combine(Path.GetTempPath(), "evemultipreview_reopen_settings.flag");
        bool reopenAfterApply = File.Exists(reopenFlag);
        if (reopenAfterApply)
        {
            try { File.Delete(reopenFlag); } catch { }
            var reopenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            reopenTimer.Tick += (_, _) =>
            {
                reopenTimer.Stop();
                OpenSettings();
                Debug.WriteLine("[App:Startup] 🔧 Settings reopened after Apply");
            };
            reopenTimer.Start();
        }

        // ── Auto-open Settings on launch (user preference, StartupSettings) ──
        // Skipped on Apply-reload (the reopen-flag path above already handles that case)
        // and skipped while the Setup Wizard is still needed.
        var startupMode = _settings.Settings.StartupSettings;
        if (!reopenAfterApply && _settings.Settings.SetupCompleted
            && startupMode != EveMultiPreview.Models.StartupSettingsMode.Off)
        {
            var startupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            startupTimer.Tick += (_, _) =>
            {
                startupTimer.Stop();
                OpenSettings(startMinimized: startupMode == EveMultiPreview.Models.StartupSettingsMode.OpenMinimized);
                Debug.WriteLine($"[App:Startup] 🪟 Settings auto-opened (mode={startupMode})");
            };
            startupTimer.Start();
        }

        Debug.WriteLine("[App:Startup] ✅ All services started successfully");
    }

    // ── Error Handler (AHK: Global error handler in Main.ahk) ─────────

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // DWM race conditions (matches AHK behavior): still handled quietly, but now counted and written to the error
        // log once per kind each session (IDEAS #55), so they no longer vanish.
        if (e.Exception is System.Runtime.InteropServices.COMException ||
            e.Exception.Message.Contains("Missing a required parameter") ||
            e.Exception is System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"[App:Error] ⚠ Quietly handled: {ErrorLog.Describe(e.Exception)}");
            ErrorLog.Shared.Record(e.Exception, "UI (window race)", DateTime.Now);
            e.Handled = true;
            return;
        }

        // Log all other errors
        LogError(e.Exception);
        e.Handled = true; // Keep running
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // The process is ending: written here, and the run marker stays, so the next start says so (IDEAS #55).
        if (e.ExceptionObject is Exception ex)
            ErrorLog.Shared.RecordCrash(ex, DateTime.Now);   // always written, whatever the session logged before
    }

    /// <summary>Shows the look the settings file names (UI styles spec §10). The same look again changes nothing. A window
    /// that failed to take it is logged; every other window, the menus and the hub have it (UiStyles.Set finishes the
    /// switch before it throws).</summary>
    private void ApplySavedLook()
    {
        try
        {
            // UI styles spec §12: High contrast in Windows' colours while a contrast theme is on, if the player follows it.
            SettingsUi.UiStyles.ListenToWindows();
            SettingsUi.UiStyles.FollowsContrast = _settings!.Settings.UiStyleFollowContrast;
            SettingsUi.UiStyles.FollowsWindowsTheme = _settings!.Settings.UiStyleFollowWindows;   // IDEAS P3: before the look is shown
            SettingsUi.UiStyles.Set(_settings!.Settings.UiStyle);
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>An error MultiPreview handled and kept running after: counted, and written to Logs\error_log.txt in the
    /// settings folder (IDEAS #55). Never throws.</summary>
    internal static void LogError(Exception ex)
    {
        ErrorLog.Shared.Record(ex, "App", DateTime.Now);
        Debug.WriteLine($"[App:Error] ❌ Logged error: {ErrorLog.Describe(ex)}");
    }

    // ── JSON Migration (AHK: EVE-X-Preview → EVE MultiPreview) ───────

    /// <summary>EVE-X-Preview's file beside the exe becomes the settings file in the settings folder (IDEAS #57), and
    /// only while that folder has none: after the move, the file beside the exe is not the one MultiPreview reads.</summary>
    private static void CheckJsonMigration(string exeDir, string newFile)
    {
        var oldFile = Path.Combine(exeDir, "EVE-X-Preview.json");

        if (File.Exists(oldFile) && !File.Exists(newFile))
        {
            UseWindowsLanguage();   // no settings are read yet, so no language is chosen yet
            var result = MessageBox.Show(
                LocalizationService.Str("L.Msg.MigrateEveX", "Found settings from EVE-X-Preview. Migrate to EVE MultiPreview?"),
                LocalizationService.Str("L.Msg.MigrateEveXTitle", "Settings Migration"), MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    SettingsHome.CopyNoFollow(oldFile, newFile, overwrite: false);   // never over a settings file, never through a link
                    Debug.WriteLine("[App:Migration] ✅ Migrated EVE-X-Preview.json → EVE MultiPreview.json");
                }
                catch (Exception ex)
                {
                    LogError(ex);   // counted and in the error log: the player said yes, and starts on defaults instead
                }
            }
        }
    }

    // ── Settings folder, backups and errors (IDEAS #49, #55, #57) ──

    /// <summary>A startup question (the missing-file and restore prompts, the newer-file question): before the main UI,
    /// so modal.</summary>
    private static int? AskAtStartup(string title, string body, IReadOnlyList<string> choices)
    {
        var dialog = new StartupDialog(title, body, choices);
        dialog.ShowDialog();
        return dialog.Choice;
    }

    /// <summary>The settings file beside the exe, when the settings folder is elsewhere: the copy from before the move,
    /// which the restore prompt also offers. Null when portable.</summary>
    private static string? CopyBesideExe() =>
        string.Equals(Path.GetFullPath(AppPaths.ConfigDir), Path.GetFullPath(AppPaths.ExeDir), StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.Combine(AppPaths.ExeDir, AppPaths.SettingsFileName);

    /// <summary>The note left beside the exe when the settings moved, in the player's language.</summary>
    private static string MovedNoteText() => string.Format(
        LocalizationService.Str("L.Move.Note", MovedNoteEnglish),
        AppPaths.ConfigDir, AppPaths.PortableMarkerName, SettingsUi.AboutActions.VersionText);

    private const string MovedNoteEnglish =
        "EVE MultiPreview now keeps its settings in this folder:\n{0}\n\n"
        + "The settings file here, beside MultiPreview.exe, is the copy from before version {2}. MultiPreview doesn't use it "
        + "any more, apart from the older-version case below, and you can keep it as a backup. If the settings file above "
        + "ever goes missing, MultiPreview asks on the next start which copy to restore: one of its newer backups, or this "
        + "file. If it has no newer backup, it copies this file back without asking.\n\n"
        + "To keep everything beside the exe instead (a portable install, for example on a USB stick): close MultiPreview, "
        + "copy the settings file (and layout_presets.json, if you use layout presets) from the folder above into this "
        + "folder, and create an empty file named {1} here. MultiPreview then reads and saves its files here, as before.\n\n"
        + "If you install an older version from Settings › App settings › About & updates, MultiPreview first copies your "
        + "current settings and layout presets here, because older versions read the files beside the exe. The files that "
        + "were here are kept in the Backups folder inside the folder above.";

    /// <summary>ErrorLog.Changed, from any thread: one tooltip refresh queued at a time, so a burst of errors can't
    /// flood the dispatcher. It never throws or records an error itself, or a failing refresh would feed itself.</summary>
    private void QueueTrayText()
    {
        if (Interlocked.Exchange(ref _trayTextQueued, 1) == 1) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _trayTextQueued, 0);
                try
                {
                    if (_trayIcon != null)
                        _trayIcon.Text = TrayMenu.TooltipText(_hotkeyService?.IsSuspended ?? false, ErrorLog.Shared.Count, DoNotDisturbOn, _focusModeOn);
                }
                catch { /* a tooltip must never raise another error */ }
            }));
        }
        catch { Interlocked.Exchange(ref _trayTextQueued, 0); }
    }

    /// <summary>IDEAS #55: the last session ended without closing (a crash, Task Manager, a power cut). One notice, which
    /// doesn't take focus from the game, offering the logs folder and a bug report (IDEAS #23).</summary>
    private void ShowUncleanExitNotice(DateTime started)
    {
        try
        {
            var notice = new StartupDialog(
                LocalizationService.Str("L.Start.Crash.Title", "MultiPreview closed unexpectedly"),
                string.Format(LocalizationService.Str("L.Start.Crash.Body", UncleanBodyEnglish),
                    started.ToString("g", System.Globalization.CultureInfo.CurrentCulture)),
                new[]
                {
                    LocalizationService.Str("L.Start.Crash.Open", "Open the logs folder"),
                    LocalizationService.Str("L.Start.Crash.Report", "Create a bug report"),
                    LocalizationService.Str("L.Start.Crash.Close", "Close"),
                })
            { ShowActivated = false };
            notice.Closed += (_, _) => { if (notice.Choice == 0) DiagnosticsService.OpenLogsFolder(); else if (notice.Choice == 1) OpenBugReport(); };
            notice.Show();
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #23 from the after-a-crash notice (#55): the bug report window, on its own (no settings window is open).</summary>
    private void OpenBugReport()
    {
        try
        {
            if (_settings == null) return;
            SettingsUi.BugReportActions.Show(_settings.Settings,
                _thumbnailManager?.GetActiveCharacterNames().ToList() ?? new List<string>(), owner: null, modal: false);
        }
        catch (Exception ex) { LogError(ex); }
    }

    private const string UncleanBodyEnglish =
        "The last session, started {0}, ended without MultiPreview closing: it crashed, or was stopped by Task Manager "
        + "or a power cut. If something went wrong, error_log.txt in the logs folder may say what. Attach it to a bug report.";

    /// <summary>The one-second timer runs only while something needs it: escalation switched on (IDEAS #29), or the
    /// "Left behind" event (IDEAS #5). With the repeats switched off, the chains that were being followed are
    /// forgotten here and now: the timer may stop with this very change, and then no tick comes to forget them.</summary>
    private void FollowAlertTimer()
    {
        if (_alertTimer == null || _settings == null) return;
        if (!AlertEscalation.Needed(_settings.Settings)) _escalation.Forget();
        bool needed = !_isShuttingDown && (AlertEscalation.Needed(_settings.Settings) || LeftBehindOn(_settings.Settings));
        switch (AlertEscalation.TimerChange(needed, _alertTimer.IsEnabled))
        {
            case true: _alertTimer.Start(); break;
            case false: _alertTimer.Stop(); break;
        }
    }

    /// <summary>Once a second, on the window's thread, while the timer runs. Escalation (IDEAS #29): is a repeat due
    /// for an alert that is still unseen? AlertEscalation.Tick decides that, and what becomes of the repeat; it is
    /// handed what it needs to ask, and asks nothing while escalation is switched off. The repeat is played the way
    /// an alert's own sound is, so the master switch, the event's sound quiet time and do-not-disturb all apply.
    /// Nothing leaves this method as an exception: a DispatcherTimer whose tick threw ticks no more, and the repeats
    /// and "Left behind" would be off for the rest of the session.</summary>
    private void AlertTick()
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return;
            var s = _settings.Settings;
            var now = DateTime.UtcNow;
            // IDEAS #5: has a jump wave left a member of a group behind? Asked before escalation, whatever that is set to.
            if (LeftBehindOn(s)) LookForStragglers(now);
            _escalation.Tick(s, now,
                unseen: () => _thumbnailManager.Attention.Unseen(),
                // What ends a chain for good: the character is muted, the event is snoozed for it, the character has
                // no client left to go to, or the event was switched off (a rule of the player's own: off or deleted).
                quieted: (who, type) => _thumbnailManager.IsCharacterAlertMuted(who) || _snoozes.IsSnoozed(who, type)
                    || _thumbnailManager.GetHwndForCharacter(who) == IntPtr.Zero || !_alertProfiles.StillOn(s, who, type),
                soundsHeld: () => _doNotDisturb.Silences(s, "critical", DateTime.Now),
                doNotDisturb: () => DoNotDisturbOn,
                play: (due, volume) => PlayAlertSound(due.Alert.Character, due.Alert.EventKey, due.Alert.Severity, due.Number, volume),
                failed: LogError,
                // IDEAS P82 (1): each repeat that is counted flashes the client's border again.
                flash: due => _thumbnailManager.FlashAgain(due.Alert.Character, due.Alert.Severity, due.Alert.EventKey));
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    /// <summary>IDEAS #42: the hotkeys Windows refused for the first time this session, in one tray note. The card on
    /// Settings › Clients › Hotkeys lists all of them, and a click on the note opens that page.</summary>
    private void TellRefusedHotkeys()
    {
        if (_isShuttingDown || _hotkeyService == null || _trayIcon == null) return;
        var fresh = _refusalAnnouncer.New(_hotkeyService.Refused.Current);
        if (fresh.Count == 0) return;
        ShowBalloon(8000, HotkeyRefusals.Note(fresh, ActionOfHotkey), ToolTipIcon.Warning, refusalNote: true);
    }

    /// <summary>Every tray balloon is shown here. Windows shows one balloon at a time, so the one shown last is the
    /// one a click is on: it is said first which balloon that is (IDEAS #42), and a click on "Client positions
    /// saved" never opens the hotkeys page because the note about refused hotkeys stood there before it.</summary>
    private void ShowBalloon(int milliseconds, string text, ToolTipIcon icon, bool refusalNote = false)
    {
        if (_trayIcon == null) return;
        _refusalNote.Shown(refusalNote);
        _trayIcon.ShowBalloonTip(milliseconds, "EVE MultiPreview", text, icon);
    }

    /// <summary>IDEAS #42, "Check again" on Settings › Clients › Hotkeys › Keys Windows refused: Windows is asked for
    /// every hotkey once more, the way a settings change asks (all released, the pause key registered, the others
    /// stored), and then at once what the half-second poll would do next (registered where clients are running and
    /// hotkeys are not paused), so the card has the answer when this returns. Nothing is saved, and nothing but the
    /// hotkeys and the mouse hook is touched.</summary>
    private void CheckHotkeysAgain()
    {
        if (_isShuttingDown || _hotkeyService == null || _settings == null || _thumbnailManager == null) return;
        _hotkeyService.RegisterFromSettings(_settings.Settings, _settings.CurrentProfile, _thumbnailManager, OpenSettings);
        _hotkeyService.EvaluateRegistration();
    }

    /// <summary>What a hotkey is bound to, as its card names it (the first, when several things share the key), or null. It
    /// goes into a tray balloon, so a character's or group's name follows streamer mode (IDEAS #19).</summary>
    private string? ActionOfHotkey(string hotkey) => _settings == null ? null
        : SettingsUi.RefusedHotkeysEditor.UsesOf(hotkey,
            SettingsUi.HotkeyUses.All(_settings.Settings, SettingsUi.SettingsRegistry.Settings, masked: true)).FirstOrDefault()?.Label;

    /// <summary>IDEAS #31: which events are on. The gate is handed its picture of the groups' choices, and the log
    /// monitor what it may raise: the settings' own table while no group has a choice (as it always was), otherwise
    /// what is on for everyone or for some group. AlertProfiles.Apply does both, in the order that lets nothing
    /// through in between, and without settings or without a log monitor (a profile switch in the first moments
    /// of a start) it does what it can. Called on the window's thread: at startup, after every settings change,
    /// and at a profile switch. Nothing leaves this method as an exception: startup goes on to the alert handler,
    /// the log monitor's start, the tray and the hub, a profile switch to the new profile's hotkeys, and a
    /// settings change to what follows it. The gate and the log monitor then keep what they had.</summary>
    private void ApplyAlertSwitches()
    {
        try
        {
            _alertProfiles.Apply(_settings?.Settings, _logMonitor == null ? null : _logMonitor.SetEnabledAlertTypes);
            // IDEAS #58: the groups' sounds, at the same three moments as the switches.
            if (_settings != null) _groupSounds.Publish(_settings.Settings);
        }
        catch (Exception ex)
        {
            // Reported by its type alone: the message may quote a name a player wrote.
            LogError(new InvalidOperationException("Alerts per group: the switches could not be handed out (" + ex.GetType().Name + ")"));
        }
    }

    /// <summary>Whether "Left behind" is looked for: it is on for everyone, or (IDEAS #31) some group switched it on.
    /// Each alert a look raises still passes the gate for its own character.</summary>
    private bool LeftBehindOn(EveMultiPreview.Models.AppSettings s) =>
        StragglerWatch.IsOn(s) || _alertProfiles.OnForSomeone(StragglerWatch.EventKey);

    /// <summary>IDEAS #5, once a second while the event is on: has a jump wave left a member of a group behind?
    /// StragglerAlerts looks (the groups as the Groups page has them, the characters that have a client, where the
    /// log monitor has each) and raises the alert the way every alert is raised. A failure there costs that look or
    /// that one alert, never the timer.</summary>
    private void LookForStragglers(DateTime now) => _stragglerAlerts?.Look(now);

    /// <summary>Whether "Client memory high" is looked for: it is on for everyone, or (IDEAS #31) some group switched it
    /// on. Each alert a look raises still passes the gate for its own character.</summary>
    private bool MemoryOn(EveMultiPreview.Models.AppSettings s) =>
        MemoryWatch.IsOn(s) || _alertProfiles.OnForSomeone(MemoryWatch.EventKey);

    /// <summary>IDEAS #45, at each report of the process monitor: is a client's memory high? Raised the way "Left behind"
    /// is, one try per alert. A failure costs that look or that one alert, never the monitor.</summary>
    private void LookAtMemory()
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null || _processMonitor == null || _stragglerAlerts == null) return;
            var s = _settings.Settings;
            if (!MemoryOn(s)) { _memoryWatch.Forget(); return; }
            var now = DateTime.UtcNow;
            foreach (var alert in _memoryWatch.Check(now, MemoryOfClients(), MemoryOptions.Of(s)))
            {
                try { _stragglerAlerts.Raise(now, alert.Character, MemoryWatch.EventKey, MemoryWatch.Severity, alert.Text); }
                catch (Exception ex) { LogError(ex); }
            }
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>The working set of each client that has a character, by the process of its window.</summary>
    private IEnumerable<(string Character, int Pid, long RamMb)> MemoryOfClients()
    {
        foreach (var name in _thumbnailManager!.GetActiveCharacterNames())
        {
            var hwnd = _thumbnailManager.GetHwndForCharacter(name);
            if (hwnd == IntPtr.Zero) continue;
            EveMultiPreview.Interop.User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (_processMonitor!.GetStats((int)pid) is { } stats) yield return (name, (int)pid, stats.RamMB);
        }
    }

    /// <summary>IDEAS #22, #47, every 3 s on the process monitor's pool thread: asks Windows what LookAtClients last said is
    /// wanted (frozen windows, the TCP table, the clients' ends) and hands it to the window's thread. It reads no setting.
    /// The processes are held only while "Client closed unexpectedly" is wanted; otherwise every handle is let go.</summary>
    private void ProbeClients()
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;
        try
        {
            if (_isShuttingDown || _thumbnailManager == null) return;
            var wants = _clientProbeWants;
            IReadOnlyList<ClientSample> samples = wants == ClientProbeWants.None ? Array.Empty<ClientSample>()
                : ClientProbe.Take(_thumbnailManager.ClientWindows(), wants, ClientProbe.PidOf, ClientProbe.IsHung,
                    (wants & ClientProbeWants.Links) != 0 ? TcpTable.Read() : null);
            IReadOnlyList<ClientExit> exits = Array.Empty<ClientExit>();
            if ((wants & ClientProbeWants.Exits) != 0)
            {
                foreach (var client in samples) _clientExits.Track(client.Pid, client.Character);
                exits = _clientExits.Look();
            }
            else _clientExits.Forget();
            Dispatcher.BeginInvoke(new Action(() => LookAtClients(samples, exits)), DispatcherPriority.Background);
        }
        catch (Exception ex) { LogError(ex); }
        finally { Volatile.Write(ref _probing, 0); }
    }

    /// <summary>IDEAS #22, #47, on the window's thread after each probe: what is wanted next time, the marks, "Client not
    /// responding", and "Client closed unexpectedly" for each client that ended with an error code. One try per alert.</summary>
    private void LookAtClients(IReadOnlyList<ClientSample> samples, IReadOnlyList<ClientExit> exits)
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return;
            var s = _settings.Settings;
            bool closedOn = ClientEventOn(s, ClientWatch.ClosedEvent);
            var options = ClientWatchOptions.Of(s, key => ClientEventOn(s, key));
            _clientProbeWants = options.Wants(closedOn);
            var now = DateTime.UtcNow;
            var look = _clientWatch.Look(now, samples, options);
            foreach (var change in look.Changes) _thumbnailManager.SetClientMark(change.Window, change.Mark);
            // IDEAS Plan 20, P119: an end seen before the alerts exist is held, not lost; the first look after they do tells it.
            var due = _exitBacklog.Take(exits, _stragglerAlerts != null);
            if (_stragglerAlerts == null) return;
            foreach (var alert in look.Alerts)
            {
                try { _stragglerAlerts.Raise(now, alert.Character, alert.EventKey, alert.Severity, alert.Text); }
                catch (Exception ex) { LogError(ex); }
            }
            if (!closedOn) return;
            foreach (var exit in due)
                if (exit.Unexpected && exit.Character.Length > 0) RaiseClosed(now, exit);
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>"Client closed unexpectedly" for one client, its character counted as having a window for this one call
    /// (ruling 5). A log alert for the same character dispatched on the log thread in that instant passes the gate too;
    /// its flash and badge find no thumbnail and do nothing. The crashed client has no window, so this alert reaches the
    /// toast, the sound, push and the history, and (IDEAS Plan 20, P118) it is spoken and a Windows notification too, by the
    /// character's name: the ways are told the client is gone (<c>clientGone</c>, from <c>_closingCharacter</c>), so the
    /// notification has no "Switch to" and nothing is enlarged (there is no thumbnail to enlarge).</summary>
    private void RaiseClosed(DateTime now, ClientExit exit)
    {
        _closingCharacter = exit.Character;
        try { _stragglerAlerts!.Raise(now, exit.Character, ClientWatch.ClosedEvent, ClientWatch.Severity, ClientExits.CodeText(exit.ExitCode)); }
        catch (Exception ex) { LogError(ex); }
        finally { _closingCharacter = null; }
    }

    private const string EndFrozenEnglish = "{0}'s client is not responding. End it now? Anything it has not saved is lost.";

    /// <summary>IDEAS #22, "End this frozen client…": asks first (Keep by default, in the look's own window, over a full-screen
    /// client), then ends only the process the freeze was seen on, and only while its window is still that process's
    /// and still frozen (ClientExits.End asks that while it holds the process). Raised by a menu click, so this runs on the
    /// window's thread, and the end follows the answer there. Says near the cursor what came of it.</summary>
    private void EndFrozenClient(IntPtr window, string character)
    {
        try
        {
            if (_isShuttingDown || _clientWatch.PidOf(window) is not int pid) return;
            // Streamer mode (IDEAS #19): the box and the note are drawn text; the pid is looked up from the window, not the name.
            var who = ThumbnailMenu.HasCharacter(character) ? StreamerMask.Name(StreamerKind.Character, character) : LocalizationService.Str("L.Ctx.LoginScreen", "Character select");
            // P116: the look's own window, Keep is the default button; Enter, Esc and the X all keep the client running. Only End (true) ends it.
            var dialog = new ChoiceDialog("EVE MultiPreview",
                string.Format(LocalizationService.Str("L.Msg.EndFrozenConfirm", EndFrozenEnglish), who),
                LocalizationService.Str("L.Msg.EndFrozenYes", "End the client"), LocalizationService.Str("L.Msg.EndFrozenNo", "Keep it running"),
                SafeAnswer.Second) { Topmost = true, ShowInTaskbar = false };
            dialog.ShowDialog();
            if (dialog.Result != true) return;
            var result = _clientExits.End(pid, () => ClientProbe.PidOf(window) == pid && ClientProbe.IsHung(window));
            var (key, english) = ClientExits.Feedback(result);
            _thumbnailManager?.ShowTooltipFeedback(string.Format(LocalizationService.Str(key, english), who));
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>Whether a client event is on for everyone or (IDEAS #31) for some group.</summary>
    private bool ClientEventOn(EveMultiPreview.Models.AppSettings s, string key) =>
        ClientWatch.IsOn(s, key) || _alertProfiles.OnForSomeone(key);

    /// <summary>IDEAS #32: the hotkey or the tray item flips do-not-disturb, and says near the cursor what it is now:
    /// once per press, as the other hotkeys' feedback is given.</summary>
    private void ToggleDoNotDisturb()
    {
        if (_isShuttingDown || _settings == null) return;
        var (key, english) = _doNotDisturb.Press(_settings.Settings, DateTime.Now);
        _thumbnailManager?.ShowTooltipFeedback(LocalizationService.Str(key, english));
        FollowDoNotDisturb();
    }

    /// <summary>IDEAS #19: the tray item flips streamer mode and saves it. Every live overlay (thumbnails, second thumbnails,
    /// stats windows, crops) and the alert history window are drawn again from the real names, and the near-cursor note says
    /// what it is now. No hotkey and no pipe verb flips it.</summary>
    private void ToggleStreamerMode()
    {
        try
        {
            if (_isShuttingDown || _settings == null) return;
            _settings.Settings.StreamerMode = !_settings.Settings.StreamerMode;
            _settings.Save();
            _thumbnailManager?.ReapplySettings();
            _cropManager?.Refresh();
            _streamerMaskDrawn = StreamerMask.IsOn;
            _historyWindow?.RefreshStreamerMask();
            _statReportWindow?.RefreshStreamerMask();
            _fleetBoardWindow?.RefreshStreamerMask();
            _trayMenu?.Refresh();
            var (key, english) = StreamerMask.IsOn ? ("L.Streamer.On", "Streamer mode: on") : ("L.Streamer.Off", "Streamer mode: off");
            _thumbnailManager?.ShowTooltipFeedback(LocalizationService.Str(key, english));
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>The tray's tip says while do-not-disturb is on. It can change without anybody pressing anything (the
    /// quiet hours begin or end), so this is asked twice a second and refreshes the tip only when the answer changed.</summary>
    private void FollowDoNotDisturb()
    {
        if (_doNotDisturb.ChangedSinceShown(_settings?.Settings, DateTime.Now)) QueueTrayText();
    }

    /// <summary>IDEAS #46: asked twice a second with do-not-disturb; KeepAwake asks Windows only when the answer changes.
    /// A timer whose tick threw would tick no more, so a failure is logged and the next tick tries again.</summary>
    private void FollowKeepAwake()
    {
        try
        {
            _keepAwake.Follow(KeepAwake.Wanted(_settings?.Settings, _thumbnailManager?.GetActiveCharacterNames() ?? Array.Empty<string>()));
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #9: asked twice a second. Nothing while the switch is off, and nothing while no character is logged
    /// in (so a warning is not used up unseen); then, when one is due, a balloon naming every logged-in character
    /// (docking is never logged, so who is in space is not known). A failure is logged and the next tick tries again.</summary>
    private void FollowDowntime()
    {
        try
        {
            if (_settings?.Settings.DowntimeWarnings != true) return;
            var names = _thumbnailManager?.GetActiveCharacterNames().ToList() ?? new List<string>();
            if (names.Count == 0) return;
            if (_downtimeWarnings.Due(DateTime.UtcNow) is int minutes)
                ShowBalloon(10000, Downtime.WarningText(minutes, names), ToolTipIcon.Warning);
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #34: asked twice a second. Nothing while the switch is off; then the watch decides whether the downtime
    /// window is open and a poll is due, and tells the players in a balloon once, when the server is back up. Today its
    /// fetch answers "not allowed" (P120). A failure is logged: an async void must never raise.</summary>
    private async void FollowTranquility()
    {
        try
        {
            if (_settings?.Settings.TranquilityBackUp != true) return;
            if (await _tranquilityWatch.PollAsync(DateTime.UtcNow, System.Threading.CancellationToken.None) is int players && !_isShuttingDown)
                ShowBalloon(10000, TranquilityWatch.BalloonText(players), ToolTipIcon.Info);
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>Whether "Character idle" is looked for: on for everyone, or (IDEAS #31) for some group.</summary>
    private bool IdleOn(EveMultiPreview.Models.AppSettings s) =>
        IdleWatch.IsOn(s) || _alertProfiles.OnForSomeone(IdleWatch.EventKey);

    /// <summary>IDEAS #7, twice a second: has a character that was working stopped? While the event is off nothing is noted
    /// and nothing kept. A failure is logged and the next tick tries again.</summary>
    private void FollowIdle()
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null || _stragglerAlerts == null) return;
            var s = _settings.Settings;
            if (!IdleOn(s))
            {
                if (_idleWatch.Enabled) { _idleWatch.Enabled = false; _idleWatch.Forget(); }
                return;
            }
            _idleWatch.Enabled = true;
            var now = DateTime.UtcNow;
            foreach (var alert in _idleWatch.Check(now, _thumbnailManager.GetActiveCharacterNames(), IdleWatch.Minutes(s)))
            {
                try { _stragglerAlerts.Raise(now, alert.Character, IdleWatch.EventKey, IdleWatch.Severity, alert.Text); }
                catch (Exception ex) { LogError(ex); }
            }
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>"Client ready" (batch 13, P12) for one character, on the window's thread. Like the other derived events it
    /// goes through the dispatcher, which asks the event's switch for that character: off until ticked. A failure is logged.</summary>
    private void RaiseClientReady(string character)
    {
        try { _stragglerAlerts?.Raise(DateTime.UtcNow, character, ClientReady.EventKey, ClientReady.Severity); }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #35, from its hotkey, the tray item or a command: focus mode on or off, said near the cursor, and
    /// applied at once. With no group to show (the chosen group is gone, or the client is in no group or at character
    /// select) a press does not turn it on: it says why (IDEAS P124, final review M4 of Plan 14). The tray's tip follows.
    /// Never saved. A failure is logged: a key press must not throw.</summary>
    private void ToggleFocusMode()
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return;
            var (chosen, _) = FocusMode.Options(_settings.Settings);
            var group = FocusMode.GroupFor(chosen, _thumbnailManager.ActiveCharacter(), FocusMode.Groups(_settings.Settings));
            var press = FocusMode.Press(_focusModeOn, chosen, group);
            _focusModeOn = press == FocusPress.On;
            if (press == FocusPress.Off) _thumbnailManager.HoldForFocus(FocusMode.Nobody);
            else if (press == FocusPress.On) _focusArrivals.Begin(_thumbnailManager.GetActiveCharacterNames());
            _thumbnailManager.ShowTooltipFeedback(FocusMode.Say(press, press == FocusPress.On ? group!.Value.Name : chosen));
            QueueTrayText();
            if (_focusModeOn) FollowFocusMode();
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #35, twice a second while focus mode is on: which thumbnails it holds hidden now (the group follows the
    /// client you are in; an unseen alert shows through). Nothing is read while it is off. A failure is logged and the next
    /// tick tries again.</summary>
    private void FollowFocusMode()
    {
        try
        {
            if (!_focusModeOn || _isShuttingDown || _settings == null || _thumbnailManager == null) return;
            var s = _settings.Settings;
            var (chosen, alertsShow) = FocusMode.Options(s);
            var group = FocusMode.GroupFor(chosen, _thumbnailManager.ActiveCharacter(), FocusMode.Groups(s));
            var arrived = _focusArrivals.Look(DateTime.UtcNow, _thumbnailManager.GetActiveCharacterNames());   // IDEAS P124
            _thumbnailManager.HoldForFocus(FocusMode.Held(group, _thumbnailManager.GetActiveCharacterNames(), alertsShow,
                name => _thumbnailManager.Attention.Has(name), arrived.Contains));
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #11, twice a second: while "Switch profiles by who is logged in" is on, has the set of logged-in
    /// characters settled on another profile? Switch to it, or ask when the player switched himself a short while ago.
    /// While the switch is off nothing is weighed and an open question is closed without a switch. A failure is logged;
    /// the decision for that set of characters is spent (AutoProfile.Look), so it is made again only when the set changes.</summary>
    private void FollowAutoProfile()
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return;
            var s = _settings.Settings;
            if (!AutoProfile.IsOn(s)) { _autoProfile.Forget(); CloseAutoProfileQuestion(); return; }
            var choice = _autoProfile.Look(DateTime.UtcNow, _thumbnailManager.GetActiveCharacterNames(), AutoProfile.Candidates(s), s.LastUsedProfile);
            if (choice == null) return;
            if (choice.Ask) AskAutoProfile(choice.Profile);
            else SwitchProfileAutomatically(choice.Profile);
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #11: switches as the tray does (ProfileSwitched applies it, #101), not counted as the player's own
    /// switch, and says so near the cursor. A profile deleted meanwhile is not switched to.</summary>
    private void SwitchProfileAutomatically(string profile)
    {
        if (_settings == null || !_settings.Settings.Profiles.ContainsKey(profile)) return;
        _switchingProfileAutomatically = true;
        try { _settings.SwitchProfile(profile); }
        finally { _switchingProfileAutomatically = false; }
        _thumbnailManager?.ShowTooltipFeedback(string.Format(
            LocalizationService.Str("L.AutoProfile.Switched", "Profile: {0}, picked by who is logged in"), StreamerMask.Name(StreamerKind.Profile, profile)));
    }

    /// <summary>IDEAS #11: the switch went off, so a question still on the screen is closed with no choice and can switch
    /// nothing. The field is cleared first, so the question's Closed handler finds no current question.</summary>
    private void CloseAutoProfileQuestion()
    {
        var open = _autoProfileQuestion;
        if (open == null) return;
        _autoProfileQuestion = null;
        open.Close();
    }

    private const string AutoProfileBodyEnglish =
        "The characters logged in now belong to the profile {0}. You switched profiles yourself a few minutes ago, so MultiPreview asks before it leaves {1}.";

    /// <summary>IDEAS #11: the player switched profiles himself a short while ago, so the switch is a question, in a notice
    /// that does not take the front from the game. A newer question replaces an older one; Switch is not counted as the
    /// player's own switch.</summary>
    private void AskAutoProfile(string profile)
    {
        if (_settings == null) return;
        // Streamer mode (IDEAS #19): the question is drawn text; the profile switched to is still the real name.
        var current = _settings.Settings.LastUsedProfile;
        var shownProfile = StreamerMask.Name(StreamerKind.Profile, profile);
        var shownCurrent = StreamerMask.Name(StreamerKind.Profile, current);
        _autoProfileQuestion?.Close();
        var question = new StartupDialog(
            LocalizationService.Str("L.AutoProfile.Title", "Switch profile?"),
            string.Format(LocalizationService.Str("L.AutoProfile.Body", AutoProfileBodyEnglish), shownProfile, shownCurrent),
            new[]
            {
                string.Format(LocalizationService.Str("L.AutoProfile.Switch", "Switch to {0}"), shownProfile),
                string.Format(LocalizationService.Str("L.AutoProfile.Stay", "Stay on {0}"), shownCurrent),
            })
        { ShowActivated = false };
        question.Closed += (_, _) =>
        {
            if (ReferenceEquals(_autoProfileQuestion, question)) _autoProfileQuestion = null;
            // The player may click Switch long after the question came up: the switch must still be on and the profile
            // still there, and a failure here must not leave the window's event handler.
            try
            {
                if (question.Choice == 0 && _settings != null && AutoProfile.IsOn(_settings.Settings)
                    && _settings.Settings.Profiles.ContainsKey(profile))
                    SwitchProfileAutomatically(profile);
            }
            catch (Exception ex) { LogError(ex); }
        };
        _autoProfileQuestion = question;
        question.Show();
    }

    /// <summary>IDEAS #39, Plan 39 Ruling 1: the pipe listens while programs may ask to connect or "Take commands from other
    /// programs" is on (PipeGate.Listens), and does not exist otherwise. Called at startup and after every settings change.
    /// A running server is never rebuilt for a switch: it only gets the new Access (both switches and this process's
    /// elevation), which its gate reads on the next connection. Its door always carries the account's read/write rule
    /// (alsoUser), so an elevated MultiPreview's ordinary programs can connect and the gate, not the list, keeps them to hello
    /// and pair until the second switch is on (Ruling 3). The pipe's thread only posts a command to the window's thread
    /// (RunOnWindowsThread) and never blocks it. A server that gave up (the name stayed taken, the next instance could not be
    /// made) is let go when it says so, and the next settings change makes a new one: driven by Apply, not by a timer, so
    /// nothing loops.</summary>
    private void FollowCommandServer()
    {
        try
        {
            // Rulings 1 and 24: a pipe while "Let Stream Deck and other programs ask to connect" or "Take commands from other programs" is on
            bool wanted = !_isShuttingDown && _settings != null && PipeGate.Listens(_settings.Settings.AllowPairingRequests, _settings.Settings.AcceptCommands);
            if (!wanted)
            {
                _commandServer?.Dispose();
                _commandServer = null;
                return;
            }
            var s = _settings!.Settings;
            var access = new PipeAccess(s.AcceptCommands, s.AcceptCommandsUnelevated, CommandPipe.IsElevated());
            if (_commandServer != null) { _commandServer.Access = access; return; }
            var (user, owner) = CommandPipe.CurrentAccount();
            CommandServer? made = null;
            made = new CommandServer(CommandPipe.CurrentName(), CommandPipe.Security(user, owner, alsoUser: true),
                command => RunOnWindowsThread(command),
                ex => { LogError(ex); DropFailedCommandServer(made); },   // logged once, without the pipe's name
                query: command => QueryOnWindowsThread(command),           // IDEAS P126: the queries, whose reply carries data
                pairing: (command, client) => PairOnWindowsThread(command, client))   // Plan 39: hello and pair
            { Access = access };
            _commandServer = made;
            made.Start();
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #39: a server that gave up is let go on the window's thread, but only that very server (a newer one of a
    /// switch that went off and on is not touched) and only when it gave up: one command that threw does not end a server
    /// that goes on. The next settings change then makes a new one.</summary>
    private void DropFailedCommandServer(CommandServer? failed)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (failed?.Failed == true && ReferenceEquals(_commandServer, failed))
                {
                    failed.Dispose();
                    _commandServer = null;
                }
            });
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #39: has the window's thread run one command, waiting at most the pipe's read time to START it (the
    /// sender's clock, CommandPipe.SendTime, covers this wait twice over, IDEAS P130; three or more senders queued behind one
    /// another can still outlast it). A command still queued when the wait ends is taken back, so this wait does not leave it to
    /// run later; one that has started is quick and its answer is awaited. Each command has its own wait: nothing is shared
    /// between two senders. A stall is not an error and is not logged.</summary>
    private async Task<RemoteOutcome> RunOnWindowsThread(RemoteCommand command)
    {
        var queued = Dispatcher.InvokeAsync(() => RunCommand(command));
        try { return await queued.Task.WaitAsync(CommandServer.ReadTime); }
        catch (TimeoutException)
        {
            if (queued.Abort()) return RemoteOutcome.NotReachable;   // never ran
            return await queued.Task;                                // already running on the window's thread: quick
        }
    }

    /// <summary>IDEAS #39: one command, on the window's thread, with App's parts (RemoteCommandRunner decides). A profile
    /// switched so counts as the player's own switch (IDEAS #11). A failure is logged and answered "refused".</summary>
    private RemoteOutcome RunCommand(RemoteCommand command)
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return RemoteOutcome.NotReachable;
            var thumbnails = _thumbnailManager;
            var settings = _settings;
            return new RemoteCommandRunner
            {
                Running = () => thumbnails.GetActiveCharacterNames(),
                Profiles = () => settings.GetProfileNames(),
                Focus = name => thumbnails.ActivateEveWindow(IntPtr.Zero, name),
                SwitchProfile = name => settings.SwitchProfile(name),   // ProfileSwitched applies it (#101)
                ToggleThumbnails = () => thumbnails.ToggleThumbnailVisibility(),
                ToggleFocusMode = ToggleFocusMode,
                Groups = () => FocusMode.Groups(settings.Settings).Select(g => g.Name),
                CycleGroup = (name, forward) =>
                {
                    var group = FocusMode.Groups(settings.Settings).FirstOrDefault(g => g.Name == name);
                    if (group.Members != null) thumbnails.CycleGroup(forward ? "remote_fwd" : "remote_bwd", group.Members.ToList(), forward);
                },
                CycleAll = forward => thumbnails.CycleAll(forward),
                PreviousClient = () => thumbnails.SwitchToPreviousClient(),
                LastAlert = () => thumbnails.GoToLastAlert(),
                CycleProfile = forward => CycleProfile(forward),
                DoNotDisturb = ToggleDoNotDisturb,
                IsDoNotDisturbOn = () => DoNotDisturbOn,
                IsFocusModeOn = () => _focusModeOn,
                // IDEAS P129: the menu's whole-character alert mute, for the longest timed length the menu offers
                SetAlertMute = (name, mute) => thumbnails.SetCharacterAlertMute(name, mute ? ThumbnailMenu.LongestTimedMuteMinutes : 0),
                // Plan 38: the windows open through the methods the tray and the settings use (on this thread); the name bar through
                // the switch the settings card flips; the minimise and restore parts never activate a window (ClientWindowCommands).
                OpenSettings = () => OpenSettings(false),
                OpenFleetBoard = OpenFleetBoard,
                OpenAlertHistory = OpenAlertHistory,
                IsNameBarOn = () => settings.Settings.NameBarMode,
                SetNameBar = SetNameBarMode,
                MarkAlertsSeen = () =>
                {
                    int taken = thumbnails.MarkAllAlertsSeen();   // AlertAttention.SeeAll + the border flashes
                    if (taken > 0) thumbnails.ShowTooltipFeedback(LocalizationService.Str("L.Msg.AlertsAllSeen", "Alerts marked seen"));
                    return taken;
                },
                MuteFor = thumbnails.SetCharacterAlertMute,
                MinimizeClients = thumbnails.MinimizeAllClients,
                RestoreClients = thumbnails.RestoreAllClients,
            }.Run(command);
        }
        catch (Exception ex)
        {
            LogError(ex);
            return RemoteOutcome.Refused;
        }
    }

    /// <summary>IDEAS #43: the game pad follows its switch. On, the bindings are read again from the settings (a line that is not a
    /// button and a command of the list is left out, GamepadMap) and, if it is not already there, the one 50 ms timer is made;
    /// off, or when the app closes, the timer is stopped and let go together with the poller, so nothing is read. Called at start-up
    /// and whenever settings apply; the same answer again changes nothing. The pad is only ever read, never written to.</summary>
    private void FollowGamepad()
    {
        try
        {
            if (_isShuttingDown || _settings?.Settings.GamepadEnabled != true) { StopGamepad(); return; }
            _gamepadBindings = GamepadMap.Parse(_settings.Settings.GamepadBindings).Bindings;
            if (_gamepadTimer != null) return;
            _gamepadPoller = new GamepadPoller(new XInputSource(), () => Environment.TickCount64);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GamepadPollMs) };
            timer.Tick += (_, _) => GamepadTick();
            _gamepadTimer = timer;
            timer.Start();
        }
        catch (Exception ex) { StopGamepad(); LogError(ex); }
    }

    private void StopGamepad()
    {
        _gamepadTimer?.Stop();
        _gamepadTimer = null;
        _gamepadPoller = null;
        _gamepadBindings = new Dictionary<GamepadButton, RemoteCommand>();
    }

    /// <summary>IDEAS #43: one look at the pad, on the window's thread (a DispatcherTimer ticks there). A button newly pressed
    /// runs the command bound to it through RunCommand, the entry point the command pipe and the palette use; a command it
    /// refuses or cannot find is dropped without a word. A pad press follows the rules of a hotkey: while hotkeys are paused
    /// (Suspend), or are held back (EVE-only scope with another program in front, the settings window or the command palette in
    /// front: HotkeyService.InputBlocked), the pad is still read, so a button held through it does not fire later, but nothing
    /// runs; with no hotkey service nothing runs either. A failure stops the pad (the next settings change starts it again) and
    /// is logged once, so a pad that keeps failing cannot fill the log twenty times a second.</summary>
    private void GamepadTick()
    {
        try
        {
            if (_isShuttingDown || _gamepadPoller == null) return;
            var pressed = _gamepadPoller.Poll();
            if (pressed.Count == 0 || _hotkeyService is not { } hk || hk.IsSuspended || hk.InputBlocked()) return;
            foreach (var button in pressed)
                if (_gamepadBindings.TryGetValue(button, out var command)) RunCommand(command);
        }
        catch (Exception ex) { StopGamepad(); LogError(ex); }
    }

    /// <summary>IDEAS P126: the same wait and the same taking back as <see cref="RunOnWindowsThread"/> for a query: it reads state
    /// on the window's thread, and one that never started answers "nothing took it".</summary>
    private async Task<QueryReply> QueryOnWindowsThread(RemoteCommand command)
    {
        var queued = Dispatcher.InvokeAsync(() => RunQuery(command));
        try { return await queued.Task.WaitAsync(CommandServer.ReadTime); }
        catch (TimeoutException)
        {
            if (queued.Abort()) return new QueryReply(RemoteOutcome.NotReachable, Array.Empty<string>());   // never ran
            return await queued.Task;                                                                        // already running: quick
        }
    }

    /// <summary>IDEAS P126: one query, on the window's thread. It reads what the commands read (the running characters, the groups,
    /// the profiles, the three switches) and changes nothing; the names are handed on as they are (the pipe is this account's own,
    /// for its own programs). A failure is logged and answered "refused".</summary>
    private QueryReply RunQuery(RemoteCommand command)
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return new QueryReply(RemoteOutcome.NotReachable, Array.Empty<string>());
            var thumbnails = _thumbnailManager;
            var settings = _settings;
            return new RemoteQueryRunner
            {
                Running = () => thumbnails.GetActiveCharacterNames(),
                Profiles = () => settings.GetProfileNames(),
                Groups = () => FocusMode.Groups(settings.Settings).Select(g => g.Name),
                IsDoNotDisturbOn = () => DoNotDisturbOn,
                IsFocusModeOn = () => _focusModeOn,
                AreThumbnailsHidden = () => thumbnails.AllThumbnailsHidden,
                LiveState = () => LiveStateLines(settings, thumbnails),
            }.Query(command);
        }
        catch (Exception ex)
        {
            LogError(ex);
            return QueryReply.Refused;
        }
    }

    /// <summary>Plan 39: the pairing seam of the command pipe, with the same wait and the same taking back as
    /// <see cref="QueryOnWindowsThread"/>. <see cref="Pair"/> answers at once on the window's thread and never waits for the
    /// player: the window is opened after the answer (Ruling 8), so the pipe, which serves one client at a time, is never held.</summary>
    private async Task<QueryReply> PairOnWindowsThread(RemoteCommand command, PipeClient client)
    {
        var queued = Dispatcher.InvokeAsync(() => Pair(command, client));
        try { return await queued.Task.WaitAsync(CommandServer.ReadTime); }
        catch (TimeoutException)
        {
            if (queued.Abort()) return QueryReply.NotReachable;   // never ran
            return await queued.Task;                             // already running: quick
        }
    }

    /// <summary>The pairing desk's clock: monotonic, never the wall clock (a clock set back must not end a pause early).</summary>
    private static TimeSpan PairClock() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Plan 39 Rulings 6, 8, 9 and 24, on the window's thread. hello: <c>commands=on|off</c> (whether this connection is
    /// admitted for the normal verbs now) and the desk's pairing word, which is <c>blocked</c> while "Let Stream Deck and other
    /// programs ask to connect" is off. pair: Done for a client that is already admitted; Refused (no window, ever) while
    /// that switch is off; else the desk decides: a new window (opened after this answer), one already open (Done), or a
    /// pause or the end of the run's windows (Refused). The program is named from its PID now, while it still waits for this
    /// answer (so the PID is still its own); the name is display only.</summary>
    private QueryReply Pair(RemoteCommand command, PipeClient client)
    {
        try
        {
            if (_isShuttingDown || _settings == null) return QueryReply.NotReachable;
            var now = PairClock();
            var access = _commandServer?.Access ?? PipeAccess.Closed;
            bool admitted = PipeGate.Admits(RemoteVerb.Focus, client, access);
            bool asking = _settings.Settings.AllowPairingRequests;
            if (command.Verb == RemoteVerb.Hello)
                return new QueryReply(RemoteOutcome.Done, PairingHello.Lines(admitted, asking ? _pairing.Status(now) : "blocked"));
            if (command.Verb != RemoteVerb.Pair) return QueryReply.Refused;
            if (admitted) return new QueryReply(RemoteOutcome.Done, Array.Empty<string>());
            if (!asking) return QueryReply.Refused;
            switch (_pairing.Request(now))
            {
                case PairStep.Ask:
                    // Ruling 3: an unelevated program asking an elevated MultiPreview gets the elevated words, and Allow turns on both switches
                    bool both = access.ServerElevated && !client.Elevated;
                    var image = CommandPipe.ImageNameOf(client.ProcessId);
                    Dispatcher.BeginInvoke(() => ShowPairPrompt(image, both));
                    return new QueryReply(RemoteOutcome.Done, Array.Empty<string>());
                case PairStep.Asking:
                    return new QueryReply(RemoteOutcome.Done, Array.Empty<string>());
                default:
                    return QueryReply.Refused;
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
            return QueryReply.Refused;
        }
    }

    /// <summary>Plan 39 Rulings 5 and 7: the Allow/Deny window, in the look of the app's other questions. Modeless, topmost, shown
    /// without taking the front from EVE, in the taskbar; Deny is the default, and unanswered after
    /// <see cref="PairingDesk.PromptLife"/> it closes as Deny. It names the program by its image's file name, and its folder
    /// only while streamer mode is off. A window the desk already expired (its timer late) is closed and its answer dropped.</summary>
    private void ShowPairPrompt(string? image, bool both)
    {
        try
        {
            if (_isShuttingDown) { _pairing.Answered(false, PairClock()); return; }
            var shown = PairingPrompt.Text(image, both, StreamerMask.IsOn, LocalizationService.Str);
            var dialog = ChoiceDialog.AllowDeny(shown.Title, shown.From == null ? shown.Body : shown.Body + "\n\n" + shown.From,
                LocalizationService.Str("L.Pair.Allow", "Allow"), LocalizationService.Str("L.Pair.Deny", "Deny"));
            dialog.Topmost = true;
            dialog.ShowActivated = false;   // never takes the front from the EVE client (Ruling 5)
            dialog.ShowInTaskbar = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var previous = _pairPrompt;
            _pairPrompt = dialog;
            previous?.Close();
            var timer = new DispatcherTimer { Interval = PairingDesk.PromptLife };
            timer.Tick += (_, _) => { timer.Stop(); dialog.Close(); };   // unanswered: Deny
            dialog.Closed += (_, _) => { timer.Stop(); PairPromptClosed(dialog, both); };
            timer.Start();
            dialog.Show();
        }
        catch (Exception ex)
        {
            LogError(ex);
            _pairing.Answered(false, PairClock());   // a window that could not be shown counts as a Deny
        }
    }

    /// <summary>Plan 39 Rulings 3 and 5: the answer of the window that is open now. Allow turns on "Take commands from other
    /// programs" (and, for an unelevated program of an elevated MultiPreview, its second switch) through the settings path
    /// (Recently changed with Undo), saves, has the pipe follow and the open settings window show it, and says so near the
    /// cursor. Anything else (Deny, Esc, the X, the timer) changes nothing but the desk's pause.</summary>
    private void PairPromptClosed(ChoiceDialog dialog, bool both)
    {
        try
        {
            if (!ReferenceEquals(_pairPrompt, dialog)) return;   // replaced by a newer window: its answer no longer counts
            _pairPrompt = null;
            bool allowed = dialog.Result == true;
            _pairing.Answered(allowed, PairClock());
            if (!allowed || _isShuttingDown || _settings == null) return;
            var s = _settings.Settings;
            SettingSwitches.TurnOn(_settings, s, "app.general.acceptCommands");
            if (both) SettingSwitches.TurnOn(_settings, s, "app.general.acceptCommandsUnelevated");
            _settings.Save();
            FollowCommandServer();
            (_settingsWindow as SettingsUi.SettingsShell)?.Reload();
            _thumbnailManager?.ShowTooltipFeedback(LocalizationService.Str("L.Pair.Allowed", "Commands from other programs are on"));
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>Plan 38 (IDEAS P126): the lines of the live-state reply, on the window's thread, from what is already in memory
    /// (no file, no ESI): the switches, the worst alert nobody has looked at, the profile, and the name bar's buttons with the names
    /// as they are drawn (streamer mode masks them; the real name stands last). The Stream Deck plugin asks about once a second.</summary>
    private IReadOnlyList<string> LiveStateLines(SettingsService settings, ThumbnailManager thumbnails)
    {
        // Unseen() lists the most severe first, newest first within a severity: the first is the worst one (Plan 38 addendum, A2).
        var worst = thumbnails.Attention.Unseen().FirstOrDefault();
        var profile = settings.Settings.LastUsedProfile;
        var globals = new LiveGlobals(DoNotDisturbOn, _focusModeOn, thumbnails.AllThumbnailsHidden, settings.Settings.NameBarMode,
            StreamerMask.IsOn, worst?.Severity, profile, StreamerMask.Name(StreamerKind.Profile, profile),
            LiveState.EventKey(worst?.EventKey));
        return LiveState.Lines(globals, NameBarEntries(), name => StreamerMask.Name(StreamerKind.Character, name));
    }

    /// <summary>IDEAS P130 (b): a first start's --focus, --mute or --unmute names a character whose thumbnail the first discovery
    /// pass may not have made by the time start-up is done. True: leave the command for the next 500 ms tick
    /// (<see cref="FollowStartupCommand"/>), for at most StartupCommandWait.Limit counted from the first look; after that it is
    /// dropped without a word (no sender to tell, nothing logged). Every other command, and one whose character is running,
    /// answers false.</summary>
    private bool StartupCommandWaits()
    {
        if (_startupCommand is not RemoteCommand pending) return false;
        _startupCommandClock ??= Stopwatch.StartNew();
        var step = StartupCommandWait.Decide(pending, StartupRunningNames(), _startupCommandClock.Elapsed);
        if (step == StartupCommandStep.Drop) _startupCommand = null;
        return step == StartupCommandStep.Wait;
    }

    /// <summary>The characters that have a thumbnail now (none before the thumbnail manager exists), as the runner reads them.</summary>
    private IEnumerable<string> StartupRunningNames() =>
        _thumbnailManager?.GetActiveCharacterNames() ?? Enumerable.Empty<string>();

    /// <summary>IDEAS P130 (b): the existing 500 ms tick asks again for a start-up command that had to wait. Nothing before the
    /// start-up step has looked at it; StartupCommandWait.Take clears what it hands out, so the step and the ticks run it once.</summary>
    private void FollowStartupCommand()
    {
        if (_startupCommandClock == null) return;
        if (StartupCommandWait.Take(ref _startupCommand, StartupRunningNames(), _startupCommandClock.Elapsed) is { } first) RunCommand(first);
    }

    private const string CommandsOffEnglish = "EVE MultiPreview is already running but did not take the command. Turn on Take commands from other programs under App settings › General. If MultiPreview runs as administrator, turn on Also take commands from programs that are not run as administrator there, or run this program as administrator too. Otherwise try again once MultiPreview has finished starting.";

    /// <summary>IDEAS #39, a second start whose command nothing took: the running MultiPreview does not take commands, runs as
    /// another kind of user (one elevated, one not) or is still starting; the text names all three. Said in Windows' language:
    /// this start reads no settings.</summary>
    private static void TellCommandsOff()
    {
        UseWindowsLanguage();
        MessageBox.Show(LocalizationService.Str("L.Msg.CommandsOff", CommandsOffEnglish), "EVE MultiPreview",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>IDEAS P121, a second start with no command: says that MultiPreview already runs, in Windows' language as
    /// the note above does (this start reads no settings).</summary>
    private static void TellAlreadyRunning()
    {
        UseWindowsLanguage();
        MessageBox.Show(LocalizationService.Str("L.Msg.AlreadyRunning", "EVE MultiPreview is already running."), "EVE MultiPreview",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>A start that has read no settings yet (a second start, the EVE-X-Preview question) speaks Windows' language.</summary>
    private static void UseWindowsLanguage()
    {
        try { LocalizationService.SetLanguage(null); }
        catch { /* the English text then */ }
    }

    /// <summary>IDEAS #30: the snooze file follows "Keep snoozes when MultiPreview restarts". Called after every change
    /// to the snoozes and after every settings change: on, the file holds the timed snoozes that are on now; off,
    /// there is no file. A session whose settings could not be read writes nothing here either.</summary>
    private void FollowSnoozeSwitch()
    {
        if (_isShuttingDown || _snoozeFile == null || _settings == null || !_settings.LoadedSuccessfully) return;
        _snoozeFile.Follow(_settings.Settings.KeepAlertSnoozes, _snoozes);
    }

    /// <summary>IDEAS #33: the registry key Windows needs, and what MultiPreview has in the Notification Center, follow
    /// "Show alerts as Windows notifications". Called at startup and after every settings change, on the window's
    /// thread. NotificationsWay.Follow has the order: switched off, the notifications go first and then the key.
    /// NotificationActions has the two reasons not to: MultiPreview is closing, or the settings could not be read
    /// (such a session runs on default values and must not remove the player's key).</summary>
    private void FollowNotifications(bool starting = false)
    {
        if (_notificationsWay == null || _notificationRegistration == null) return;
        _notificationActions?.Follow(_notificationsWay, _notificationRegistration, starting);
    }

    /// <summary>IDEAS #33, the card's "Show a test notification": one notification through the way, so the player
    /// sees whether Windows lets MultiPreview's notifications through. False: Windows was not asked for one.</summary>
    private bool ShowTestNotification()
    {
        if (_notificationsWay == null || _notificationRegistration == null) return false;
        return _notificationActions?.ShowTest(_notificationsWay, _notificationRegistration, DateTime.UtcNow) ?? false;
    }

    /// <summary>IDEAS #33, the card's question: is Windows itself not showing MultiPreview's notifications? Asked
    /// from the window's thread, answered on another one.</summary>
    private System.Threading.Tasks.Task<bool> NotificationsBlocked() =>
        _notificationActions != null && _notificationsWay != null && _notificationRegistration != null
            ? _notificationActions.Blocked(_notificationsWay, _notificationRegistration)
            : System.Threading.Tasks.Task.FromResult(false);

    /// <summary>Hands the custom rules (IDEAS #8) to everything that uses them: their names and sounds by alert type
    /// (CustomAlertTypes), and the rules that are on, made ready, to the log monitor. With none on the monitor gets
    /// null, and its reader does nothing more per line than see that.</summary>
    private void ApplyCustomRules()
    {
        if (_isShuttingDown || _customAlerts == null || _logMonitor == null) return;
        var rules = _customAlerts.Rules;
        CustomAlertTypes.Publish(rules);
        _logMonitor.SetCustomRules(CustomAlertEngine.Build(rules, ErrorLog.Shared));
    }

    // ── Alert Sound System (per-event sounds, WAV/MP3 via MediaPlayer) ──

    // P57: the log reader's thread calls this for every alert, so it posts the play to the window's thread and returns; a busy
    // window no longer holds every client's alerts. The play is the member below, unchanged, run there in the order the
    // alerts were posted (its quiet-time check and stamp keep their order). The repeats (AlertTick, on the window's
    // thread) call that member directly. A throw inside a posted lambda would reach the unhandled-exception handler, so
    // it is recorded here, by type: a message can quote a name another player chose. A lambda that reaches the window's
    // thread after the exit began plays nothing and records nothing.
    private void PlayAlertSound(string character, string alertType, string severity) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isShuttingDown) return;
            try { PlayAlertSound(character, alertType, severity, repeat: 0, volume: null); }
            catch (Exception ex)
            {
                ErrorLog.Shared.Record(new InvalidOperationException("An alert sound could not be played: " + ex.GetType().Name), "App:Sound", DateTime.Now);
            }
        }));

    /// <param name="repeat">0 for the alert itself; 1, 2, … for a repeat of escalation (IDEAS #29).</param>
    /// <param name="volume">The volume of a repeat (0–100) as escalation worked it out, each one louder. Null for the
    /// alert itself: the set volume.</param>
    /// <returns>Whether the sound was started, was put off (do-not-disturb, the event's sound quiet time), or has
    /// nothing to play.</returns>
    private SoundResult PlayAlertSound(string character, string alertType, string severity, int repeat, int? volume)
    {
        var s = _settings?.Settings;
        // EnableAlertSounds is the field the "Enable Sounds" master checkbox writes
        // (#settings-audit). The old gate read AlertSoundEnabled, which no UI ever set,
        // so the master toggle did nothing. Read the UI-backed field.
        if (s == null || !s.EnableAlertSounds)
        {
            // A repeat with "Play a sound" off is counted as done without a sound (IDEAS #29): it says nothing here.
            if (repeat == 0) EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                $"[Sound] GATED off: EnableAlertSounds={s?.EnableAlertSounds} for {alertType} on '{character}'");
            return SoundResult.Never;
        }
        // Do-not-disturb (IDEAS #32) mutes the sound and the voice (SpokenWay asks it as this does), nothing else: the
        // border, the badge and the toast were shown before this is asked. A critical alert still plays, and is still
        // spoken, unless the player turned that off.
        if (_doNotDisturb.Silences(s, severity, DateTime.Now))
        {
            // A repeat that has to wait is asked for again every second: it writes nothing each time (IDEAS #29).
            if (repeat == 0) EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                $"[Sound] GATED do-not-disturb: {alertType} [{severity}]");
            return SoundResult.Later;
        }
        if (repeat == 0) EveMultiPreview.Services.DiagnosticsService.LogAlerts(
            $"[Sound] enter: {alertType} on '{character}' | EnableAlertSounds={s.EnableAlertSounds} " +
            $"volume={s.AlertSoundVolume} eventFile='{(s.AlertSounds != null && s.AlertSounds.TryGetValue(alertType, out var ef) ? ef : "")}' " +
            $"globalFile='{s.AlertSoundFile}'");

        // Per-character per-event sound cooldown check. Keying by character means
        // simultaneous alerts on different clients (e.g. five chars depleting the
        // same rock) each get their sound — only repeats from the *same* char on
        // the *same* event are coalesced.
        string cooldownKey = $"sound_{character}_{alertType}";
        int soundCooldown = s.SoundCooldowns?.GetValueOrDefault(alertType, 0) ?? 0;
        bool played;
        DateTime lastPlay;
        lock (_soundCooldownLock) played = _soundCooldowns.TryGetValue(cooldownKey, out lastPlay);
        if (soundCooldown > 0 && played)
        {
            if (AlertSoundChoice.StillWaits(lastPlay, DateTime.UtcNow, soundCooldown))
            {
                if (repeat == 0)
                {
                    Debug.WriteLine($"[AlertSound:Cooldown] ⏳ Sound cooldown active: {alertType} for '{character}' ({soundCooldown}s)");
                    EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                        $"[Sound] GATED cooldown: {alertType} on '{character}' within {soundCooldown}s of last play");
                }
                return SoundResult.Later;
            }
        }

        // The sound of the character's group first (IDEAS #58; where its file is gone, what follows); else the
        // event's own sound file, else the default one; a custom rule's own before both (IDEAS #8), and the
        // default one when the rule's own file is no longer there.
        string? soundFile = AlertSoundChoice.FileToPlay(s, alertType, System.IO.File.Exists, _groupSounds.For(character, alertType));

        if (string.IsNullOrEmpty(soundFile) || !System.IO.File.Exists(soundFile))
        {
            // Said for the alert itself. Its repeats find the same and say nothing more.
            if (repeat == 0)
            {
                Debug.WriteLine($"[AlertSound:Play] ⚠ No sound file for {alertType} (file='{soundFile ?? "null"}')");
                EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                    $"[Sound] GATED no-file: {alertType} on '{character}' resolved='{soundFile ?? "null"}' " +
                    $"(exists={(!string.IsNullOrEmpty(soundFile) && System.IO.File.Exists(soundFile))})");
            }
            return SoundResult.Never;
        }

        int loud = volume ?? AlertEscalation.Volume(s.AlertSoundVolume, 0, false);
        try
        {
            // Each invocation gets its own MediaPlayer so concurrent alerts don't
            // cancel each other (a shared MediaPlayer's second Open() unloads the
            // first's media before it finishes playing). The instance is held in
            // _activeSoundPlayers until MediaEnded fires, then disposed.
            Dispatcher.Invoke(() =>
            {
                var player = new MediaPlayer { Volume = loud / 100.0 };
                player.MediaEnded += (_, _) =>
                {
                    player.Close();
                    lock (_soundPlayerLock) { _activeSoundPlayers.Remove(player); }
                };
                player.MediaFailed += (_, args) =>
                {
                    EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                        AlertSoundLog.MediaFailedLine(repeat, alertType, character, soundFile, args.ErrorException?.Message));
                    player.Close();
                    lock (_soundPlayerLock) { _activeSoundPlayers.Remove(player); }
                };
                lock (_soundPlayerLock) { _activeSoundPlayers.Add(player); }
                player.Open(new Uri(soundFile));
                player.Play();
            });

            var playedAt = DateTime.UtcNow;
            lock (_soundCooldownLock) _soundCooldowns[cooldownKey] = playedAt;
            if (repeat == 0)
            {
                Debug.WriteLine($"[AlertSound:Play] 🔊 Playing '{System.IO.Path.GetFileName(soundFile)}' for {alertType} on '{character}' (vol={s.AlertSoundVolume}%)");
                EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                    $"[Sound] PLAYING '{System.IO.Path.GetFileName(soundFile)}' for {alertType} on '{character}' at vol={s.AlertSoundVolume}%"
                    + (s.AlertSoundVolume == 0 ? " ⚠ VOLUME IS 0 — will be inaudible" : ""));
            }
            else
            {
                // A repeat says which one it is and how loud it really was. It names the event and nobody.
                EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                    $"[Sound] PLAYING repeat {repeat} of {alertType} at vol={loud}%");
            }
            return SoundResult.Played;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlertSound:Play] ❌ Error playing sound: {ex.Message}");
            EveMultiPreview.Services.DiagnosticsService.LogAlerts(AlertSoundLog.PlayErrorLine(repeat, alertType, character, ex));
            return SoundResult.Never;
        }
    }

    // ── Tray Menu (matches AHK TrayMenu.ahk) ────────────────────────

    /// <summary>IDEAS P4: the one place the tray icon is chosen: the pause icon while hotkeys are suspended, else the
    /// red one while a critical alert is unseen, else the ordinary one (<see cref="TrayAlertState"/>). Called at start-up,
    /// when suspend toggles and whenever <see cref="AlertAttention.Changed"/> is raised, which can be on any thread, so
    /// it hops to the UI thread first. It sets the icon only when the choice changed, and never raises an error.</summary>
    private void ApplyTrayIcon()
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ApplyTrayIcon));
                return;
            }
            if (_trayIcon == null || _isShuttingDown) return;
            var kind = TrayAlertState.From(_thumbnailManager?.Attention.Unseen(), _hotkeyService?.IsSuspended ?? false);
            if (kind == _trayIconShown) return;
            // Icons come from the embedded resources (works with single-file publish).
            _trayIcons ??= new TrayIcons(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream);
            _trayIcon.Icon = _trayIcons.For(kind);
            _trayIconShown = kind;
        }
        catch { /* an icon must never raise another error */ }
    }

    private void SetupTrayIcon()
    {
        // Views/TrayMenu builds the menu (Plan 4: the Fluent look, check marks that follow the live state, and lists
        // rebuilt on every open). Each action is the call the old menu made.
        _trayMenu = new TrayMenu(new TrayMenuActions
        {
            Settings = () => _settings?.Settings,
            ProfileNames = () => _settings?.GetProfileNames() ?? Array.Empty<string>(),
            OpenSettings = () =>
            {
                // Deferred so the tray menu has fully closed before Show(); otherwise the NotifyIcon's message window
                // takes the foreground back and Settings appears but can't receive input.
                SettingsDiag("Tray menu item clicked");
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => OpenSettings()));
            },
            OpenSettingsPage = page =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => OpenSettings(startMinimized: false, pageId: page))),
            OpenAlertHistory = () => Application.Current?.Dispatcher.BeginInvoke(new Action(OpenAlertHistory)),
            OpenStatReport = () => Application.Current?.Dispatcher.BeginInvoke(new Action(OpenStatReport)),
            OpenFleetBoard = () => Application.Current?.Dispatcher.BeginInvoke(new Action(OpenFleetBoard)),
            OpenHotkeySheet = () => Application.Current?.Dispatcher.BeginInvoke(new Action(ToggleHotkeySheet)),
            SwitchProfile = name => _settings?.SwitchProfile(name),   // ProfileSwitched applies it (#101)
            IsSuspended = () => _hotkeyService?.IsSuspended ?? false,
            ToggleSuspend = () => _hotkeyService?.ToggleSuspend(),
            DoNotDisturbOn = () => DoNotDisturbOn,
            ToggleDoNotDisturb = ToggleDoNotDisturb,
            FocusModeOn = () => _focusModeOn,
            ToggleFocusMode = ToggleFocusMode,
            StreamerModeOn = () => StreamerMask.IsOn,
            ToggleStreamerMode = ToggleStreamerMode,
            ToggleLock = () => _thumbnailManager?.ToggleLockPositions(),
            ThumbnailsHidden = () => _thumbnailManager?.AllThumbnailsHidden ?? false,
            ToggleThumbnails = () => _thumbnailManager?.ToggleThumbnailVisibility(),
            ClickThroughOn = () => _thumbnailManager?.ClickThroughOn ?? false,
            ToggleClickThrough = () => _thumbnailManager?.ToggleClickThrough(),
            SecondaryHidden = () => _thumbnailManager?.SecondaryThumbnailsHidden ?? false,
            ToggleSecondary = () => _thumbnailManager?.ToggleSecondaryVisibility(),
            SetSecondary = (name, on) =>
            {
                // AHK: TrayMenu._TrayPiPToggle
                var pips = _settings?.Settings.SecondaryThumbnails;   // the list the tray shows reads the same source
                if (pips == null || !pips.TryGetValue(name, out var pip)) return;
                pip.IsEnabled = on;
                if (on) _thumbnailManager?.CreateSecondaryForCharacter(name);
                else _thumbnailManager?.DestroySecondaryForCharacter(name);
                _settings?.Save();
            },
            SavePositions = () =>
            {
                _thumbnailManager?.SaveClientPositions();
                ShowBalloon(2000, LocalizationService.Str("L.Tray.PosSaved", "Client positions saved"), ToolTipIcon.Info);
            },
            RestorePositions = () =>
            {
                // Used to only show this balloon: restore ran solely for newly detected
                // clients, so already-open clients never moved.
                _thumbnailManager?.RestoreAllClientPositions();
                ShowBalloon(2000, LocalizationService.Str("L.Tray.PosRestored", "Client positions restored"), ToolTipIcon.Info);
            },
            SnapToSlots = () => _thumbnailManager?.ApplyAllClientSlots(),
            ConfirmCloseAll = () => System.Windows.MessageBox.Show(
                LocalizationService.Str("L.Tray.CloseConfirm", "Close all EVE Online windows?"),
                LocalizationService.Str("L.Tray.ConfirmTitle", "Confirm"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes,   // IDEAS P92: Enter keeps the clients
            CloseAll = () => _thumbnailManager?.CloseAllEveWindows(),
            Exit = ExitApplication,
        });

        _trayIcon = new NotifyIcon
        {
            Text = TrayMenu.TooltipText(_hotkeyService?.IsSuspended ?? false, ErrorLog.Shared.Count, DoNotDisturbOn, _focusModeOn),
            ContextMenuStrip = _trayMenu.Strip
        };
        // IDEAS P4: the icon is chosen in one place, with the icon in before the tray shows it, and again whenever
        // an alert is taken or looked at. The follower goes on first: an alert raised between the first choice and the
        // subscription would otherwise never turn the icon.
        if (_thumbnailManager != null) _thumbnailManager.Attention.Changed += ApplyTrayIcon;
        ApplyTrayIcon();
        _trayIcon.Visible = true;

        // A click on a tray balloon. The one balloon a click means something on is the note about refused hotkeys
        // (IDEAS #42). No alert shows a balloon any more: an alert's own notification is a toast of the alert hub,
        // or a Windows notification (IDEAS #33), and each has its own click.
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (_refusalNote.Clicked())
            {
                // IDEAS #42: deferred, as the tray menu's Settings item is, so the note has gone before the window shows.
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    OpenSettings(startMinimized: false, pageId: SettingsUi.ClientsPages.HotkeysId)));
            }
        };

        _trayIcon.BalloonTipClosed += (_, _) => _refusalNote.Closed();

        var menu = _trayMenu.Strip;

        // Pause the thumbnail focus sweep while the menu is up. Opening the menu
        // makes this app foreground, and the sweep would otherwise re-show and
        // re-raise thumbnails over it — closing or burying the menu.
        // And no thumbnail stands enlarged for an alert under it (FX-m2): counted as a thumbnail's menu is.
        var counted = new ThumbnailWindow.AppMenu(() => menu.Visible);
        if (_thumbnailManager != null) _thumbnailManager.TrayMenuShown = () => menu.Visible;
        menu.Opening += (_, _) =>
        {
            _thumbnailManager?.SetTrayMenuOpen(true);
            counted.Note(true);
            SettingsDiag("Tray menu opening");
        };
        menu.Opened += (_, _) => SettingsDiag("Tray menu opened");
        menu.Closing += (_, e) => SettingsDiag($"Tray menu closing, reason={e.CloseReason}");
        menu.Closed += (_, _) =>
        {
            _thumbnailManager?.SetTrayMenuOpen(false);
            counted.Note(false);
        };

        // Double-click to open settings (deferred — see the Settings item above)
        _trayIcon.DoubleClick += (_, _) =>
        {
            SettingsDiag("Tray double-click");
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => OpenSettings()));
        };
    }

    /// <summary>
    /// Cycles to the next/previous profile — mirrors AHK Main_Class.CycleProfile (L670-710).
    /// AHK: enumerates profile names → finds current → wraps forward/backward → saves → Reload().
    /// C# equivalent: SwitchProfile + re-register hotkeys (no full app reload needed).
    /// </summary>
    private void CycleProfile(bool forward)
    {
        if (_settings == null) return;

        var profileNames = _settings.GetProfileNames();
        if (profileNames.Length <= 1) return; // Only one profile, nothing to cycle

        // Find current profile index
        int currentIdx = Array.IndexOf(profileNames, _settings.Settings.LastUsedProfile);
        if (currentIdx < 0) currentIdx = 0;

        // Calculate next index with wrapping (AHK L692-700)
        int nextIdx;
        if (forward)
        {
            nextIdx = currentIdx + 1;
            if (nextIdx >= profileNames.Length) nextIdx = 0;
        }
        else
        {
            nextIdx = currentIdx - 1;
            if (nextIdx < 0) nextIdx = profileNames.Length - 1;
        }

        var newProfile = profileNames[nextIdx];
        _settings.SwitchProfile(newProfile);   // ProfileSwitched applies it (#101)

        // Tooltip feedback (AHK L705: ToolTip("Profile: " newProfile))
        _thumbnailManager?.ShowTooltipFeedback(string.Format(LocalizationService.Str("L.Tray.ProfileFmt", "Profile: {0}"), StreamerMask.Name(StreamerKind.Profile, newProfile)));

        Debug.WriteLine($"[App:Profile] 🔄 Cycled {(forward ? "forward" : "backward")} to profile: {newProfile}");
    }

    private void OpenSettings() => OpenSettings(startMinimized: false);

    /// <param name="pageId">Open on this page (Plan 4: a thumbnail's "Open ‹character›'s settings", the tray's Manage
    /// profiles…). Only the new window has pages; the old one opens as usual.</param>
    private void OpenSettings(bool startMinimized, string? pageId = null)
    {
        SettingsDiag($"OpenSettings called, startMinimized={startMinimized}, page={pageId ?? "-"}, existing={_settingsWindow != null}");
        if (_settingsWindow != null && _settingsWindow.IsVisible)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;

            try { SettingsUi.SettingsShell.OpenOn(_settingsWindow, pageId); }   // already open: move it to the page, then to the front
            catch (Exception ex) { LogError(ex); }   // a failed navigation still brings the window forward
            _settingsWindow.Activate();
            SettingsDiag("Re-activated existing Settings window");
            return;
        }

        Action applyLiveSettings = () =>
        {
            if (_isShuttingDown) return;

            // Reload hotkeys when settings change
            _hotkeyService?.RegisterFromSettings(
                _settings!.Settings, _settings.CurrentProfile,
                _thumbnailManager!, OpenSettings);

            // Re-wire log monitor settings
            if (_logMonitor != null && _settings != null)
            {
                _logMonitor.PveMode = _settings.Settings.PveMode;
                _logMonitor.SetCooldown(_settings.Settings.AlertCooldown);
                ApplyAlertSwitches();   // IDEAS #31: which events are on, for the log monitor and for the gate
                if (_settings.Settings.SeverityCooldowns != null)
                    _logMonitor.SetEventCooldowns(_settings.Settings.SeverityCooldowns);
                _logMonitor.SetSeverityCooldowns(LogMonitorService.SeverityWaits(_settings.Settings));   // IDEAS P28
                // The two log switches and folders apply live (IDEAS P30): the reading thread changes folders before
                // its next read and keeps each file's place. The same folders again change nothing.
                _logMonitor.Reconfigure(_settings.Settings.ChatLogDirectory, _settings.Settings.GameLogDirectory,
                    _settings.Settings.EnableChatLogMonitoring, _settings.Settings.EnableGameLogMonitoring);
            }

            // The alert history follows its switch at once (IDEAS #1). A changed keep time deletes nothing until the
            // next start. Nothing is recorded in a session whose settings could not be read (see where it is made).
            if (_alertHistory != null && _settings != null)
            {
                _alertHistory.Enabled = _settings.LoadedSuccessfully && _settings.Settings.AlertHistoryEnabled;
                _alertHistory.KeepDays = _settings.Settings.AlertHistoryDays;
            }

            FollowSnoozeSwitch();   // IDEAS #30: the snooze file is written or removed as its switch says
            FollowAlertTimer();     // IDEAS #29: the one-second look starts or stops with what needs it
            ApplySavedLook();       // UI styles: an import, or the setup wizard, may have picked another look
            FollowNotifications();  // IDEAS #33: the registry key is written or removed as its switch says
            _spokenWay?.Follow(_settings!.Settings.SpeakAlerts);   // FX-m4: off and on again counts the voice's asks anew
            _pushWay?.Follow(_settings!.Settings);   // IDEAS #2: a newly saved link or a switch takes effect now
            FollowCommandServer();   // IDEAS #39: the pipe opens or closes with its switch
            FollowGamepad();         // IDEAS #43: the pad is read, or not, as its switch says; the lines are read again
            FollowNameBar();         // IDEAS #38: the bar and the hold on the thumbnails follow their switch at once
            if (StreamerMask.IsOn != _streamerMaskDrawn)   // IDEAS #19: its names follow streamer mode at once, once per flip
            {
                _streamerMaskDrawn = StreamerMask.IsOn;
                _nameBar?.Refresh();   // IDEAS #38
                _historyWindow?.RefreshStreamerMask();
                _statReportWindow?.RefreshStreamerMask();
                _fleetBoardWindow?.RefreshStreamerMask();
            }

            // Stats logging applies live too (IDEAS P31); unchanged values don't rescan the log folder.
            if (_statTracker != null && _settings != null)
            {
                _statTracker.SetCsvLogging(_settings.Settings.StatLoggingEnabled, _settings.Settings.StatLogDirectory,
                    _settings.Settings.StatLogRetentionDays);
                _statTracker.CountNpcDamage = !_settings.Settings.StatsExcludeNpcDamage;   // IDEAS P32
            }

            // Dynamically show/hide the Alert Hub based on settings toggle
            if (_isShuttingDown) return;
            try
            {
                // Toast length and stacking side were read only when the hub was made (Plan 3c).
                _alertHub?.ApplySettings();
                if (_settings != null && _settings.Settings.AlertHubEnabled)
                    _alertHub?.Show();
                else
                    _alertHub?.Hide();
            }
            catch (InvalidOperationException)
            {
                // Ignore if the window was closed during shutdown
            }
        };

        // UseLegacySettingsWindow (spec §12): the old window stays reachable for a release or two.
        Window? opened = null;
        if (!_settings!.Settings.UseLegacySettingsWindow)
        {
            try
            {
                var shell = new SettingsUi.SettingsShell(_settings!, new SettingsUi.AppServices
                {
                    Thumbnails = _thumbnailManager,
                    Crops = _cropManager,
                    Hotkeys = _hotkeyService,
                    CheckHotkeysAgain = CheckHotkeysAgain,
                    AlertHub = _alertHub,
                    OpenAlertHistory = OpenAlertHistory,
                    Snoozes = _snoozes,
                    SnoozeStarted = (who, type) => { _notificationsWay?.Snoozed(who, type); _thumbnailManager?.PutBackForASnooze(who); },
                    DoNotDisturbOn = () => DoNotDisturbOn,
                    Speaker = _speaker,
                    VoiceAnswered = () => _spokenWay?.VoiceAnswered(),
                    NotificationsBlocked = NotificationsBlocked,
                    TestNotification = ShowTestNotification,
                    TestPush = () => _pushWay?.TestAsync(_settings.Settings) ?? System.Threading.Tasks.Task.FromResult(new PushResult(PushOutcome.Refused)),
                    PushStatus = () => _pushWay?.Status,
                    CustomAlerts = _customAlerts,
                    // Which custom rules the log monitor has switched off for what they cost: the page says it on their cards.
                    CustomRulesSwitchedOff = () => _logMonitor?.CustomRulesSwitchedOff ?? CustomAlertEngine.NoneSwitchedOff,
                    // Whether a running client's log header is not English: the Events table says so on the rows read from an English log only.
                    LogHeaderNotEnglish = () => _logMonitor?.NonEnglishLogCharacters.Count > 0,
                    ExitForUninstall = ExitForUninstall,
                });
                shell.SettingsApplied += applyLiveSettings;
                opened = shell;
            }
            catch (Exception ex)
            {
                // Without this, a failing new window is swallowed by the dispatcher and Settings does nothing, every time.
                // The setting is left alone: a transient failure must not flip the player's choice for good.
                LogError(ex);
                SettingsDiag($"New settings window failed to open ({ex.GetType().Name}: {ex.Message}); opening the old one");
            }
        }
        if (opened == null)
        {
            var legacy = new SettingsWindow(_settings!, _thumbnailManager!, _cropManager);
            legacy.SettingsApplied += applyLiveSettings;
            opened = legacy;
        }
        _settingsWindow = opened;
        // After the try above: a failed navigation leaves the new window on its default page, not a hidden shell behind.
        try { SettingsUi.SettingsShell.OpenOn(opened, pageId); }
        catch (Exception ex) { LogError(ex); }

        // Set initial WindowState BEFORE Show() so WPF commits the restore rect
        // with the saved Width/Height already applied. Setting WindowState after
        // Show() can race the HWND map and corrupt the restore rect.
        if (startMinimized)
        {
            _settingsWindow.WindowState = WindowState.Minimized;
        }
        else
        {
            if (_settingsWindow is SettingsWindow)
            {
                // Auto-maximize when the saved size doesn't fit the work area —
                // mostly catches 1080p users on the default 1080×1080 size, where
                // the bottom of the panel would be hidden under the taskbar. Once
                // the user resizes to something that fits, the saved smaller size
                // is respected on subsequent opens. WorkArea is in DIPs, matching
                // WPF's Width/Height semantics.
                var workArea = SystemParameters.WorkArea;
                int savedW = _settings!.Settings.SettingsWindowWidth;
                int savedH = _settings!.Settings.SettingsWindowHeight;
                if (savedH >= workArea.Height || savedW >= workArea.Width)
                {
                    _settingsWindow.WindowState = WindowState.Maximized;
                    SettingsDiag($"Auto-maximized: saved {savedW}x{savedH} doesn't fit work area {workArea.Width}x{workArea.Height}");
                }
            }
        }

        var shellHwnd = IntPtr.Zero;   // set below, once the window has a handle; Closed only clears its own
        _settingsWindow.Closed += (_, _) =>
        {
            SettingsDiag("Closed event fired");
            // Ensure topmost + click-through are restored when settings closes
            _thumbnailManager?.SetSuppressTopmost(false);
            _thumbnailManager?.SetSettingsClickSuppression(false);
            _thumbnailManager?.SetSettingsOpen(false);

            _settingsWindow = null;
            if (_hotkeyService != null && _hotkeyService.SettingsWindowHandle == shellHwnd) _hotkeyService.SettingsWindowHandle = IntPtr.Zero;
            if (!_isShuttingDown)
            {
                applyLiveSettings();
            }

            Debug.WriteLine("[App:Settings] ⚙ Settings window closed — services re-configured");
        };

        // WinForms thumbnail windows hit-test their full client rect, so any
        // z-order dance (Topmost, pin-below, SetWindowPos) is fragile — tray
        // opens, minimize transitions, and foreground-lock all break it in
        // different ways. Instead, flip thumbnails click-through while Settings
        // is open: every click passes through them to whatever is below.
        _settingsWindow.Activated += (_, _) =>
        {
            SettingsDiag("Activated event fired");
            _thumbnailManager?.SetSuppressTopmost(true);
            _thumbnailManager?.SetSettingsClickSuppression(true);
        };
        _settingsWindow.Deactivated += (_, _) =>
        {
            SettingsDiag("Deactivated event fired");
            _thumbnailManager?.SetSuppressTopmost(false);
            _thumbnailManager?.SetSettingsClickSuppression(false);
        };
        _settingsWindow.IsVisibleChanged += (_, e) =>
            SettingsDiag($"IsVisibleChanged → {e.NewValue}");
        _settingsWindow.StateChanged += (_, _) =>
            SettingsDiag($"StateChanged → {_settingsWindow?.WindowState}");

        SettingsDiag("About to call Show()");
        _settingsWindow.Show();
        SettingsDiag($"Show() returned. IsVisible={_settingsWindow.IsVisible}, IsEnabled={_settingsWindow.IsEnabled}, State={_settingsWindow.WindowState}, Left={_settingsWindow.Left}, Top={_settingsWindow.Top}, W={_settingsWindow.Width}, H={_settingsWindow.Height}");

        // Topmost-flip forces Settings to the top of the z-band without leaving
        // it permanently topmost. Covers the case where Show() doesn't grant
        // foreground rights (tray-menu paths, foreground-lock policy).
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Activate();

        var settingsHwnd = new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Handle;
        // Hotkeys stay quiet while settings is in front. The new window's title is translated, so the service gets its
        // handle instead of looking for "Settings" in the title (IDEAS P23).
        shellHwnd = settingsHwnd;
        if (_hotkeyService != null) _hotkeyService.SettingsWindowHandle = settingsHwnd;
        var fg = Interop.User32.GetForegroundWindow();
        SettingsDiag($"After Activate: settingsHwnd=0x{settingsHwnd.ToInt64():X}, foreground=0x{fg.ToInt64():X}, match={settingsHwnd == fg}");

        // Tray-menu opens don't always fire Activated. Apply suppressions
        // synchronously so Settings is usable regardless of how it was opened.
        _thumbnailManager?.SetSuppressTopmost(true);
        _thumbnailManager?.SetSettingsClickSuppression(true);
        _thumbnailManager?.SetSettingsOpen(true);
        SettingsDiag("OpenSettings finished");
    }

    /// <summary>Opens the alert history (IDEAS #1), or brings it forward when it is already open. The ways in: the
    /// alert hub's menu, the tray menu, and Alerts › Alert hub in the settings window.</summary>
    private void OpenAlertHistory()
    {
        if (_isShuttingDown) return;
        if (_alertHistory == null || _settings == null)
        {
            // The tray menu is there before the deferred startup made the history. A moment later it opens.
            Debug.WriteLine("[App:History] the alert history was asked for before startup had made it: not opened");
            return;
        }
        if (_historyWindow != null)
        {
            if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
            _historyWindow.Activate();
            return;
        }
        try
        {
            _streamerMaskDrawn = StreamerMask.IsOn;   // IDEAS #19: the window is drawn for the mode as it is now, so a later flip redraws it
            _historyWindow = new AlertHistoryWindow(_alertHistory, _settings.Settings, new AlertHistoryActions
            {
                SwitchTo = SwitchToCharacter,
                PickSaveFile = suggested =>
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggested, DefaultExt = ".csv", Filter = "CSV (*.csv)|*.csv" };
                    return dialog.ShowDialog(_historyWindow) == true ? dialog.FileName : null;
                },
                Confirm = (title, body) =>
                    MessageBox.Show(_historyWindow!, body, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes,   // IDEAS P92: Enter keeps the history
                // Asked at every refresh: while the setting is off, the window says so above its list.
                HistoryOn = () => _settings.Settings.AlertHistoryEnabled,
                // A settings file that could not be read means nothing is recorded; the window says that, not that the history is off.
                SettingsLoaded = () => _settings.LoadedSuccessfully,
            });
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        catch (Exception ex)
        {
            _historyWindow = null;
            LogError(ex);
        }
    }

    /// <summary>Opens the session reports (IDEAS #20), or brings them forward when they are already open. They read the stat
    /// log folder of Tools › Stats (StatLogDirectory) and nothing else; the way in is the tray menu.</summary>
    private void OpenStatReport()
    {
        if (_isShuttingDown || _settings == null) return;
        if (_statReportWindow != null)
        {
            if (_statReportWindow.WindowState == WindowState.Minimized) _statReportWindow.WindowState = WindowState.Normal;
            _statReportWindow.Activate();
            return;
        }
        try
        {
            _streamerMaskDrawn = StreamerMask.IsOn;   // IDEAS #19: drawn for the mode as it is now, so a later flip redraws it
            _statReportWindow = new StatReportWindow(new StatReportActions { Folder = () => _settings.Settings.StatLogDirectory });
            _statReportWindow.Closed += (_, _) => _statReportWindow = null;
            _statReportWindow.Show();
        }
        catch (Exception ex)
        {
            _statReportWindow = null;
            LogError(ex);
        }
    }

    /// <summary>Opens the fleet board (IDEAS #18), or brings it forward when it is already open. It reads, once a second while
    /// it is open, what the app already knows: the thumbnails' characters, the log monitor's system and time in it, the process
    /// monitor's CPU and RAM, and the alert history. It changes nothing; the way in is the tray menu.</summary>
    private void OpenFleetBoard()
    {
        if (_isShuttingDown || _settings == null || _thumbnailManager == null) return;
        if (_fleetBoardWindow != null)
        {
            if (_fleetBoardWindow.WindowState == WindowState.Minimized) _fleetBoardWindow.WindowState = WindowState.Normal;
            _fleetBoardWindow.Activate();
            return;
        }
        try
        {
            _streamerMaskDrawn = StreamerMask.IsOn;   // IDEAS #19: drawn for the mode as it is now, so a later flip redraws it
            _fleetBoardWindow = new FleetBoardWindow(new FleetBoardActions
            {
                Inputs = () => FleetBoardRows.Gather(_thumbnailManager!.SnapshotCharacters(),
                    _alertHistory?.Recent() ?? Array.Empty<AlertHistoryEntry>(),
                    name => _logMonitor?.GetCharacterSystem(name),
                    name => _logMonitor?.GetSystemSinceUtc(name),
                    pid => _processMonitor?.GetStats(pid),
                    name => _logMonitor?.IsSystemSinceApproximate(name) ?? false),
                // The type only: an exception's message may hold a character's name, and the error log writes messages.
                Failed = type => ErrorLog.Shared.Record(new InvalidOperationException("the fleet board refresh failed: " + type), "FleetBoard", DateTime.Now),
            });
            _fleetBoardWindow.Closed += (_, _) => _fleetBoardWindow = null;
            _fleetBoardWindow.Show();
        }
        catch (Exception ex)
        {
            _fleetBoardWindow = null;
            LogError(ex);
        }
    }

    /// <summary>IDEAS #16: opens the hotkey cheat sheet over the thumbnails, or closes it when it is open. The ways in: its
    /// hotkey and the tray menu. The rows come from the list the conflict check reads (HotkeySheet.Rows), so the sheet,
    /// the conflict check and the refused-keys card never disagree. A failure is logged: a key press must not throw.</summary>
    private void ToggleHotkeySheet()
    {
        try
        {
            if (_isShuttingDown || _settings == null) return;
            if (_hotkeySheet != null)
            {
                var open = _hotkeySheet;
                _hotkeySheet = null;
                open.Dismiss();
                return;
            }
            var rows = SettingsUi.HotkeySheet.Rows(_settings.Settings, SettingsUi.SettingsRegistry.Settings);
            var sheet = new Views.HotkeySheetWindow(rows);
            sheet.Closed += (_, _) => { if (ReferenceEquals(_hotkeySheet, sheet)) _hotkeySheet = null; };
            _hotkeySheet = sheet;
            sheet.Show();
        }
        catch (Exception ex)
        {
            _hotkeySheet = null;
            LogError(ex);
        }
    }

    /// <summary>Plan 38: --name-bar, --name-bar-on and --name-bar-off set the switch the settings card sets (NameBarMode, saved),
    /// then the bar follows it at once (FollowNameBar) and the near-cursor note says what it is now. The same answer again
    /// changes nothing. A failure is logged.</summary>
    private void SetNameBarMode(bool on)
    {
        try
        {
            if (_isShuttingDown || _settings == null || _settings.Settings.NameBarMode == on) return;
            _settings.Settings.NameBarMode = on;
            _settings.Save();
            FollowNameBar();
            var (key, english) = on ? ("L.NameBar.On", "Name bar: on") : ("L.NameBar.Off", "Name bar: off");
            _thumbnailManager?.ShowTooltipFeedback(LocalizationService.Str(key, english));
        }
        catch (Exception ex) { LogError(ex); }
    }

    /// <summary>IDEAS #38, at each look of the half-second timer and when settings apply: the compact name bar follows its switch.
    /// On, the main thumbnails are held hidden and the bar is made (or asked for its buttons again); off, the bar is closed
    /// (its preview with it) and the thumbnails are let go. A bar that could not be made is logged once and the hold is let go,
    /// so the thumbnails come back; the next try waits for the switch to go off and on. A failure is logged.</summary>
    private void FollowNameBar()
    {
        Views.NameBarWindow? bar = null;      // kept outside the try: a failure part-way closes what was made
        Views.NameBarPreview? preview = null;
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return;
            if (!_settings.Settings.NameBarMode)
            {
                _nameBarFailed = false;
                var open = _nameBar;
                _nameBar = null;
                open?.Close();
                _thumbnailManager.SetNameBarHold(false);
                return;
            }
            if (_nameBarFailed) return;
            if (_nameBar != null) { _nameBar.Refresh(); return; }
            var thumbnails = _thumbnailManager;
            preview = new Views.NameBarPreview();
            bar = new Views.NameBarWindow(NameBarEntries, name => SwitchToCharacter(name),
                name => thumbnails.GetHwndForCharacter(name), preview);   // the bar masks its own names at each Refresh, so the history window's drawn-mode flag is not set here
            var made = bar;
            made.Closed += (_, _) => { if (ReferenceEquals(_nameBar, made)) _nameBar = null; };
            _nameBar = made;
            thumbnails.SetNameBarHold(true);
            made.Show();
        }
        catch (Exception ex)
        {
            _nameBarFailed = true;
            _nameBar = null;
            try { bar?.Close(); preview?.Dispose(); }   // what was made goes with the failure: the bar (its Closed gives its preview up) and a preview no bar took
            catch (Exception cleanup) { LogError(cleanup); }
            _thumbnailManager?.SetNameBarHold(false);
            LogError(ex);
        }
    }

    /// <summary>IDEAS #38: the name bar's buttons now: the running characters in the quick-switch wheel's order, each with the
    /// colour of its first colour group, its worst unseen alert and whether its client is in front. Read from what the app knows.</summary>
    private IReadOnlyList<NameBarEntry> NameBarEntries()
    {
        if (_settings == null || _thumbnailManager == null) return Array.Empty<NameBarEntry>();
        var s = _settings.Settings;
        var thumbnails = _thumbnailManager;
        return NameBar.Entries(thumbnails.SnapshotCharacters().Select(c => c.Name).ToList(),
            s.ThumbnailGroups.Select(g => (g.Name, (string?)g.Color, (IReadOnlyList<string>)g.Members)).ToList(),
            name => thumbnails.Attention.For(name)?.Severity, thumbnails.ActiveCharacter(), s.QuickSwitchCardOrder,
            name => LiveState.EventKey(thumbnails.Attention.Top(name)?.EventKey));
    }

    /// <summary>IDEAS #14: opens the command palette over the monitor under the cursor, or closes it when it is open (never two).
    /// Its rows come from the sources the remote commands' runner reads (RunCommand): the running characters, the groups and the
    /// profiles, and the labels the player gave characters; the row chosen is run through RunCommand itself, so one action is
    /// the same as a command from another program. A failure is logged: a key press must not throw.</summary>
    private void ToggleCommandPalette()
    {
        try
        {
            if (_isShuttingDown || _settings == null || _thumbnailManager == null) return;
            if (_commandPalette != null)
            {
                var open = _commandPalette;
                _commandPalette = null;
                open.Dismiss();
                return;
            }
            var settings = _settings;
            var entries = CommandPalette.Entries(_thumbnailManager.GetActiveCharacterNames(), settings.Settings.ThumbnailAnnotations,
                FocusMode.Groups(settings.Settings).Select(g => g.Name), settings.GetProfileNames());
            var palette = new Views.CommandPaletteWindow(entries, command => RunCommand(command));
            var paletteHwnd = IntPtr.Zero;   // set below, once the window has a handle; Closed only clears its own
            palette.Closed += (_, _) =>
            {
                if (ReferenceEquals(_commandPalette, palette)) _commandPalette = null;
                if (_hotkeyService != null && _hotkeyService.CommandPaletteWindowHandle == paletteHwnd) _hotkeyService.CommandPaletteWindowHandle = IntPtr.Zero;
            };
            _commandPalette = palette;
            palette.Show();
            // Hotkeys stay quiet while the palette is in front (typed text must not switch a client); it may already have closed.
            paletteHwnd = new System.Windows.Interop.WindowInteropHelper(palette).Handle;
            if (_hotkeyService != null && ReferenceEquals(_commandPalette, palette)) _hotkeyService.CommandPaletteWindowHandle = paletteHwnd;
        }
        catch (Exception ex)
        {
            _commandPalette = null;
            LogError(ex);
        }
    }

    /// <summary>Brings a character's client to the front the way a click on its toast does
    /// (ThumbnailManager.ActivateEveWindow). False when no client of that character is running.</summary>
    private bool SwitchToCharacter(string name)
    {
        if (_thumbnailManager == null || _thumbnailManager.GetHwndForCharacter(name) == IntPtr.Zero) return false;
        _thumbnailManager.ActivateEveWindow(IntPtr.Zero, name);
        return true;
    }

    private static readonly string SettingsDiagLogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "EveMultiPreview_SettingsDiag.log");

    private static void SettingsDiag(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [Settings] {message}{Environment.NewLine}";
            System.IO.File.AppendAllText(SettingsDiagLogPath, line);
            Debug.WriteLine(line.TrimEnd());
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Snoozes an event for a character, from a toast's menu or a notification's button (IDEAS #30, #33):
    /// and its thumbnail, enlarged for an alert, goes back now, as for a mute (FY-n3).</summary>
    private void SnoozeEvent(string character, string alertType, TimeSpan? length)
    {
        _snoozes.Snooze(character, alertType, length);
        _thumbnailManager?.PutBackForASnooze(character);
        _notificationsWay?.Snoozed(character, alertType);
    }

    private void ExitApplication()
    {
        // FY-m9: the switch is followed first, while that is still allowed: notifications switched off within the
        // second before Exit (before the settings change was applied) leave no registry key behind.
        FollowNotifications();
        _isShuttingDown = true;
        // Save stat window positions before exit (AHK _OnAppExit pattern)
        _thumbnailManager?.SaveStatWindowPositions();
        _settings?.Save();

        Debug.WriteLine("[App:Startup] 🛑 Application exiting");
        // All service disposal happens in OnExit (triggered by Shutdown)
        Shutdown();
    }

    /// <summary>What the clean uninstall (IDEAS P16) runs as the last step of OnExit, before the mutex is let go.</summary>
    private Action? _afterExit;

    /// <summary>Closes the app for the clean uninstall. Not ExitApplication: that follows the notification switch (which
    /// would write the registry key back) and saves the settings and the stat windows' places (which would make the
    /// settings folder again). The shutting-down flag goes up first, so a settings window that closes with the app applies
    /// and saves nothing live. What was handed in runs at the end of OnExit, before the single-instance mutex is released.</summary>
    private void ExitForUninstall(Action afterExit)
    {
        _afterExit = afterExit;
        _isShuttingDown = true;
        Debug.WriteLine("[App:Startup] Application exiting for a clean uninstall");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Cover every shutdown path: tray Exit (sets this in ExitApplication),
        // UpdateService.ApplyUpdate calling Shutdown() directly, OS logoff
        // routing WM_ENDSESSION through Shutdown(), and second-instance
        // bail-out. Setting it here guarantees the applyLiveSettings gates
        // (App.xaml.cs:831/850/875) see the right state even when shutdown
        // bypasses ExitApplication.
        _isShuttingDown = true;

        // IDEAS #55: a clean exit. First, so a Dispose that throws below can't leave a marker that reports a crash that
        // didn't happen. Only the instance that began the marker deletes it; the second instance never did. An update
        // exits through here too (ApplyUpdate calls Shutdown). Its script waits for this process by its id, at most
        // 30 seconds, before it starts the new exe. It used to wait for a process NAMED "EVE MultiPreview", which a
        // freshly downloaded copy (EVE.MultiPreview.exe) is not: for that copy it went on after two seconds, whether
        // this process was gone or not (IDEAS P52).
        _runMarker?.End();

        // FY-m1: the player's data first. What the history holds is written now (at most two seconds), before the
        // voice and the notifications are stopped, which can take seconds when Windows does not answer.
        _alertHistory?.Flush(TimeSpan.FromSeconds(2));
        _thumbnailManager?.FlushPlaytime();   // IDEAS P11: the counted playtime, before anything below can stop the exit (Dispose flushes again; nothing is left)

        try
        {
            // Single disposal path — ExitApplication calls Shutdown() which triggers this
            _keepAwake.Follow(false);   // IDEAS #46: Windows may sleep again
            _clientExits.Forget();      // IDEAS #22: the clients' process handles are let go
            _commandServer?.Dispose();   // IDEAS #39: no more commands
            _commandServer = null;
            StopGamepad();   // IDEAS #43: the pad is no longer read
            _hotkeySheet?.Dismiss();   // IDEAS #16
            _nameBar?.Close();   // IDEAS #38: its preview's picture is let go with it
            _commandPalette?.Dismiss();   // IDEAS #14
            _spokenWay?.Stop();   // IDEAS #26: nothing more is said, and what is being said is cut off
            _notificationsWay?.Stop();   // IDEAS #33: what this session showed is taken out of the Notification Center
            _pushWay?.Stop();   // IDEAS #2: what waits to be sent is dropped and the one sender is let go
            _alertTimer?.Stop();
            _alertHub?.Dispose();
            _logMonitor?.Dispose();
            _alertHistory?.Dispose();   // writes what is waiting (at most two seconds)
            _hotkeyService?.Dispose();
            _processMonitor?.Dispose();
            _cropManager?.Dispose();
            _thumbnailManager?.Dispose();
            _discovery?.Dispose();
            _winEvents?.Dispose();
            SettingsUi.UiStyles.StopListening();   // Windows' contrast and colour events let go (UI styles S4, Task 6 review)
            _settings?.Dispose();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _trayIcons?.Dispose();
            _trayMenu?.Dispose();
        }
        finally
        {
            // Clean uninstall (IDEAS P16): the last step of the app, while this process still owns the single-instance mutex, so a
            // second MultiPreview started now waits for it instead of making the settings folder while it is being deleted. Every
            // service has stopped by now (the settings service has written what was waiting, the history is closed), so a folder
            // deleted here is not made again. What this leaves behind is told to the player by what was handed in.
            // A finally: a Dispose above that throws must not skip it (the player who chose "delete my settings" would get no
            // delete and no message, with the Windows entries already gone).
            if (_afterExit != null)
            {
                // A Debug build's trace listener holds Logs\debug_log.txt open (no Delete share), which would leave the folder part
                // there on every uninstall. Cleared, not only closed: a closed listener opens its file again on the next write.
                // In a Release build no Trace output exists, so this closes the listeners that are there and does nothing else.
                try
                {
                    foreach (System.Diagnostics.TraceListener listener in System.Diagnostics.Trace.Listeners) listener.Close();
                    System.Diagnostics.Trace.Listeners.Clear();
                }
                catch (Exception) { }
            }
            try { _afterExit?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[App:Exit] Clean uninstall step failed: {ex.GetType().Name}"); }
        }

        // Release + dispose the single-instance mutex so that if it
        // throws (e.g. abandoned, not owned by this thread, or already
        // disposed by the second-instance path), the rest of OnExit has
        // already run. Catch every exception so the process can still exit
        // cleanly even on hostile shutdown paths (OS logoff / process kill
        // recovery). Process exit will release the kernel mutex regardless.
        try { _singleInstanceMutex?.ReleaseMutex(); }
        catch (Exception ex) { Debug.WriteLine($"[App:Exit] ⚠ ReleaseMutex: {ex.GetType().Name}: {ex.Message}"); }
        try { _singleInstanceMutex?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[App:Exit] ⚠ Mutex Dispose: {ex.GetType().Name}: {ex.Message}"); }
        _singleInstanceMutex = null;

        base.OnExit(e);
    }


}
