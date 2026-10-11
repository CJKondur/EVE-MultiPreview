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
    private EventSnoozeFile? _snoozeFile;
    /// <summary>Do-not-disturb (IDEAS #32): alert sounds are muted while it is on. What the hotkey or the tray chose is
    /// forgotten when MultiPreview closes; the quiet hours are settings.</summary>
    private readonly DoNotDisturb _doNotDisturb = new();
    private bool DoNotDisturbOn => _doNotDisturb.IsOn(_settings?.Settings, DateTime.Now);
    /// <summary>Escalation (IDEAS #29), and the one timer that asks it once a second while it is switched on.</summary>
    private readonly AlertEscalation _escalation = new();
    private DispatcherTimer? _alertTimer;
    /// <summary>"Left behind" (IDEAS #5): told every system change, asked on the one-second timer while the event is on.</summary>
    private readonly StragglerWatch _stragglers = new();
    /// <summary>The look for it and the alert it raises, with what App has for both (set at startup).</summary>
    private StragglerAlerts? _stragglerAlerts;
    /// <summary>IDEAS #42: each hotkey Windows refused is told once a session in a tray note; a click on that note opens
    /// the hotkeys page. A click on any other balloon does not: ShowBalloon says which one is up.</summary>
    private readonly RefusalAnnouncer _refusalAnnouncer = new();
    private readonly RefusalNote _refusalNote = new();
    private AlertHistoryWindow? _historyWindow;
    private CustomAlertStore? _customAlerts;   // IDEAS #8: the player's own alert rules
    private BroadcastHudWindow? _broadcastHud;
    private ProcessMonitorService? _processMonitor;
    private CropManager? _cropManager;
    private Window? _settingsWindow;

    // IDEAS #57: where the settings live this run, decided first thing in OnStartup.
    private SettingsHomeResult? _home;
    // IDEAS #55: the marker that tells an unexpected end from a clean exit, and one queued tray tooltip refresh.
    private RunMarker? _runMarker;
    private DateTime? _previousRunStart;
    private int _trayTextQueued;

    // Tray balloon click-to-focus (matches AHK _trayAlertChar/_trayAlertHwnd)
    private string _trayAlertChar = "";
    private IntPtr _trayAlertHwnd = IntPtr.Zero;

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
            MessageBox.Show("EVE MultiPreview is already running.", "EVE MultiPreview",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

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
        // Clear perf log on each launch
        try { System.IO.File.WriteAllText(_perfLogPath, ""); } catch { }

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
        StartupSettings.Open(_settings, _home, AppPaths.ExeDir, CopyBesideExe(), AskAtStartup, LocalizationService.Str,
            DateTime.Now, LogError, Thread.Sleep,
            // Apply UI language before any window is shown (issue #86). "" auto-detects from the OS; falls back to
            // English for unsupported languages.
            () => Services.LocalizationService.SetLanguage(_settings.Settings.Language), MovedNoteText);

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
            // Recovery net (issue #81): never carry a stuck crops-hidden state across a switch.
            _cropManager?.ShowCrops();
            _cropManager?.Refresh();
            _hotkeyService?.RegisterFromSettings(
                _settings.Settings, _settings.CurrentProfile,
                _thumbnailManager!, OpenSettings);
            Debug.WriteLine($"[App:Profile] 🔄 Applied profile: {name}");
        };

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

            // 5. Initialize hotkey service
            _hotkeyService = new HotkeyService();
            _hotkeyService.Initialize();
            // Foreground-gated registration so EVE-only hotkeys pass through to other
            // apps when EVE isn't in front (issue #68).
            if (_winEvents != null)
                _hotkeyService.AttachWinEvents(_winEvents,
                    () => _thumbnailManager?.HasTrackedClients() == true);
            var profile = _settings.CurrentProfile;
            PerfLog($"[Hotkey] Profile='{_settings.Settings.LastUsedProfile}', CharHotkeys={profile.Hotkeys.Count}, Groups={profile.HotkeyGroups.Count}, Suspend='{_settings.Settings.SuspendHotkey}', Global={_settings.Settings.GlobalHotkeys}");
            foreach (var (name, binding) in profile.Hotkeys)
                PerfLog($"[Hotkey]   char='{name}' key='{binding.Key}'");
            _hotkeyService.RegisterFromSettings(
                _settings.Settings, profile,
                _thumbnailManager, OpenSettings);
            PerfLog($"[Deferred] Hotkeys registered: {deferSw.ElapsedMilliseconds}ms");

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
                _snoozes.Snooze(charName, alertType, length);
                _thumbnailManager.ShowTooltipFeedback(SnoozeText.Done(charName, alertType));
            };
            _thumbnailManager.Snoozes = _snoozes;
            _snoozeFile = new EventSnoozeFile(AppPaths.ConfigDir, ErrorLog.Shared);
            if (_settings.LoadedSuccessfully && _settings.Settings.KeepAlertSnoozes) _snoozeFile.Load(_snoozes);
            _snoozes.Changed += FollowSnoozeSwitch;
            PerfLog($"[Deferred] AlertHub created: {deferSw.ElapsedMilliseconds}ms");

            // Broadcast-key HUD — self-gates on ShowBroadcastKeyHud each tick.
            _broadcastHud = new BroadcastHudWindow(_settings.Settings);
            _broadcastHud.SaveRequested += () => _settings.SaveDelayed();

            // 7. Start log monitor and wire events
            _logMonitor = new LogMonitorService();
            _logMonitor.PveMode = _settings.Settings.PveMode;
            _logMonitor.SetCooldown(_settings.Settings.AlertCooldown);
            _logMonitor.SetSettings(_settings.Settings);

            if (_settings.Settings.EnabledAlertTypes != null)
                _logMonitor.SetEnabledAlertTypes(_settings.Settings.EnabledAlertTypes);
            if (_settings.Settings.SeverityCooldowns != null)
                _logMonitor.SetEventCooldowns(_settings.Settings.SeverityCooldowns);
            _logMonitor.SetSeverityCooldowns(LogMonitorService.SeverityWaits(_settings.Settings));   // IDEAS P28

            // ── Damage received (incoming) → stat tracker + alert ──
            _logMonitor.DamageReceived += (dmg) =>
            {
                _statTracker.RecordDamage(dmg.CharacterName, dmg.Amount, true, dmg.IsNpc, damageType: dmg.Type);
                _thumbnailManager?.SignalUnderFire(dmg.CharacterName);
                Debug.WriteLine($"[App:Alert] 🔴 Damage received: {dmg.Amount} from '{dmg.SourceName}' to '{dmg.CharacterName}' (NPC={dmg.IsNpc})");
            };
            _logMonitor.DamageDealt += (dmg) =>
            {
                _statTracker.RecordDamage(dmg.CharacterName, dmg.Amount, false, dmg.IsNpc);
            };
            _logMonitor.MiningYield += (mining) =>
            {
                _statTracker.RecordMining(mining.CharacterName, mining.Amount, mining.MineType);
            };
            _logMonitor.RepairReceived += (repair) =>
            {
                _statTracker.RecordRepair(repair.CharacterName, repair.Amount, repair.IsIncoming, repair.RepairType);
            };
            _logMonitor.BountyReceived += (bounty) =>
            {
                _statTracker.RecordBounty(bounty.CharacterName, bounty.Amount);
            };
            _logMonitor.SystemChanged += (charName, systemName) =>
            {
                Debug.WriteLine($"[App:Alert] 🌍 System change: '{charName}' → '{systemName}'");
                // IDEAS #5: every move, whether the event is on or not. First: the line below waits for the window's thread.
                _stragglers.Moved(charName, systemName, DateTime.UtcNow);
                _thumbnailManager.UpdateCharacterSystem(charName, systemName);
            };

            // Cycle-wrap sound (issue #24) — plays whenever a cycle hotkey rolls
            // from the last client back to the first (or vice-versa reversing).
            _thumbnailManager.CycleWrapped += () =>
            {
                var s = _settings?.Settings;
                if (s == null || !s.CycleWrapSoundEnabled) return;
                if (string.IsNullOrEmpty(s.CycleWrapSoundFile)
                    || !System.IO.File.Exists(s.CycleWrapSoundFile)) return;
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        _soundPlayer?.Open(new Uri(s.CycleWrapSoundFile));
                        _soundPlayer!.Volume = s.AlertSoundVolume / 100.0;
                        _soundPlayer.Play();
                    });
                    Debug.WriteLine($"[CycleWrap:Sound] 🔊 Playing '{System.IO.Path.GetFileName(s.CycleWrapSoundFile)}'");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CycleWrap:Sound] ❌ {ex.Message}");
                }
            };
            // What an alert is shown with. AlertDispatcher makes the decision this handler used to make inline (the same
            // checks in the same order, the same calls) and says what became of the alert (IDEAS #1).
            var alertTargets = new AlertTargets
            {
                ActiveCharacters = () => _thumbnailManager.GetActiveCharacterNames(),
                IsMuted = charName => _thumbnailManager.IsCharacterAlertMuted(charName),
                IsSnoozed = (charName, alertType) => _snoozes.IsSnoozed(charName, alertType),
                Flash = (charName, severity, alertType) => _thumbnailManager.SetAlertFlash(charName, severity, alertType),
                Badge = (charName, severity, alertType) => _thumbnailManager.IncrementAlertBadge(charName, severity, alertType),
                Unseen = (charName, alertType, severity) => _thumbnailManager.NoteUnseenAlert(charName, alertType, severity),
                WindowOf = charName => _thumbnailManager.GetHwndForCharacter(charName),
                ForegroundWindow = () => EveMultiPreview.Interop.User32.GetForegroundWindow(),
                Toast = (charName, alertType, severity) => _alertHub.ShowToast(charName, alertType, severity),
                Sound = PlayAlertSound,
            };
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
                _logMonitor.GetCharacterSystem, alertTargets, _alertHistory, LogError);
            _alertHub.PlaceLookup = _stragglerAlerts.PlaceOf;
            // IDEAS #17: "Go to the last alert" walks back through the history's memory list once nothing is unseen.
            _thumbnailManager.RecentAlerts = () => _alertHistory.Recent();
            // Windows signing out or shutting down: WPF only queues its shutdown then, and OnExit (which closes the
            // history) may not get to run before the process is ended. What is waiting is written now.
            SessionEnding += (_, _) => _alertHistory?.Flush(TimeSpan.FromSeconds(2));
            _logMonitor.AlertTriggered += (charName, alertType, severity) =>
            {
                // What a custom rule matched is there only while its alert is being raised: taken first (IDEAS #8).
                // The rule's name is taken with it: Dispatch waits for the window's thread (the toast, the sound),
                // and the player may rename or delete the rule meanwhile. The history keeps the name the alert was
                // raised under. For a built-in alert both are null.
                var matched = _logMonitor.RaisingCustomText;
                var rule = CustomAlertTypes.Find(alertType)?.Name;
                // So is the time: the entry says when the alert was raised, not when the window's thread got to it.
                var raised = DateTime.UtcNow;
                var outcome = AlertDispatcher.Dispatch(_settings.Settings, alertTargets, charName, alertType, severity);
                // With the history switched off no entry is made at all. (Record asks for itself as well.)
                if (!_alertHistory.Enabled) return;
                _alertHistory.Record(AlertHistory.Entry(raised, charName, alertType, severity,
                    _logMonitor.GetCharacterSystem(charName), outcome, rule, matched));
            };

            // IDEAS #8: the player's own alert rules, from custom_alerts.json in the settings folder. A file that can't
            // be used is set aside and reported, and MultiPreview runs without rules. The log monitor gets the rules
            // that are on before it starts reading, and again whenever the rules are saved.
            _customAlerts = new CustomAlertStore(AppPaths.ConfigDir, ErrorLog.Shared);
            _customAlerts.Load();
            _customAlerts.Changed += ApplyCustomRules;
            ApplyCustomRules();

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

            var chatLogDir = _settings.Settings.ChatLogDirectory;
            var gameLogDir = _settings.Settings.GameLogDirectory;
            _logMonitor.Start("", chatLogDir, gameLogDir,
                _settings.Settings.EnableChatLogMonitoring,
                _settings.Settings.EnableGameLogMonitoring);
            PerfLog($"[Deferred] LogMonitor started: {deferSw.ElapsedMilliseconds}ms");

            // 8. Set up system tray
            SetupTrayIcon();
            PerfLog($"[Deferred] Tray icon: {deferSw.ElapsedMilliseconds}ms");

            _alertHub.Show();
            PerfLog($"[Deferred] AlertHub.Show: {deferSw.ElapsedMilliseconds}ms");

            // Wire suspend icon toggle
            _hotkeyService.SuspendToggled += () =>
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Text = TrayMenu.TooltipText(_hotkeyService.IsSuspended, ErrorLog.Shared.Count, DoNotDisturbOn);
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    var icoName = _hotkeyService.IsSuspended ? "EveMultiPreview.Icon-Suspend.ico" : "EveMultiPreview.Icon.ico";
                    var icoStream = asm.GetManifestResourceStream(icoName);
                    if (icoStream != null)
                        _trayIcon.Icon = new System.Drawing.Icon(icoStream);
                }
                _thumbnailManager?.ShowSuspendTooltip(_hotkeyService.IsSuspended);
            };
            _hotkeyService.ProfileCycleForward += () => CycleProfile(forward: true);
            _hotkeyService.ProfileCycleBackward += () => CycleProfile(forward: false);
            _hotkeyService.DoNotDisturbToggled += ToggleDoNotDisturb;   // IDEAS #32
            // IDEAS #42: said after the registration is done, not from inside it. The pause key was registered before
            // the tray icon existed, and the other keys are asked for within the next half second. Told at once, the
            // pause key's note would be replaced by the note about the others: one second from now, one note names
            // every key that is refused by then.
            _hotkeyService.Refused.Changed += () => Dispatcher.BeginInvoke(new Action(TellRefusedHotkeys));
            var refusedAtStartup = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            refusedAtStartup.Tick += (_, _) =>
            {
                refusedAtStartup.Stop();
                TellRefusedHotkeys();
            };
            refusedAtStartup.Start();

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
            };
            hotkeyToggleTimer.Start();

            PerfLog($"[Deferred] ✅ All deferred startup complete: {deferSw.ElapsedMilliseconds}ms total");

            // IDEAS #55: once, after a session that ended without closing.
            if (_previousRunStart is DateTime previousStart) ShowUncleanExitNotice(previousStart);

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
                    PerfLog($"[Update] ⚠ Auto-check failed (non-fatal): {ex.Message}");
                }
            });
        };
        deferTimer.Start();

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
            Debug.WriteLine($"[App:Error] ⚠ Quietly handled: {e.Exception.GetType().Name}: {e.Exception.Message}");
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

    /// <summary>An error MultiPreview handled and kept running after: counted, and written to Logs\error_log.txt in the
    /// settings folder (IDEAS #55). Never throws.</summary>
    internal static void LogError(Exception ex)
    {
        ErrorLog.Shared.Record(ex, "App", DateTime.Now);
        Debug.WriteLine($"[App:Error] ❌ Logged error: {ex.GetType().Name}: {ex.Message}");
    }

    // ── JSON Migration (AHK: EVE-X-Preview → EVE MultiPreview) ───────

    /// <summary>EVE-X-Preview's file beside the exe becomes the settings file in the settings folder (IDEAS #57), and
    /// only while that folder has none: after the move, the file beside the exe is not the one MultiPreview reads.</summary>
    private static void CheckJsonMigration(string exeDir, string newFile)
    {
        var oldFile = Path.Combine(exeDir, "EVE-X-Preview.json");

        if (File.Exists(oldFile) && !File.Exists(newFile))
        {
            var result = MessageBox.Show(
                "Found settings from EVE-X-Preview. Migrate to EVE MultiPreview?",
                "Settings Migration", MessageBoxButton.YesNo, MessageBoxImage.Question);

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
                        _trayIcon.Text = TrayMenu.TooltipText(_hotkeyService?.IsSuspended ?? false, ErrorLog.Shared.Count, DoNotDisturbOn);
                }
                catch { /* a tooltip must never raise another error */ }
            }));
        }
        catch { Interlocked.Exchange(ref _trayTextQueued, 0); }
    }

    /// <summary>IDEAS #55: the last session ended without closing (a crash, Task Manager, a power cut). One notice, which
    /// doesn't take focus from the game, offering the logs folder.</summary>
    private static void ShowUncleanExitNotice(DateTime started)
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
                    LocalizationService.Str("L.Start.Crash.Close", "Close"),
                })
            { ShowActivated = false };
            notice.Closed += (_, _) => { if (notice.Choice == 0) DiagnosticsService.OpenLogsFolder(); };
            notice.Show();
        }
        catch (Exception ex) { LogError(ex); }
    }

    private const string UncleanBodyEnglish =
        "The last session, started {0}, ended without MultiPreview closing: it crashed, or was stopped by Task Manager "
        + "or a power cut. If something went wrong, error_log.txt in the logs folder may say what. Attach it to a bug report.";

    /// <summary>The one-second timer runs only while something needs it: escalation switched on (IDEAS #29), or the
    /// "Left behind" event (IDEAS #5).</summary>
    private void FollowAlertTimer()
    {
        if (_alertTimer == null || _settings == null) return;
        bool needed = !_isShuttingDown && (AlertEscalation.Needed(_settings.Settings) || StragglerWatch.IsOn(_settings.Settings));
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
            if (StragglerWatch.IsOn(s)) LookForStragglers(now);
            _escalation.Tick(s, now,
                unseen: () => _thumbnailManager.Attention.Unseen(),
                // What ends a chain for good: the character is muted, the event is snoozed for it, the character has
                // no client left to go to, or the event was switched off (a rule of the player's own: off or deleted).
                quieted: (who, type) => _thumbnailManager.IsCharacterAlertMuted(who) || _snoozes.IsSnoozed(who, type)
                    || _thumbnailManager.GetHwndForCharacter(who) == IntPtr.Zero || !AlertEscalation.EventOn(s, type),
                soundsHeld: () => _doNotDisturb.Silences(s, "critical", DateTime.Now),
                doNotDisturb: () => DoNotDisturbOn,
                play: (due, volume) => PlayAlertSound(due.Alert.Character, due.Alert.EventKey, due.Alert.Severity, due.Number, volume),
                failed: LogError);
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

    /// <summary>What a hotkey is bound to, as its card names it (the first, when several things share the key), or null.</summary>
    private string? ActionOfHotkey(string hotkey) => _settings == null ? null
        : SettingsUi.RefusedHotkeysEditor.UsesOf(hotkey,
            SettingsUi.HotkeyUses.All(_settings.Settings, SettingsUi.SettingsRegistry.Settings)).FirstOrDefault()?.Label;

    /// <summary>IDEAS #5, once a second while the event is on: has a jump wave left a member of a group behind?
    /// StragglerAlerts looks (the groups as the Groups page has them, the characters that have a client, where the
    /// log monitor has each) and raises the alert the way every alert is raised. A failure there costs that look or
    /// that one alert, never the timer.</summary>
    private void LookForStragglers(DateTime now) => _stragglerAlerts?.Look(now);

    /// <summary>IDEAS #32: the hotkey or the tray item flips do-not-disturb, and says near the cursor what it is now:
    /// once per press, as the other hotkeys' feedback is given.</summary>
    private void ToggleDoNotDisturb()
    {
        if (_isShuttingDown || _settings == null) return;
        var (key, english) = _doNotDisturb.Press(_settings.Settings, DateTime.Now);
        _thumbnailManager?.ShowTooltipFeedback(LocalizationService.Str(key, english));
        FollowDoNotDisturb();
    }

    /// <summary>The tray's tip says while do-not-disturb is on. It can change without anybody pressing anything (the
    /// quiet hours begin or end), so this is asked twice a second and refreshes the tip only when the answer changed.</summary>
    private void FollowDoNotDisturb()
    {
        if (_doNotDisturb.ChangedSinceShown(_settings?.Settings, DateTime.Now)) QueueTrayText();
    }

    /// <summary>IDEAS #30: the snooze file follows "Keep snoozes when MultiPreview restarts". Called after every change
    /// to the snoozes and after every settings change: on, the file holds the timed snoozes that are on now; off,
    /// there is no file. A session whose settings could not be read writes nothing here either.</summary>
    private void FollowSnoozeSwitch()
    {
        if (_isShuttingDown || _snoozeFile == null || _settings == null || !_settings.LoadedSuccessfully) return;
        _snoozeFile.Follow(_settings.Settings.KeepAlertSnoozes, _snoozes);
    }

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

    private void PlayAlertSound(string character, string alertType, string severity) =>
        PlayAlertSound(character, alertType, severity, repeat: 0, volume: null);

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
            EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                $"[Sound] GATED off: EnableAlertSounds={s?.EnableAlertSounds} for {alertType} on '{character}'");
            return SoundResult.Never;
        }
        // Do-not-disturb (IDEAS #32) mutes the sound and nothing else: the border, the badge and the toast were shown
        // before this is asked. A critical alert still plays unless the player turned that off.
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
            if ((DateTime.Now - lastPlay).TotalSeconds < soundCooldown)
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

        // The event's own sound file, else the default one; a custom rule's own before both (IDEAS #8), and the
        // default one when the rule's own file is no longer there.
        string? soundFile = AlertSoundChoice.FileToPlay(s, alertType, System.IO.File.Exists);

        if (string.IsNullOrEmpty(soundFile) || !System.IO.File.Exists(soundFile))
        {
            Debug.WriteLine($"[AlertSound:Play] ⚠ No sound file for {alertType} (file='{soundFile ?? "null"}')");
            EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                $"[Sound] GATED no-file: {alertType} on '{character}' resolved='{soundFile ?? "null"}' " +
                $"(exists={(!string.IsNullOrEmpty(soundFile) && System.IO.File.Exists(soundFile))})");
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
                        $"[Sound] MediaFailed for {alertType} on '{character}' file='{soundFile}': {args.ErrorException?.Message}");
                    player.Close();
                    lock (_soundPlayerLock) { _activeSoundPlayers.Remove(player); }
                };
                lock (_soundPlayerLock) { _activeSoundPlayers.Add(player); }
                player.Open(new Uri(soundFile));
                player.Play();
            });

            var playedAt = DateTime.Now;
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
            EveMultiPreview.Services.DiagnosticsService.LogAlerts(
                $"[Sound] ERROR playing {alertType} on '{character}': {ex.GetType().Name}: {ex.Message}");
            return SoundResult.Never;
        }
    }

    // ── Tray Menu (matches AHK TrayMenu.ahk) ────────────────────────

    private void SetupTrayIcon()
    {
        // Load icon from embedded resource (works with single-file publish)
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var iconStream = asm.GetManifestResourceStream("EveMultiPreview.Icon.ico");
        var trayIco = iconStream != null ? new System.Drawing.Icon(iconStream) : SystemIcons.Application;

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
            SwitchProfile = name => _settings?.SwitchProfile(name),   // ProfileSwitched applies it (#101)
            IsSuspended = () => _hotkeyService?.IsSuspended ?? false,
            ToggleSuspend = () => _hotkeyService?.ToggleSuspend(),
            DoNotDisturbOn = () => DoNotDisturbOn,
            ToggleDoNotDisturb = ToggleDoNotDisturb,
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
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
            CloseAll = () => _thumbnailManager?.CloseAllEveWindows(),
            Exit = ExitApplication,
        });

        _trayIcon = new NotifyIcon
        {
            Text = TrayMenu.TooltipText(_hotkeyService?.IsSuspended ?? false, ErrorLog.Shared.Count, DoNotDisturbOn),
            Icon = trayIco,
            Visible = true,
            ContextMenuStrip = _trayMenu.Strip
        };

        // M3: Balloon click → focus attacked character (matches AHK NIN_BALLOONUSERCLICK)
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (_refusalNote.Clicked())
            {
                // IDEAS #42: deferred, as the tray menu's Settings item is, so the note has gone before the window shows.
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    OpenSettings(startMinimized: false, pageId: SettingsUi.ClientsPages.HotkeysId)));
                return;
            }
            if (!string.IsNullOrEmpty(_trayAlertChar))
            {
                _thumbnailManager?.ActivateEveWindow(IntPtr.Zero, _trayAlertChar);
                Debug.WriteLine($"[Tray:BalloonClick] 🔧 Focused '{_trayAlertChar}' via balloon click");
            }
        };

        _trayIcon.BalloonTipClosed += (_, _) => _refusalNote.Closed();

        var menu = _trayMenu.Strip;

        // Pause the thumbnail focus sweep while the menu is up. Opening the menu
        // makes this app foreground, and the sweep would otherwise re-show and
        // re-raise thumbnails over it — closing or burying the menu.
        menu.Opening += (_, _) =>
        {
            _thumbnailManager?.SetTrayMenuOpen(true);
            SettingsDiag("Tray menu opening");
        };
        menu.Opened += (_, _) => SettingsDiag("Tray menu opened");
        menu.Closing += (_, e) => SettingsDiag($"Tray menu closing, reason={e.CloseReason}");
        menu.Closed += (_, _) => _thumbnailManager?.SetTrayMenuOpen(false);

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
        _thumbnailManager?.ShowTooltipFeedback($"Profile: {newProfile}");

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
                if (_settings.Settings.EnabledAlertTypes != null)
                    _logMonitor.SetEnabledAlertTypes(_settings.Settings.EnabledAlertTypes);
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
                    DoNotDisturbOn = () => DoNotDisturbOn,
                    CustomAlerts = _customAlerts,
                    // Which custom rules the log monitor has switched off for what they cost: the page says it on their cards.
                    CustomRulesSwitchedOff = () => _logMonitor?.CustomRulesSwitchedOff ?? CustomAlertEngine.NoneSwitchedOff,
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
            _historyWindow = new AlertHistoryWindow(_alertHistory, _settings.Settings, new AlertHistoryActions
            {
                SwitchTo = SwitchToCharacter,
                PickSaveFile = suggested =>
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggested, DefaultExt = ".csv", Filter = "CSV (*.csv)|*.csv" };
                    return dialog.ShowDialog(_historyWindow) == true ? dialog.FileName : null;
                },
                Confirm = (title, body) =>
                    MessageBox.Show(_historyWindow!, body, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
                // Asked at every refresh: while the setting is off, the window says so above its list.
                HistoryOn = () => _settings.Settings.AlertHistoryEnabled,
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

    private void ExitApplication()
    {
        _isShuttingDown = true;
        // Save stat window positions before exit (AHK _OnAppExit pattern)
        _thumbnailManager?.SaveStatWindowPositions();
        _settings?.Save();

        Debug.WriteLine("[App:Startup] 🛑 Application exiting");
        // All service disposal happens in OnExit (triggered by Shutdown)
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

        // Single disposal path — ExitApplication calls Shutdown() which triggers this
        _alertTimer?.Stop();
        _alertHub?.Dispose();
        _broadcastHud?.Dispose();
        _logMonitor?.Dispose();
        _alertHistory?.Dispose();   // writes what is waiting (at most two seconds)
        _hotkeyService?.Dispose();
        _processMonitor?.Dispose();
        _cropManager?.Dispose();
        _thumbnailManager?.Dispose();
        _discovery?.Dispose();
        _winEvents?.Dispose();
        _settings?.Dispose();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayMenu?.Dispose();

        // Release + dispose the single-instance mutex LAST so that if it
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
