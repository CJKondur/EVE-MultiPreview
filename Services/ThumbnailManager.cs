using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Views;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace EveMultiPreview.Services;

/// <summary>
/// Manages the lifecycle of DWM thumbnail windows. Creates a ThumbnailWindow
/// for each discovered EVE client and orchestrates:
///   - Active/inactive border highlighting (with custom + group colors)
///   - Hide on lost focus
///   - Hide active thumbnail
///   - Minimize inactive clients
///   - Always maximize on activation
///   - Thumbnail snap on drag release
///   - Lock positions toggle
///   - Position save/restore
///   - Session timer overlay
///   - System name overlay
///   - Not-logged-in indicator
///   - Client position save/restore
/// Matches AHK Main_Class + ThumbWindow behavior.
/// </summary>
public sealed class ThumbnailManager : IDisposable
{
    private readonly WindowDiscoveryService _discovery;
    private readonly SettingsService _settings;
    private readonly ConcurrentDictionary<IntPtr, ThumbnailWindow> _thumbnails = new();

    // Secondary PiP thumbnails — keyed by character name (independent of primary)
    private readonly ConcurrentDictionary<string, ThumbnailWindow> _secondaryThumbnails = new();

    // Stat overlay windows — keyed by "charName:statType" (e.g. "Pilot:DPS")
    private readonly ConcurrentDictionary<string, StatOverlayWindow> _statWindows = new();
    private StatTrackerService? _statTracker;

    // Session-only cycle exclusion (issue #16). Shift-click on a thumbnail toggles
    // membership; excluded characters are skipped by CycleGroup/CycleAll until the
    // user toggles them back or the app restarts. Not persisted.
    private readonly ConcurrentDictionary<string, byte> _excludedFromCycle = new(StringComparer.OrdinalIgnoreCase);

    // Active window tracking
    private IntPtr _lastActiveEveHwnd = IntPtr.Zero;
    private IntPtr _lastSweptHwnd = IntPtr.Zero;   // foreground the focus sweep last reconciled
    // ConcurrentDictionary used as a thread-safe set because OnWindowLost
    // (background poll thread) mutates this alongside UpdateActiveBorders
    // (UI thread). The byte value is unused; only keys matter. Mechanical
    // swap from HashSet — no call site iterates, so semantics are identical.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte> _iconicHwnds = new();
    private FrozenFrameService? _frozenFrames;
    private WinEventHookService? _winEvents;
    private DispatcherTimer? _focusTimer;
    /// <summary>Debounces RaiseThumbnailsAboveOverlays so a client switch never pays for
    /// it inline — see the comment where it is created (#100).</summary>
    private DispatcherTimer? _raiseOverlaysDebounce;
    /// <summary>IDEAS #10: applies the layout saved for the monitors that come back (<see cref="NewMonitorFollower"/>); started
    /// with the focus tracking, stopped and unhooked in Dispose.</summary>
    private MonitorLayoutFollower? _monitorFollower;
    private DispatcherTimer? _monitorSettleTimer;
    private EventHandler? _displayChanged;
    private DispatcherTimer? _sessionTimer;
    private DispatcherTimer? _statTimer;
    private DispatcherTimer? _fpsTimer;

    // Batch window creation — accumulate discovered windows and create in groups
    private readonly List<EveWindow> _pendingWindows = new();
    private DispatcherTimer? _batchTimer;

    // Visibility state (matches AHK toggle hotkeys)
    private bool _thumbnailsHidden = false;
    private bool _primaryHidden = false;
    private bool _clickThroughActive = false;
    private bool _settingsClickSuppressed = false;  // Force thumbnails click-through while Settings UI is open
    private bool _suppressTopmost = false;  // Suppress topmost while settings window is active
    private bool _settingsOpen = false;     // Settings window exists (even minimized) — keeps thumbnails visible
    private bool _trayMenuOpen = false;     // Tray context menu is showing — focus sweep paused so it can't close/bury it
    private bool _primaryHiddenByFocus = false; // Primary thumbnails fully hidden by "Hide When Alt-Tabbed" (focus loss)
    private bool _lastEveFocused = false;   // Track focus transitions for one-time BringToFront
    private IntPtr _lastZOrderHwnd = IntPtr.Zero; // Last foreground HWND we re-raised thumbnails for
    private bool? _lastAppliedTopmost;      // Last topmost state pushed to all windows (forces full re-assert on change)
    private bool _lastGpuFreezeState = false; // StaticThumbnails / SuspendThumbnailsWhenBackground transitions
    private bool _lastHideActiveThumbnailState = false; // HideActiveThumbnail on→off transition tracking

    // Alert flash state — per-severity rates + expiry (matches AHK)
    private readonly ConcurrentDictionary<string, AlertFlashInfo> _alertFlashChars = new();
    private DispatcherTimer? _flashTimer;

    // Per-character unread-alert badge state. Counts every alert fired for a
    // character; clears when the user pulls that character's window to front.
    // The strongest severity seen since last clear drives the badge color.
    private readonly ConcurrentDictionary<string, AlertBadgeInfo> _alertBadges = new(StringComparer.OrdinalIgnoreCase);
    // Severity ranking — higher = more important. Used so a Critical bumps
    // the badge color even if subsequent Info alerts pile on after it.
    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = 1, ["warning"] = 2, ["critical"] = 3
    };

    // Per-severity flash rates (ms) from AHK
    private static readonly Dictionary<string, int> FlashRates = new()
    {
        ["critical"] = 200,
        ["warning"] = 500,
        ["info"] = 1000
    };

    // Per-severity expiry (seconds) from AHK
    private static readonly Dictionary<string, int> FlashExpiry = new()
    {
        ["critical"] = 8,
        ["warning"] = 6,
        ["info"] = 4
    };

    // Destroy debounce
    private readonly ConcurrentDictionary<IntPtr, DispatcherTimer> _destroyTimers = new();

    // Key = CharacterName (case-insensitive), Value = SystemName
    private readonly ConcurrentDictionary<string, string> _charSystems = new(StringComparer.OrdinalIgnoreCase);

    // Key = CharacterName, Value = mute-until time in UTC (DateTime.MaxValue = until cleared) and the mute's length.
    // Per-character alert mute/snooze — runtime only, clears on app restart.
    private readonly ConcurrentDictionary<string, (DateTime Until, TimeSpan Length)> _alertMutedChars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Now, in UTC, for a timed mute (FY-I1): the local wall clock steps back an hour when daylight saving
    /// time ends, and a mute then lasted an hour longer. A test hands in a clock of its own; null changes nothing.</summary>
    public Func<DateTime> MuteClock { get => _muteClock; set => _muteClock = value ?? _muteClock; }
    private Func<DateTime> _muteClock = () => DateTime.UtcNow;

    /// <summary>Now, in UTC, for the alert flash and the under-fire border (IDEAS P97): they counted on the local wall
    /// clock, which steps back an hour when daylight saving time ends, and a flash raised just before the step stood as
    /// a steady border for an hour. A moment that lies ahead of now ends them. A test hands in a clock of its own; null changes nothing.</summary>
    public Func<DateTime> FlashClock { get => _flashClock; set => _flashClock = value ?? _flashClock; }
    private Func<DateTime> _flashClock = () => DateTime.UtcNow;

    /// <summary>Which clients need the player: per character, the alerts shown and not looked at yet (IDEAS batch 5).
    /// Fed where the badge is fed and cleared, and where a client goes away. It doesn't depend on a display setting,
    /// as the badge does.</summary>
    public AlertAttention Attention { get; }

    /// <summary>Enlarging a thumbnail on an alert (IDEAS #13): which thumbnails are enlarged, and until when. App adds
    /// it to the ways an alert reaches the player; <see cref="EnlargeTick"/> tells the windows.</summary>
    public AlertEnlarge Enlarge { get; } = new();

    /// <summary>Every 50 ms, on the timer the border flash runs on: tells each thumbnail the scale AlertEnlarge
    /// answers (<see cref="TellEnlarged"/>). Only a character's main thumbnail is ever told: second thumbnails and
    /// crop windows are not enlarged. While nothing is enlarged this is one read. While any menu of the app is open
    /// (a thumbnail's, the tray's, a toast's: ThumbnailWindow.MenuOpen, FX-m1, FX-m2) no thumbnail is enlarged:
    /// nothing grows, what was enlarged is told to go back (and to grow again when the menu has closed), and what is
    /// over still goes back. Nothing is raised meanwhile (a thumbnail raised then would bury the menu).</summary>
    private void EnlargeTick()
    {
        if (!Enlarge.Any) return;
        try
        {
            TellEnlarged(Enlarge, DateTime.UtcNow, _settings.Settings, GetHwndForCharacter, Interop.User32.GetForegroundWindow,
                GetThumbnailForCharacter, ThumbnailWindow.MenuOpen, ReportEnlarge);
        }
        catch (Exception ex)
        {
            ReportEnlarge(AlertWays.Failure("Enlarge on alert: the tick", ex));
        }
    }

    /// <summary>Where a failure of the enlargement goes: the error log, as a type and a stack (AlertWays.Failure),
    /// never as the message of what was thrown.</summary>
    private static void ReportEnlarge(Exception ex) => ErrorLog.Shared.Record(ex, "Thumbnails (enlarge on alert)", DateTime.Now);

    /// <summary>Tells a reporter what failed and the type of what was thrown. Reporting never costs the thumbnails
    /// that are still to be told.</summary>
    private static void ReportEnlargeFailure(Action<Exception> report, string what, Exception ex)
    {
        try { report(AlertWays.Failure("Enlarge on alert: " + what, ex)); }
        catch (Exception) { /* a reporter that fails must not stop the others' going back */ }
    }

    /// <summary>What <see cref="EnlargeTick"/> does, with its clock and its lookups handed in (tests): asks
    /// AlertEnlarge what is due at this moment and tells each thumbnail its scale. Each thumbnail is told in a try
    /// of its own: AlertEnlarge has forgotten an enlargement by the time its thumbnail is told to go back, so one
    /// thumbnail that throws would otherwise cost every thumbnail behind it its going back, for ever. What was
    /// thrown is reported by its type, and the others are still told. While a thumbnail's right-click menu is open
    /// nothing is told to grow (T6-I1), and what was told to grow is told to go back (RD-I1): each is told to grow
    /// when the menu has closed, if its time is not over by then. What is over goes back meanwhile: a
    /// thumbnail's menu can stay open for a long time, because the window that shows it never becomes the window in
    /// front and a click into the game does not close it, and a thumbnail must not stay enlarged for that long.</summary>
    /// <param name="utcNow">Now, in UTC, as an alert's time is (AlertNotice.TimeUtc).</param>
    /// <param name="clientOf">A character's client window, or zero. Asked under AlertEnlarge's lock: it must only
    /// look the window up.</param>
    /// <param name="foreground">The window in front. Asked once.</param>
    /// <param name="thumbnailOf">A character's main thumbnail, or null. It is asked under AlertEnlarge's lock too,
    /// so it must not throw, wait or ask AlertEnlarge anything.</param>
    /// <param name="menuOpen">A thumbnail's right-click menu is open (ThumbnailWindow.MenuOpen).</param>
    public static void TellEnlarged(AlertEnlarge enlarge, DateTime utcNow, AppSettings settings, Func<string, IntPtr> clientOf,
        Func<IntPtr> foreground, Func<string, ThumbnailWindow?> thumbnailOf, bool menuOpen, Action<Exception> report)
    {
        var front = foreground();
        foreach (var change in enlarge.Tick(utcNow, settings, name => InFront(clientOf(name), front), name => thumbnailOf(name) != null,
                     holdGrowth: menuOpen))
        {
            try { thumbnailOf(change.Character)?.SetAlertScale(change.Scale); }
            catch (Exception ex) { ReportEnlargeFailure(report, "telling a thumbnail", ex); }
        }
    }

    /// <summary>The watchdog of the enlargement (T6-C1), run for every thumbnail at each focus sweep, beside the
    /// hover zoom's: a thumbnail that is enlarged for an alert, or was asked to be, while AlertEnlarge holds
    /// nothing for its character is put back. Nothing else would ever tell such a thumbnail to go back: its
    /// character left the window (a logoff keeps the window and changes its name, and AlertEnlarge then forgets
    /// the name without a word), or its "go back" failed. One read per thumbnail while none is enlarged. A
    /// thumbnail that throws is reported by the type of what it threw, and is asked again at the next sweep.</summary>
    public static void PutBackUnheld(AlertEnlarge enlarge, ThumbnailWindow thumb, Action<Exception> report)
    {
        if (!thumb.IsAlertEnlarged && !thumb.IsAlertEnlargementAsked) return;
        if (enlarge.Holds(thumb.CharacterName)) return;
        try { thumb.SetAlertScale(1.0); }
        catch (Exception ex) { ReportEnlargeFailure(report, "putting a thumbnail back", ex); }
    }

    /// <summary>The events snoozed for one character alone (IDEAS #30; App sets it). A thumbnail's menu lists its
    /// character's and ends them.</summary>
    public EventSnoozes? Snoozes { get; set; }

    private IReadOnlyList<EventSnooze> SnoozesOf(string characterName) =>
        Snoozes?.Of(characterName) ?? (IReadOnlyList<EventSnooze>)Array.Empty<EventSnooze>();

    /// <summary>An event was snoozed for a character, from a toast's menu, a notification's button (FY-n3) or the settings' snooze list: its
    /// thumbnail, enlarged for an alert, goes back now, and the enlargement is forgotten, as a mute does it.</summary>
    public void PutBackForASnooze(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        GetThumbnailForCharacter(characterName)?.SetAlertScale(1.0);
        Enlarge.Forget(characterName);
    }

    private void OnSnoozeEndRequested(ThumbnailWindow thumb, string? eventKey)
    {
        if (Snoozes == null || string.IsNullOrEmpty(thumb.CharacterName)) return;
        if (eventKey == null) Snoozes.EndAll(thumb.CharacterName);
        else Snoozes.End(thumb.CharacterName, eventKey);
    }

    /// <summary>An alert was shown for a character (AlertTargets.Unseen; the log monitor's reading thread). An alert on
    /// the client in front is seen as it arrives: the badge's rule (IncrementAlertBadge).</summary>
    public void NoteUnseenAlert(string characterName, string alertType, string severity) =>
        NoteUnseen(Attention, characterName, alertType, severity, GetHwndForCharacter, Interop.User32.GetForegroundWindow);

    /// <summary>Whether a client is the window in front. No client (a zero handle) is never in front, whatever
    /// Windows names as the window in front: with no window in front at all it names zero too.</summary>
    public static bool InFront(IntPtr client, IntPtr foreground) => client != IntPtr.Zero && client == foreground;

    /// <summary>What <see cref="NoteUnseenAlert"/> does, with its two questions handed in (tests): which window is
    /// a character's client, and which window is in front. A character without a client gets no entry: there is
    /// nothing to go to, and nothing would ever take the entry off again. The client is asked for a second time
    /// after the entry is made, because this runs on the log monitor's thread while the window's thread may be
    /// closing that client: if its "gone" came first, the entry is taken off here.</summary>
    public static void NoteUnseen(AlertAttention attention, string characterName, string alertType, string severity,
        Func<string, IntPtr> clientOf, Func<IntPtr> foreground)
    {
        var client = clientOf(characterName);
        if (client == IntPtr.Zero) return;
        if (!attention.Raised(characterName, alertType, severity, clientInFront: InFront(client, foreground()))) return;
        if (clientOf(characterName) == IntPtr.Zero) attention.Gone(characterName);
    }

    /// <summary>What GoToLastAlert does before it asks the walk, with the two windows handed in (tests): the alerts of
    /// the client MultiPreview counts as active are looked at, but only when that client really is the window in
    /// front. While the tray menu is open, during a drag, and for a moment after a switch Windows refused, it is
    /// not: an alert raised for it then is unseen and badged, and stays so, so that the walk names that client and
    /// the key goes to it.</summary>
    /// <param name="character">The active client's character, or null when it has none (or there is no client).</param>
    public static void SeeClientInFront(AlertAttention attention, string? character, IntPtr client, IntPtr foreground)
    {
        if (character != null && InFront(client, foreground)) attention.Seen(character);
    }

    /// <summary>The character the walk of "Go to the last alert" is told is in front: the active client's character
    /// when that client really is the window in front, else nobody (IDEAS P78 (4)). While the tray menu is open,
    /// during a drag, after a switch Windows refused or with a browser in front, the client MultiPreview counts as
    /// active is not the one the player is on: the key must go there, not say the last alert is on this client.</summary>
    public static string? CharacterInFront(string? character, IntPtr client, IntPtr foreground) =>
        character != null && InFront(client, foreground) ? character : null;

    /// <summary>Whether a client's change of title means that the character it had has left it: it logged out, or
    /// another one logged in. The same name in another spelling is the same character, and a client that had no
    /// character had none to lose.</summary>
    public static bool CharacterLeft(string? before, string? now) =>
        !string.IsNullOrEmpty(before) && !string.Equals(before, now, StringComparison.OrdinalIgnoreCase);

    // Per-process audio (auto-solo + per-client mute/volume), matched by PID.
    private readonly AudioSessionService _audio = new();

    // Layout undo/redo — bounded in-session stacks of saved-position snapshots.
    private readonly List<Dictionary<string, ThumbnailRect>> _layoutUndo = new();
    private readonly List<Dictionary<string, ThumbnailRect>> _layoutRedo = new();
    private const int MaxLayoutHistory = 25;
    private DateTime _lastResizeAllSnapshot = DateTime.MinValue;

    // Under-fire tracking: character name → last damage timestamp
    private readonly ConcurrentDictionary<string, DateTime> _underFireChars = new();

    // Drag all state: positions of all thumbnails at start of Ctrl+drag
    private readonly Dictionary<ThumbnailWindow, (double X, double Y)> _dragAllStartPositions = new();
    private double _dragAllBaseX, _dragAllBaseY;

    /// <param name="attentionClock">The clock the unseen alerts age by (<see cref="AlertAttention.MaxAge"/>); a test hands
    /// one in. Null: the PC's clock in UTC.</param>
    public ThumbnailManager(WindowDiscoveryService discovery, SettingsService settings, Func<DateTime>? attentionClock = null)
    {
        _discovery = discovery;
        _settings = settings;
        Attention = new AlertAttention(attentionClock);
        // An alert that expired unseen takes its share of its character's badge with it (IDEAS P78); looking is what clears it otherwise.
        Attention.Expired += TrimAlertBadge;
        // And a character whose client closed, or who logged out of it (AlertAttention.Gone), takes its whole badge along:
        // the next one to log in on that window starts from nothing, not from this one's count. (Said by the sweep too, where
        // the client really is in front, which clears the badge itself a line before: a second clear finds nothing.)
        Attention.Looked += ClearAlertBadge;
        Playtime = new PlaytimeLedger(() => PlaytimeClock());

        _discovery.WindowFound += OnWindowFound;
        _discovery.WindowLost += OnWindowLost;
        _discovery.WindowTitleChanged += OnWindowTitleChanged;
    }

    // ── Process Monitor Integration ─────────────────────────────────
    private ProcessMonitorService? _processMonitor;

    public void SetProcessMonitor(ProcessMonitorService monitor)
    {
        _processMonitor = monitor;
        _processMonitor.StatsUpdated += OnProcessStatsUpdated;
    }

    private void OnProcessStatsUpdated()
    {
        if (_processMonitor == null || !_settings.Settings.ShowProcessStats) return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                int pid = thumb.GetProcessId();
                var stats = _processMonitor.GetStats(pid);
                thumb.UpdateProcessStats(stats != null
                    ? ProcessMonitorService.FormatStats(stats)
                    : null);
            }
        });
    }

    /// <summary>The logged-in characters now (a login screen is nobody): name, when the thumbnail's session began and the process
    /// that owns the client's window (IDEAS #18, the fleet board). A copy. UI thread: thumbnails are made and closed there.</summary>
    public IReadOnlyList<CharacterSnapshot> SnapshotCharacters()
    {
        var list = new List<CharacterSnapshot>();
        foreach (var (_, thumb) in _thumbnails)
            if (ThumbnailMenu.HasCharacter(thumb.CharacterName))
                list.Add(new CharacterSnapshot(thumb.CharacterName, thumb.SessionStartUtc, thumb.GetProcessId()));
        return list;
    }

    /// <summary>
    /// Start the focus tracker and session timer. Must be called on UI thread.
    /// </summary>
    public void StartFocusTracking(WinEventHookService? winEvents = null)
    {
        _winEvents = winEvents;

        // Frozen-frame snapshot service — captures a PrintWindow bitmap per EVE
        // HWND on a slow safety cadence and eagerly on MINIMIZESTART (via the
        // win-event hook) so a last-rendered frame is available the instant the
        // source window becomes iconic.
        _frozenFrames = new FrozenFrameService();
        _frozenFrames.Start(() => _thumbnails.Keys.ToArray(), _winEvents);

        // Focus tracking — slow safety-net sweep at 250ms. Real-time response
        // to focus changes / minimize transitions arrives via the win-event
        // hooks below, which call UpdateActiveBorders on demand. The sweep
        // still runs to handle transitions the OS doesn't event for (e.g.
        // virtual-desktop changes) and to drive overlay position sync.
        // Coalesce overlay re-raises off the client-switch path (#100). Raising every
        // thumbnail/PiP/stat window is O(number of clients) and each one is a layered
        // topmost window, so the z-order change makes DWM recompose — doing that inline
        // on every switch is what made switching slower the more clients were open.
        // 30ms is under a frame, so the raise still looks immediate, and a burst of
        // cycling collapses into a single raise once it settles.
        _raiseOverlaysDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _raiseOverlaysDebounce.Tick += (_, _) =>
        {
            _raiseOverlaysDebounce?.Stop();
            RaiseThumbnailsAboveOverlays();
        };

        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _focusTimer.Tick += (_, _) => UpdateActiveBorders();
        _focusTimer.Start();

        // Event-driven wake-ups for sub-frame border response. The hook fires
        // on the UI thread so we can call UpdateActiveBorders directly — but
        // marshal via BeginInvoke to keep the hook callback short and let the
        // OS dispatcher get back to delivering events.
        if (_winEvents != null)
        {
            _winEvents.ForegroundChanged += OnForegroundOrMinimizeEvent;
            _winEvents.WindowMinimizeStart += OnForegroundOrMinimizeEvent;
            _winEvents.WindowMinimizeEnd += OnForegroundOrMinimizeEvent;
        }

        // Session timer — 1 second intervals
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sessionTimer.Tick += (_, _) => { UpdateSessionTimers(); UpdateJumpCounts(); CheckUnderFireExpiry(); };
        _sessionTimer.Start();

        // Flash timer — 100ms for urgent, responsive alert flashing (10Hz)
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _flashTimer.Tick += (_, _) => { FlashAlertTick(); EnlargeTick(); };
        _flashTimer.Start();

        // Stat overlay update timer — 1s interval for DPS/Logi/Mining/Ratting
        _statTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statTimer.Tick += (_, _) => UpdateStatWindows();
        _statTimer.Start();

        // FPS polling timer — 500ms interval for real-time overlay
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fpsTimer.Tick += (_, _) => UpdateFpsOverlays();
        _fpsTimer.Start();

        StartMonitorFollowing();

        Debug.WriteLine("[AlertFlash:Start] 🔧 Focus (100ms), Session (1s), Flash (200ms), Stat (1s), FPS (500ms) timers started");
    }

    // ── Monitor changes (IDEAS #10) ─────────────────────────────────

    /// <summary>The bounds of every monitor now, as MonitorFingerprint takes them.</summary>
    public static IReadOnlyList<System.Drawing.Rectangle> CurrentMonitorBounds() =>
        System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).ToList();

    /// <summary>The follower that applies a layout when the monitors change: it reads the switch AutoLayoutOnMonitorChange
    /// each time, and looks the layout up in the layouts file as it is then. Tests hand in the monitors, the clock, the
    /// timer and the apply; nothing is hooked here (<see cref="StartMonitorFollowing"/> does that).</summary>
    public MonitorLayoutFollower NewMonitorFollower(Func<IReadOnlyList<System.Drawing.Rectangle>>? screens = null,
        Func<DateTime>? clock = null, Action<TimeSpan>? schedule = null, Action<LayoutPreset>? apply = null) =>
        new(() => _settings.Settings.AutoLayoutOnMonitorChange,
            screens ?? CurrentMonitorBounds,
            clock ?? (() => DateTime.UtcNow),
            schedule ?? ScheduleMonitorSettle,
            fingerprint => new LayoutPresetService(_settings.ConfigDirectory).FindForFingerprint(fingerprint),
            apply ?? (preset =>
            {
                ApplyLayoutPreset(preset, forMonitors: true);
                MonitorLayoutApplied?.Invoke(preset.Name);
            }));

    /// <summary>Raised (on the follower's thread, the window's) after the monitors' layout was applied by itself, with the
    /// layout's name. App refreshes the crop windows and records the name as the profile's applied layout, as the layouts
    /// editor does for an apply the player asked for.</summary>
    public event Action<string>? MonitorLayoutApplied;

    private void StartMonitorFollowing()
    {
        _monitorFollower = NewMonitorFollower();
        _monitorFollower.Start();
        // DisplaySettingsChanged is raised on a thread of its own; the follower works on the UI thread.
        _displayChanged = (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => _monitorFollower?.Changed());
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += _displayChanged;
    }

    /// <summary>The follower's timer: one tick after the given time, replacing one that was asked for before.</summary>
    private void ScheduleMonitorSettle(TimeSpan after)
    {
        if (_monitorSettleTimer == null)
        {
            _monitorSettleTimer = new DispatcherTimer();
            _monitorSettleTimer.Tick += (_, _) =>
            {
                _monitorSettleTimer?.Stop();
                try { _monitorFollower?.Tick(); }
                catch (Exception ex) { ErrorLog.Shared.Record(ex, "Thumbnails (layout for the monitors)", DateTime.Now); }
            };
        }
        _monitorSettleTimer.Stop();
        _monitorSettleTimer.Interval = after;
        _monitorSettleTimer.Start();
    }

    // ── Window Found / Lost ─────────────────────────────────────────

    private static void PerfLog(string msg) => App.PerfLog(msg);

    private void OnWindowFound(EveWindow window)
    {
        // Accumulate discovered windows and batch-create thumbnails.
        // This prevents 20+ sequential BeginInvoke calls from blocking the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _pendingWindows.Add(window);

            // Restart the batch timer — after 50ms of no new windows, process the batch
            if (_batchTimer == null)
            {
                _batchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _batchTimer.Tick += (_, _) =>
                {
                    _batchTimer.Stop();
                    CreateThumbnailBatch();
                };
            }
            else
            {
                _batchTimer.Stop();
            }
            _batchTimer.Start();
        });
    }

    private void CreateThumbnailBatch()
    {
        // Race guard: this method can be reached via _batchTimer.Tick during
        // the narrow window between Application.Shutdown() being called and
        // OnExit running our timer stops. TextOverlayWindow's constructor
        // calls Application.LoadComponent which throws InvalidOperationException
        // ("The Application object is being shut down") if we proceed.
        // Detect that state and bail before allocating any WPF windows.
        // (Reported by @CatsLiKeDogs, issue #42.)
        var app = Application.Current;
        if (app == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
        {
            _pendingWindows.Clear();
            return;
        }

        var batch = new List<EveWindow>(_pendingWindows);
        _pendingWindows.Clear();

        if (batch.Count == 0) return;

        var totalSw = Stopwatch.StartNew();
        const int groupSize = 5;
        var deferredWindows = new List<EveWindow>();

        for (int i = 0; i < batch.Count; i++)
        {
            CreateThumbnailForWindow(batch[i]);
            deferredWindows.Add(batch[i]);

            // Yield to the UI thread between groups to keep the app responsive
            if ((i + 1) % groupSize == 0 && i + 1 < batch.Count)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        PerfLog($"✅ Batch created {batch.Count} thumbnails in {totalSw.ElapsedMilliseconds}ms");

        // Single consolidated deferred timer for all secondary/stat windows
        if (deferredWindows.Count > 0)
        {
            var deferTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            deferTimer.Tick += (_, _) =>
            {
                deferTimer.Stop();
                var deferSw = Stopwatch.StartNew();
                foreach (var window in deferredWindows)
                    CreateDeferredWindows(window);
                PerfLog($"✅ Batch deferred {deferredWindows.Count} secondary/stat windows in {deferSw.ElapsedMilliseconds}ms");
            };
            deferTimer.Start();
        }
    }

    /// <summary>
    /// Reset every primary thumbnail to its default flow-layout slot — the same
    /// placement a fresh launch produces. Clears the current profile's saved
    /// per-character positions (so the reset survives a relaunch), then re-sizes
    /// and re-lays out the live thumbnails from ThumbnailStartLocation on the
    /// preferred monitor. Primary thumbnails only — PiPs / stat overlays keep
    /// their own positions. (User-requested "Reset Positions" button.)
    /// </summary>
    public void ResetThumbnailPositions()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var s = _settings.Settings;

            // Snapshot the pre-reset layout so this (previously irreversible) Reset
            // can be undone.
            PushLayoutSnapshot();

            // Drop persisted positions first so they can't override the fresh layout.
            _settings.ClearThumbnailPositions();

            int width = (int)s.ThumbnailStartLocation.Width;
            int height = (int)s.ThumbnailStartLocation.Height;
            var workArea = GetTargetMonitorWorkArea(s.PreferredMonitor);
            int startX = workArea.Left + (int)s.ThumbnailStartLocation.X;
            int startY = workArea.Top + (int)s.ThumbnailStartLocation.Y;

            // Same flow-layout as CreateThumbnailForWindow: stack vertically, wrap
            // to a new column at the screen edge, ThumbnailGutter apart (IDEAS P27).
            int index = 0;
            foreach (var (_, thumb) in _thumbnails)
            {
                var (x, y) = FlowSlot(index, startX, startY, width, height, s.ThumbnailGutter, workArea.Bottom);
                (x, y) = EnsureOnScreen(x, y, width, height);
                thumb.Resize(width, height);
                thumb.MoveTo(x, y);
                index++;
            }

            _settings.Save();
            PerfLog($"[Reset] Thumbnail positions reset to default ({index} thumbnails)");
        });
    }

    // ── Layout undo / redo ──────────────────────────────────────────

    /// <summary>Snapshot the current wall layout (saved positions + live thumbnail
    /// bounds) onto the undo stack. Call BEFORE a destructive bulk op (Reset, drag-
    /// all, resize-all) so it can be reverted. Clears the redo stack.</summary>
    public void PushLayoutSnapshot()
    {
        _layoutUndo.Add(CaptureLiveLayout());
        if (_layoutUndo.Count > MaxLayoutHistory) _layoutUndo.RemoveAt(0);
        _layoutRedo.Clear();
    }

    public bool CanUndoLayout => _layoutUndo.Count > 0;
    public bool CanRedoLayout => _layoutRedo.Count > 0;

    /// <summary>Revert the wall to the last snapshot. False if nothing to undo.</summary>
    public bool UndoLayout()
    {
        if (_layoutUndo.Count == 0) { ShowTooltipFeedback(LocalizationService.Str("L.Msg.NothingToUndo", "Nothing to undo")); return false; }
        _layoutRedo.Add(CaptureLiveLayout());
        var snap = _layoutUndo[^1];
        _layoutUndo.RemoveAt(_layoutUndo.Count - 1);
        RestoreLayout(snap);
        ShowTooltipFeedback(LocalizationService.Str("L.Msg.LayoutUndone", "Layout: undone"));
        return true;
    }

    /// <summary>Re-apply the last undone layout. False if nothing to redo.</summary>
    public bool RedoLayout()
    {
        if (_layoutRedo.Count == 0) { ShowTooltipFeedback(LocalizationService.Str("L.Msg.NothingToRedo", "Nothing to redo")); return false; }
        _layoutUndo.Add(CaptureLiveLayout());
        var snap = _layoutRedo[^1];
        _layoutRedo.RemoveAt(_layoutRedo.Count - 1);
        RestoreLayout(snap);
        ShowTooltipFeedback(LocalizationService.Str("L.Msg.LayoutRedone", "Layout: redone"));
        return true;
    }

    private Dictionary<string, ThumbnailRect> CaptureLiveLayout()
    {
        // Saved positions (covers offline characters) overlaid with live thumbnail
        // bounds (authoritative for what's currently on screen).
        var snap = _settings.GetThumbnailPositionsSnapshot();
        foreach (var (_, thumb) in _thumbnails)
            if (!string.IsNullOrEmpty(thumb.CharacterName))
                snap[thumb.CharacterName] = new ThumbnailRect
                {
                    X = thumb.Left, Y = thumb.Top, Width = thumb.Width, Height = thumb.Height
                };
        return snap;
    }

    private void RestoreLayout(Dictionary<string, ThumbnailRect> snap)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _settings.RestoreThumbnailPositions(snap);
            foreach (var (_, thumb) in _thumbnails)
            {
                if (string.IsNullOrEmpty(thumb.CharacterName)) continue;
                if (snap.TryGetValue(thumb.CharacterName, out var r))
                {
                    thumb.Resize((int)r.Width, (int)r.Height);
                    thumb.MoveTo((int)r.X, (int)r.Y);
                }
            }
        });
    }

    // ── Layout presets (monitor-relative, shareable) ────────────────

    /// <summary>Capture the current wall as a named preset: each live character's
    /// position/size as fractions of the monitor it's on, so the preset survives
    /// resolution/monitor changes and transfers across machines.</summary>
    public LayoutPreset CaptureCurrentLayoutPreset(string name)
    {
        var preset = new LayoutPreset
        {
            Name = name,
            IncludesVisibility = true,
            CropsEnabled = _settings.Settings.CropEnabled,
        };
        var screens = System.Windows.Forms.Screen.AllScreens;
        preset.MonitorFingerprint = MonitorFingerprint.Of(screens.Select(s => s.Bounds));   // IDEAS #10: the arrangement this layout is for
        foreach (var (_, thumb) in _thumbnails)
        {
            if (string.IsNullOrEmpty(thumb.CharacterName)) continue;
            var rect = new System.Drawing.Rectangle(
                (int)thumb.Left, (int)thumb.Top, (int)Math.Max(1, thumb.Width), (int)Math.Max(1, thumb.Height));
            var screen = System.Windows.Forms.Screen.FromRectangle(rect);
            int mon = Array.FindIndex(screens, s => s.DeviceName == screen.DeviceName);
            if (mon < 0) mon = 0;
            var wa = screen.WorkingArea;
            double ww = Math.Max(1, wa.Width), wh = Math.Max(1, wa.Height);
            preset.Slots.Add(new LayoutSlot
            {
                Character = thumb.CharacterName,
                Monitor = mon,
                Fx = (thumb.Left - wa.Left) / ww,
                Fy = (thumb.Top - wa.Top) / wh,
                Fw = thumb.Width / ww,
                Fh = thumb.Height / wh,
                Hidden = IsCharacterUserHidden(thumb.CharacterName),
            });
        }
        return preset;
    }

    /// <summary>Apply a preset to the live wall. Slots match characters by exact name
    /// first; any leftover slots map positionally to remaining characters (so a
    /// shared preset with different character names still arranges your clients).</summary>
    /// <param name="forMonitors">IDEAS #10: applied because the monitors changed; the tooltip says so.</param>
    public void ApplyLayoutPreset(LayoutPreset preset, bool forMonitors = false)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            PushLayoutSnapshot();   // applying a preset is undoable
            var screens = System.Windows.Forms.Screen.AllScreens;
            var live = _thumbnails.Values.Where(t => !string.IsNullOrEmpty(t.CharacterName)).ToList();
            var used = new HashSet<ThumbnailWindow>();

            bool applyVis = preset.IncludesVisibility;
            var leftover = new List<LayoutSlot>();
            foreach (var slot in preset.Slots)
            {
                var thumb = live.FirstOrDefault(t => !used.Contains(t)
                    && string.Equals(t.CharacterName, slot.Character, StringComparison.OrdinalIgnoreCase));
                if (thumb != null) { ApplyPresetSlot(thumb, slot, screens, applyVis); used.Add(thumb); }
                else leftover.Add(slot);
            }

            var remaining = live.Where(t => !used.Contains(t))
                .OrderBy(t => t.CharacterName, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < leftover.Count && i < remaining.Count; i++)
                ApplyPresetSlot(remaining[i], leftover[i], screens, applyVis);

            // PRE-POSITION offline characters (#94). Slots left over after the live
            // thumbnails are used up belong to characters that aren't logged in yet —
            // previously they were simply dropped, so you had to log a character in
            // and re-apply just to record its spot. Persisting the resolved position
            // now means the thumbnail appears in the right place the moment that
            // client logs in: CreateThumbnailForWindow already reads these saved
            // positions. Purely additive — these slots did nothing before.
            int preStaged = 0;
            for (int i = remaining.Count; i < leftover.Count; i++)
            {
                var slot = leftover[i];
                if (string.IsNullOrWhiteSpace(slot.Character)) continue;
                var (sx, sy, sw, sh) = ResolveSlotRect(slot, screens);
                _settings.SaveThumbnailPosition(slot.Character, sx, sy, sw, sh);
                if (applyVis) _settings.Settings.ThumbnailVisibility[slot.Character] = slot.Hidden ? 1 : 0;
                preStaged++;
            }
            if (preStaged > 0) _settings.Save();

            // Crop master-toggle captured with the preset (CropManager picks this up
            // when the caller refreshes it after apply).
            if (applyVis) { _settings.Settings.CropEnabled = preset.CropsEnabled; _settings.Save(); }

            string shownPreset = StreamerMask.Name(StreamerKind.Layout, preset.Name);   // streamer mode (IDEAS #19): drawn text only
            ShowTooltipFeedback(forMonitors
                ? string.Format(LocalizationService.Str("L.Msg.LayoutAppliedForMonitors", "Monitors changed: applied layout '{0}'"), shownPreset)
                : preStaged > 0
                ? string.Format(LocalizationService.Str("L.Msg.LayoutAppliedSaved", "Layout: applied '{0}' ({1} saved for characters not logged in)"), shownPreset, preStaged)
                : string.Format(LocalizationService.Str("L.Msg.LayoutApplied", "Layout: applied '{0}'"), shownPreset));
        });
    }

    /// <summary>Apply a preset using an explicit slot-index → character mapping (the
    /// cross-machine remap dialog). Unmapped slots are skipped.</summary>
    public void ApplyLayoutPresetMapped(LayoutPreset preset, Dictionary<int, string> mapping)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            PushLayoutSnapshot();
            var screens = System.Windows.Forms.Screen.AllScreens;
            bool applyVis = preset.IncludesVisibility;

            for (int i = 0; i < preset.Slots.Count; i++)
            {
                if (!mapping.TryGetValue(i, out var targetChar) || string.IsNullOrEmpty(targetChar)) continue;
                var thumb = _thumbnails.Values.FirstOrDefault(t =>
                    string.Equals(t.CharacterName, targetChar, StringComparison.OrdinalIgnoreCase));
                if (thumb != null) ApplyPresetSlot(thumb, preset.Slots[i], screens, applyVis);
            }

            if (applyVis) { _settings.Settings.CropEnabled = preset.CropsEnabled; _settings.Save(); }
            ShowTooltipFeedback(string.Format(LocalizationService.Str("L.Msg.LayoutAppliedRemapped", "Layout: applied '{0}' (remapped)"), StreamerMask.Name(StreamerKind.Layout, preset.Name)));
        });
    }

    /// <summary>Resolve a preset slot's fractional geometry to an absolute on-screen
    /// rect for THIS machine's monitors. Shared by the live-thumbnail apply path and
    /// the offline-character pre-position path.</summary>
    private (int x, int y, int w, int h) ResolveSlotRect(LayoutSlot slot, System.Windows.Forms.Screen[] screens)
    {
        var screen = (slot.Monitor >= 0 && slot.Monitor < screens.Length)
            ? screens[slot.Monitor]
            : (System.Windows.Forms.Screen.PrimaryScreen ?? screens[0]);
        var wa = screen.WorkingArea;
        int w = Math.Max(40, (int)(slot.Fw * wa.Width));
        int h = Math.Max(30, (int)(slot.Fh * wa.Height));
        int x = wa.Left + (int)(slot.Fx * wa.Width);
        int y = wa.Top + (int)(slot.Fy * wa.Height);
        (x, y) = EnsureOnScreen(x, y, w, h);
        return (x, y, w, h);
    }

    private void ApplyPresetSlot(ThumbnailWindow thumb, LayoutSlot slot, System.Windows.Forms.Screen[] screens, bool applyVis)
    {
        var (x, y, w, h) = ResolveSlotRect(slot, screens);
        thumb.Resize(w, h);
        thumb.MoveTo(x, y);
        if (!string.IsNullOrEmpty(thumb.CharacterName))
            _settings.SaveThumbnailPosition(thumb.CharacterName, x, y, w, h);

        // Restore the captured show/hide state for this character.
        if (applyVis && !string.IsNullOrEmpty(thumb.CharacterName))
        {
            _settings.Settings.ThumbnailVisibility[thumb.CharacterName] = slot.Hidden ? 1 : 0;
            if (slot.Hidden) thumb.HideWithOverlay();
            else if (!_thumbnailsHidden && !_primaryHidden) thumb.ShowWithOverlay();
        }
    }

    private void CreateThumbnailForWindow(EveWindow window)
    {
        // Guard: skip if we already track this HWND
        if (_thumbnails.ContainsKey(window.Hwnd))
        {
            Debug.WriteLine($"[Thumbnail:Dedup] ⚠️ Skipped duplicate HWND 0x{window.Hwnd:X} for '{window.CharacterName}'");
            return;
        }

        // Guard: one thumbnail per process — exefile can spawn secondary windows
        // that slip past class-name filters. Only the first HWND per PID gets a thumbnail.
        Interop.User32.GetWindowThreadProcessId(window.Hwnd, out uint newPid);
        foreach (var (existingHwnd, _) in _thumbnails)
        {
            Interop.User32.GetWindowThreadProcessId(existingHwnd, out uint existingPid);
            if (existingPid == newPid)
            {
                Debug.WriteLine($"[Thumbnail:Dedup] ⚠️ Skipped HWND 0x{window.Hwnd:X} — PID {newPid} already tracked by 0x{existingHwnd:X}");
                return;
            }
        }

        var sw = Stopwatch.StartNew();

        var s = _settings.Settings;
        int width = (int)s.ThumbnailStartLocation.Width;
        int height = (int)s.ThumbnailStartLocation.Height;
        int x = (int)s.ThumbnailStartLocation.X;
        int y = (int)s.ThumbnailStartLocation.Y;

        // Check for saved position
        var savedPos = _settings.GetThumbnailPosition(window.CharacterName);
        if (savedPos != null && !string.IsNullOrEmpty(window.CharacterName))
        {
            x = (int)savedPos.X;
            y = (int)savedPos.Y;
            width = (int)savedPos.Width;
            height = (int)savedPos.Height;
        }
        else
        {
            // No saved position → place on the PREFERRED monitor (issue #70). The
            // configured start X/Y is treated as an offset from that monitor's work
            // area, so defaults land on the chosen screen instead of always the
            // primary. (For the primary monitor work-area Left/Top are 0, so this is
            // a no-op there — no regression for single-monitor users.)
            var workArea = GetTargetMonitorWorkArea(s.PreferredMonitor);
            x = workArea.Left + (int)s.ThumbnailStartLocation.X;
            y = workArea.Top + (int)s.ThumbnailStartLocation.Y;

            // Flow-layout: stack vertically, wrap to new column at screen edge, ThumbnailGutter apart (IDEAS P27)
            (x, y) = FlowSlot(_thumbnails.Count, x, y, width, height, s.ThumbnailGutter, workArea.Bottom);
        }

        // Never create a thumbnail fully off-screen (issue #74) — whether from the
        // preferred-monitor default, a stale saved position, or column overflow.
        (x, y) = EnsureOnScreen(x, y, width, height);

        PerfLog($"Settings lookup: {sw.ElapsedMilliseconds}ms");

        var thumbWindow = new ThumbnailWindow();
        PerfLog($"ThumbnailWindow ctor: {sw.ElapsedMilliseconds}ms");

        thumbWindow.Initialize(window.Hwnd, window.CharacterName, width, height, x, y);
        PerfLog($"Initialize: {sw.ElapsedMilliseconds}ms");

        // Apply all settings
        ApplySettings(thumbWindow, window);
        PerfLog($"ApplySettings: {sw.ElapsedMilliseconds}ms");

        // Wire events
        thumbWindow.PositionChanged += OnThumbnailPositionChanged;
        thumbWindow.SwitchRequested += OnSwitchRequested;
        thumbWindow.MinimizeRequested += OnMinimizeRequested;
        thumbWindow.DragMoveAll += OnDragMoveAll;
        thumbWindow.ResizeAll += OnResizeAll;
        thumbWindow.CycleExclusionRequested += OnCycleExclusionRequested;
        WireMenu(thumbWindow);
        // IDEAS #13 (T6-m10): an enlarged thumbnail raises itself; not while the settings window is in front.
        thumbWindow.RaiseSuppressed = () => _suppressTopmost;

        // Restore any prior session-exclusion visual state
        if (!string.IsNullOrEmpty(thumbWindow.CharacterName)
            && _excludedFromCycle.ContainsKey(thumbWindow.CharacterName))
        {
            thumbWindow.SetCycleExcluded(true);
        }

        PerfLog($"Pre-Show: {sw.ElapsedMilliseconds}ms");
        thumbWindow.Show();
        PerfLog($"Show: {sw.ElapsedMilliseconds}ms");
        _thumbnails[window.Hwnd] = thumbWindow;
        if (InFront(window.Hwnd, Interop.User32.GetForegroundWindow())) NoteClientInFront(window.Hwnd);   // IDEAS #56: see NoteClientInFront
        NoteRosterChanged();   // IDEAS P19: it was painted before it was counted

        // Honor a per-character "Hidden" choice (Visibility tab, 1 = hidden) and the
        // global hide-all toggles on a freshly created thumbnail. Without this, a
        // hidden character's thumbnail pops up on launch / relaunch and only the
        // ReapplySettings path (triggered by a settings change) would hide it — so
        // the user had to uncheck/recheck the option every session (#61).
        if (!string.IsNullOrEmpty(window.CharacterName)
            && s.ThumbnailVisibility.TryGetValue(window.CharacterName, out var visFlag)
            && visFlag != 0)
        {
            thumbWindow.HideWithOverlay();
        }
        else if (_thumbnailsHidden || _primaryHidden)
        {
            thumbWindow.HideWithOverlay();
        }
        if (_nameBarHold) thumbWindow.SetHeldHidden(true);   // IDEAS #38: the name bar holds every main thumbnail, a new one too

        // Apply system name *after* window show to guarantee WPF visibility rendering
        if (!string.IsNullOrEmpty(window.CharacterName) &&
            _settings.Settings.ShowSystemName &&
            _charSystems.TryGetValue(window.CharacterName, out var finalSys))
        {
            thumbWindow.UpdateSystemName(finalSys);
        }

        // Restore client position if tracking; otherwise apply a fixed spawn
        // position (center / custom) so fixed-window clients don't all open in
        // the top-left corner on large/ultrawide monitors (issue #85).
        // Fixed slots (mode 3) take precedence over Track positions: EVE relaunches at
        // its remembered size, so the slot must be re-applied on every new window.
        if (s.ClientPositionMode == 3)
            ApplyClientSlot(window.Hwnd, window.CharacterName);
        else if (s.TrackClientPositions)
            RestoreClientPosition(window.Hwnd, window.CharacterName);
        else
            ApplyFixedClientPosition(window.Hwnd);

        PerfLog(
            $"✅ Created thumbnail @ ({x},{y}) {width}x{height} [{sw.ElapsedMilliseconds}ms]");   // IDEAS P94: never whose
    }

    private void CreateDeferredWindows(EveWindow window)
    {
        var s = _settings.Settings;

        // Create secondary PiP if configured
        if (!string.IsNullOrEmpty(window.CharacterName) &&
            s.SecondaryThumbnails.TryGetValue(window.CharacterName, out var secSettings) &&
            secSettings.Enabled != 0)
        {
            CreateSecondaryThumbnail(window.CharacterName, window.Hwnd, secSettings);
        }

        // Create stat windows if configured
        CreateStatWindowsForCharacter(window.CharacterName, window.Hwnd);
    }

    private void OnWindowLost(EveWindow window)
    {
        _iconicHwnds.TryRemove(window.Hwnd, out _);
        _frozenFrames?.Forget(window.Hwnd);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _slotInsets.TryRemove(window.Hwnd, out _);
            _clientHistory.Closed(window.Hwnd);   // IDEAS #56: "back" never goes to a client that closed
            if (_thumbnails.TryRemove(window.Hwnd, out var thumbWindow))
            {
                NoteRosterChanged();   // IDEAS P19
                if (!string.IsNullOrEmpty(thumbWindow.CharacterName))
                {
                    _settings.SaveThumbnailPosition(
                        thumbWindow.CharacterName,
                        (int)thumbWindow.Left, (int)thumbWindow.Top,
                        (int)thumbWindow.Width, (int)thumbWindow.Height);

                    // Destroy secondary PiP
                    DestroySecondaryThumbnail(thumbWindow.CharacterName);

                    // Destroy stat windows
                    DestroyStatWindowsForCharacter(thumbWindow.CharacterName);

                    // Its unseen alerts go with it (IDEAS batch 5): nothing there needs the player any more.
                    Attention.Gone(thumbWindow.CharacterName);
                }

                UnwireEvents(thumbWindow);
                thumbWindow.Cleanup();
                thumbWindow.Close();
            }
        });
    }

    /// <summary>IDEAS P19: automatic border colours are chosen among the characters that have a thumbnail, so a
    /// thumbnail added or removed, or a new character name, can change other clients' colours. The border is painted
    /// by the focus sweep only when the foreground changes, so it is made to run again: the 250 ms timer then
    /// repaints every border. Nothing is done while the switch is off.</summary>
    private void NoteRosterChanged()
    {
        if (_settings.Settings.AutoCharacterBorders) _lastSweptHwnd = IntPtr.Zero;
    }

    private void OnWindowTitleChanged(EveWindow window, string oldTitle)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_thumbnails.TryGetValue(window.Hwnd, out var thumbWindow))
            {
                // Save position under old name
                string oldChar = CleanTitle(oldTitle);
                if (!string.IsNullOrEmpty(oldChar))
                {
                    _settings.SaveThumbnailPosition(oldChar,
                        (int)thumbWindow.Left, (int)thumbWindow.Top,
                        (int)thumbWindow.Width, (int)thumbWindow.Height);
                }

                thumbWindow.UpdateCharacterName(window.CharacterName);
                NoteRosterChanged();   // IDEAS P19: a new name is a new roster

                // The character logged out of this window (or another one logged in): its unseen alerts are
                // not this client's any more (IDEAS batch 5). Below the new name: an alert raised for the old
                // one from here on finds no client for it (NoteUnseen asks twice), and one raised before is
                // taken off here.
                if (CharacterLeft(oldChar, window.CharacterName)) Attention.Gone(oldChar);

                // Gone takes the old character's badge out of the table (Attention.Looked), but the repaint it posts finds
                // no thumbnail by the old name any more: this window shows nothing for whoever is on it now.
                if (CharacterLeft(oldChar, window.CharacterName)) thumbWindow.SetAlertBadge(0, "#FF0000");

                // And an enlargement for an alert was that character's too (IDEAS #13, T6-C1). The window stays and
                // only its name changes, so AlertEnlarge finds no thumbnail by the old name and forgets it without
                // telling anybody: this window is put back here, at once, or it would pass enlarged to whoever
                // logs in on it next. (The watchdog in UpdateActiveBorders would do it at the next sweep.)
                if (CharacterLeft(oldChar, window.CharacterName)) thumbWindow.SetAlertScale(1.0);

                // "Client ready" (batch 13, P12): "EVE" became "EVE - Name" after discovery's first pass.
                if (ClientReady.ShouldRaise(oldChar, window.CharacterName, _discovery.FirstPassDone)) CharacterReady?.Invoke(window.CharacterName);

                // UpdateCharacterName force-shows the name overlay; re-assert the
                // user's "show character name" preference so a disabled overlay
                // doesn't reappear when the character resolves at login (issue #63).
                thumbWindow.SetTextOverlayVisible(_settings.Settings.ShowThumbnailTextOverlay);

                // Re-apply cycle-exclusion visual after a character resolves
                // post char-select. The thumbnail was created with an empty
                // CharacterName (full EVE relaunch lands at char-select first),
                // so the initial check in CreateThumbnail couldn't match the
                // _excludedFromCycle entry. Issue #39 — without this re-apply
                // the red X disappeared after a full client restart even
                // though the character was still functionally excluded.
                if (!string.IsNullOrEmpty(window.CharacterName))
                {
                    bool isExcluded = _excludedFromCycle.ContainsKey(window.CharacterName);
                    thumbWindow.SetCycleExcluded(isExcluded);
                }

                // Fixed slots: a client at character select sits in the default slot;
                // once the character resolves, move it to that character's own slot.
                ApplyClientSlot(window.Hwnd, window.CharacterName);

                // Check if char select (title == "EVE" without character name)
                bool isCharSelect = string.IsNullOrEmpty(window.CharacterName) ||
                    window.Title == "EVE";
                thumbWindow.SetNotLoggedIn(isCharSelect, _settings.Settings.NotLoggedInIndicator,
                    ParseColor(_settings.Settings.NotLoggedInColor));

                // Resolve the system label to a DEFINITE state on every title
                // change so a character switch can't leave the PREVIOUS character's
                // system showing (issue #83 — logout/login another char in a
                // different system kept the old system name). Cases:
                //   • feature off  → clear (also stops a switch silently re-enabling
                //     the overlay when off — issue #42, bug #1)
                //   • feature on, new char's system already known → show it
                //   • feature on, new char's system NOT known yet (just logged in /
                //     at char-select) → clear; UpdateCharacterSystem repaints it once
                //     the game log reports the new character's location.
                if (_settings.Settings.ShowSystemName
                    && !string.IsNullOrEmpty(window.CharacterName)
                    && _charSystems.TryGetValue(window.CharacterName, out var sys))
                {
                    thumbWindow.UpdateSystemName(sys);
                }
                else
                {
                    thumbWindow.UpdateSystemName(null);
                }

                // Re-apply custom label + per-label style for the now-resolved
                // character (fixes the persistence bug where labels saved for a
                // character were never restored if the EVE window first appeared
                // at character-select with an empty name).
                if (!string.IsNullOrEmpty(window.CharacterName))
                {
                    var s = _settings.Settings;
                    if (s.ThumbnailAnnotations.TryGetValue(window.CharacterName, out var annotation))
                        thumbWindow.UpdateAnnotation(annotation);
                    else
                        thumbWindow.UpdateAnnotation(null);

                    if (s.ThumbnailLabelStyles.TryGetValue(window.CharacterName, out var labelStyle))
                        thumbWindow.SetLabelStyle(labelStyle.Color, labelStyle.Size);
                    else
                        thumbWindow.SetLabelStyle(null, 0);
                }

                // The character's own text colour (or the profile's, when it has none): a client that
                // starts at character select would otherwise keep the profile colour after login, and a
                // switch from A to B would leave A's colour on B.
                ApplyTextStyle(thumbWindow, _settings.Settings, window.CharacterName);

                // Load saved position for new character
                if (!string.IsNullOrEmpty(window.CharacterName))
                {
                    var savedPos = _settings.GetThumbnailPosition(window.CharacterName);
                    if (savedPos != null)
                    {
                        thumbWindow.MoveTo((int)savedPos.X, (int)savedPos.Y);
                        thumbWindow.Resize((int)savedPos.Width, (int)savedPos.Height);
                    }

                    // Create stat windows for the new character
                    CreateStatWindowsForCharacter(window.CharacterName, window.Hwnd);

                    // Apply the per-character Hidden choice now that the character
                    // is known. At creation the window was at char-select with an
                    // empty name, so the creation-time hide couldn't match it — the
                    // thumbnail would otherwise stay visible at login until a manual
                    // toggle re-ran ReapplySettings (issue #63 / follow-up to #61).
                    // Applied last so the MoveTo/Resize above can't re-show it.
                    if (IsCharacterUserHidden(window.CharacterName) || _thumbnailsHidden || _primaryHidden)
                        thumbWindow.HideWithOverlay();
                }
            }
        });
    }

    /// <summary>True when the user marked this character Hidden in the Visibility
    /// tab (ThumbnailVisibility value != 0, where 1 = hidden).</summary>
    private bool IsCharacterUserHidden(string characterName) =>
        !string.IsNullOrEmpty(characterName)
        && _settings.Settings.ThumbnailVisibility.TryGetValue(characterName, out var v)
        && v != 0;

    // ── Settings Application ────────────────────────────────────────

    /// <summary>The zoom ApplySettings gives a thumbnail: HoverScale while hover zoom is on, else 1.0,
    /// which ThumbnailWindow treats as no zoom. Unticking hover zoom used to keep the old scale until
    /// a restart, because the scale was only ever set while it was on.</summary>
    public static double HoverScaleFor(AppSettings s) => s.ResizeThumbnailsOnHover ? s.HoverScale : 1.0;

    /// <summary>A second thumbnail's own opacity (SecondaryThumbnailSettings.Opacity, the value
    /// ReapplySettings applies); the main thumbnail opacity only when it has no entry.</summary>
    public static byte PipOpacityFor(AppSettings s, string character) =>
        (byte)Math.Clamp(s.SecondaryThumbnails.TryGetValue(character, out var ss) ? ss.Opacity : s.ThumbnailOpacity, 0, 255);

    /// <summary>A character's thumbnail text colour: its own (Character page › Colours) while per-character colours are on
    /// and one was chosen, else the profile's text colour, as always (CustomColorEntry.HasOwnText).</summary>
    public static string TextColourFor(AppSettings s, string character) =>
        s.CustomColorsActive && !string.IsNullOrEmpty(character) && s.CustomColors.TryGetValue(character, out var own)
        && CustomColorEntry.HasOwnText(own.Text)
            ? own.Text
            : s.ThumbnailTextColor;

    /// <summary>Paints a thumbnail's text style: the profile's font and size, and the character's own text colour when it has one.
    /// ApplySettings and the title-change path share it, so a client that logs in (or switches character) gets the right colour.</summary>
    public static void ApplyTextStyle(ThumbnailWindow thumb, AppSettings s, string character) =>
        thumb.SetTextStyle(s.ThumbnailTextFont,
            double.TryParse(s.ThumbnailTextSize, out var fs) ? fs : 12,
            ParseColor(TextColourFor(s, character)),
            s.FpsOverlayTextSize);

    /// <summary>The range the resize limit's width and height may take (the settings cards use it too).</summary>
    public const int MinFloor = 20, MaxFloor = 2000;

    /// <summary>The smallest size a thumbnail can be dragged to (IDEAS P26): 80 × 50, as it always was, until the player
    /// turns on the limit; then the minimum size, held to MinFloor–MaxFloor. Files store 50 × 50, EVE-O imports 192 × 108
    /// and old AHK files 100 × 60, none of which ever had an effect, so nobody's drags change by themselves.</summary>
    public static (int Width, int Height) ResizeFloor(AppSettings s) =>
        s.ThumbnailMinimumSizeEnabled && s.ThumbnailMinimumSize is { } min
            ? (Math.Clamp((int)Math.Round(min.Width), MinFloor, MaxFloor), Math.Clamp((int)Math.Round(min.Height), MinFloor, MaxFloor))
            : (80, 50);

    /// <summary>Where the index-th thumbnail goes in the column MultiPreview lays out for new clients and Reset positions:
    /// down from the start place, a new column at the bottom of the work area, ThumbnailGutter apart (IDEAS P27; it was a
    /// fixed 8 px, the default every file stores).</summary>
    public static (int X, int Y) FlowSlot(int index, int startX, int startY, int width, int height, int gap, int workBottom)
    {
        gap = Math.Clamp(gap, 0, 200);   // the card's range; a hand-edited file can't overflow the layout
        int maxPerCol = Math.Max(1, (workBottom - startY) / Math.Max(1, height + gap));
        return (startX + index / maxPerCol * (width + gap), startY + index % maxPerCol * (height + gap));
    }

    private void ApplySettings(ThumbnailWindow thumb, EveWindow window)
    {
        var s = _settings.Settings;

        // Lock positions
        thumb.IsLocked = s.LockPositions;

        // Opacity
        thumb.SetOpacity((byte)s.ThumbnailOpacity);
        thumb.OpacityOnHover = s.OpacityOnHover;

        // Cycle-exclusion badge position (one of nine anchor points). Issue #41.
        thumb.SetCycleExclusionPosition(s.CycleExclusionBadgePosition);
        thumb.SetAlertTextPosition(s.AlertTextPosition);

        // Always on top
        thumb.SetTopmost(s.ShowThumbnailsAlwaysOnTop);

        // Confine drags to the starting monitor (edge-snap feature).
        thumb.ConfineDragsToMonitor = s.ConfineDragsToMonitor;

        // Keep a resized thumbnail's own shape (IDEAS P7).
        thumb.LockResizeAspect = s.LockResizeAspect;

        // The smallest size a resize drag gives (IDEAS P26).
        var (floorW, floorH) = ResizeFloor(s);
        thumb.ResizeFloor = new System.Drawing.Size(floorW, floorH);

        // Background color
        thumb.SetBackgroundColor(ParseColor(s.ThumbnailBackgroundColor));

        // Hover zoom. 1.0 = off, so unticking it applies at once.
        thumb.SetHoverScale(HoverScaleFor(s));

        // Rounded corners (IDEAS P8). 0 = square, as always.
        thumb.SetCornerRadius(s.ThumbnailCornerRadius);

        // Text overlay. The template first (IDEAS P2): the name, system, label and timer calls below then fill it, and a
        // toggle that is off leaves its token empty. Empty template = the separate blocks, as always.
        thumb.SetTextTemplate(s.ThumbnailTextTemplate);
        thumb.SetTextOverlayVisible(s.ShowThumbnailTextOverlay);
        ApplyTextStyle(thumb, s, window.CharacterName);
        thumb.SetTextMargins((int)s.ThumbnailTextMargins.X, (int)s.ThumbnailTextMargins.Y,
            s.FpsOverlayMarginX, s.FpsOverlayMarginY);

        // Process stats text size
        thumb.SetProcessStatsTextSize(
            double.TryParse(s.ProcessStatsTextSize, out var pfs) ? pfs : 9);
        if (!s.ShowProcessStats)
            thumb.UpdateProcessStats(null);

        // System name
        if (s.ShowSystemName && _charSystems.TryGetValue(window.CharacterName, out var sys))
            thumb.UpdateSystemName(sys);
        else
            thumb.UpdateSystemName(null);

        // Annotation label + per-label style overrides (v2.0.6)
        if (s.ThumbnailAnnotations.TryGetValue(window.CharacterName, out var annotation))
            thumb.UpdateAnnotation(annotation);
        else
            thumb.UpdateAnnotation(null);

        if (s.ThumbnailLabelStyles.TryGetValue(window.CharacterName, out var labelStyle))
            thumb.SetLabelStyle(labelStyle.Color, labelStyle.Size);
        else
            thumb.SetLabelStyle(null, 0);

        // Border — show with appropriate color, or hide if inactive border is empty
        {
            var borderColor = GetBorderColor(window.CharacterName, false);
            int thickness = ShouldShowInactiveBorder(window.CharacterName) ? s.InactiveClientBorderThickness : 0;
            thumb.SetBorder(borderColor, thickness);
        }

        // Not logged in
        bool isCharSelect = string.IsNullOrEmpty(window.CharacterName) || window.Title == "EVE";
        thumb.SetNotLoggedIn(isCharSelect, s.NotLoggedInIndicator, ParseColor(s.NotLoggedInColor));
    }

    /// <summary>
    /// Tracks whether Settings is active; gates BringToFront so we don't fight
    /// Settings for the top of the topmost band. Thumbnails stay at the user's
    /// configured topmost state — Settings interaction is preserved by
    /// click-through (SetSettingsClickSuppression), not by dropping topmost,
    /// so the user never sees previews fall behind other apps while Settings
    /// is open.
    /// </summary>
    public void SetSuppressTopmost(bool suppress)
    {
        _suppressTopmost = suppress;
        // Intentionally do NOT mutate topmost state on thumbnails/overlays/stats here.
        // Prior versions flipped topmost=false while Settings was open, which caused
        // previews to drop behind the browser/file-explorer even though the user had
        // "Always on top" enabled. Click-through already makes Settings reachable.
    }

    /// <summary>
    /// Marks the tray context menu as open/closed. Opening the menu makes this app
    /// the foreground process, which the focus sweep treats like "EVE regained focus":
    /// it re-shows hidden thumbnails (a WPF overlay Show() takes activation, which
    /// closes the menu) and raises everything to HWND_TOPMOST (above the menu). The
    /// sweep is paused while the menu is open and resumes on the next tick after it closes.
    /// </summary>
    public void SetTrayMenuOpen(bool open) => _trayMenuOpen = open;

    /// <summary>Whether the tray menu is on the screen: App hands it the menu's Visible (IDEAS P98 (7)). The focus sweep asks it
    /// only while <see cref="SetTrayMenuOpen"/> says the menu is open, and clears that flag when the menu is not there, so a
    /// "closed" that never came (a menu cancelled while it opened) cannot pause the sweep for good. Null: the flag is believed.</summary>
    public Func<bool>? TrayMenuShown { get; set; }

    /// <summary>The flag after the sweep's look at the menu: a flag that is down stays down without a look; one that is up stays up
    /// while the probe says the menu is shown, or when there is no probe, and falls when the menu is not shown or the probe
    /// throws (its menu is gone).</summary>
    public static bool TrayMenuStillOpen(bool flagged, Func<bool>? shown)
    {
        if (!flagged || shown == null) return flagged;
        try { return shown(); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Marks the Settings window as open/closed. While open (even minimized),
    /// thumbnails are kept visible so HideThumbnailsOnLostFocus doesn't erase
    /// them the moment the user clicks another app while Settings is in the tray.
    /// </summary>
    public void SetSettingsOpen(bool open)
    {
        _settingsOpen = open;
        if (open) return;

        // On close, force a re-show in case previews were hidden while another
        // app held focus during the Settings session.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_thumbnailsHidden || _primaryHidden) return;
            foreach (var (_, thumb) in _thumbnails)
                thumb.ShowDwmOnly();
            foreach (var (_, sw) in _statWindows)
                sw.Visibility = Visibility.Visible;
            foreach (var (_, pip) in _secondaryThumbnails)
                pip.Visibility = Visibility.Visible;
        }));
    }

    /// <summary>Resize EVERY live thumbnail to an exact pixel size and persist it.
    /// Also updates the default start size so newly-appearing clients match.
    /// Positions are left alone — only width/height change.</summary>
    public void ApplySizeToAll(int w, int h)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            PushLayoutSnapshot();   // one snapshot for the whole operation (layout undo)

            foreach (var (_, thumb) in _thumbnails)
            {
                thumb.Resize(w, h);
                if (!string.IsNullOrEmpty(thumb.CharacterName))
                    _settings.SaveThumbnailPosition(thumb.CharacterName, (int)thumb.Left, (int)thumb.Top, w, h);
            }

            // Keep the default in sync so clients that log in later get the same size.
            _settings.Settings.ThumbnailStartLocation.Width = w;
            _settings.Settings.ThumbnailStartLocation.Height = h;
            _settings.Save();
        });
    }

    /// <summary>IDEAS #36: align, distribute, match the size of or nudge the named thumbnails (Thumbnails › Layout &amp; screens ›
    /// Align, distribute and nudge; <see cref="LayoutAlign"/> does the arithmetic). Names with no thumbnail are left out;
    /// fewer than <see cref="LayoutAlign.MinItems"/> thumbnails change nothing. The result is held inside a work area
    /// (<paramref name="workAreas"/>, else the screens'), Match keeps each shape while the aspect lock is on, and the first
    /// name is the one the Match operations copy. One layout-undo step, each moved thumbnail's place saved, one save.
    /// False, with no undo step, when nothing moved.</summary>
    public bool ApplyAlign(AlignOp op, IReadOnlyList<string> names, int dx = 0, int dy = 0, IReadOnlyList<System.Drawing.Rectangle>? workAreas = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher != null && !dispatcher.CheckAccess()
            ? dispatcher.Invoke(() => AlignThumbnails(op, names, dx, dy, workAreas))
            : AlignThumbnails(op, names, dx, dy, workAreas);
    }

    private bool AlignThumbnails(AlignOp op, IReadOnlyList<string> names, int dx, int dy, IReadOnlyList<System.Drawing.Rectangle>? workAreas)
    {
        var picked = new List<ThumbnailWindow>();
        foreach (var name in names)
        {
            var thumb = _thumbnails.Values.FirstOrDefault(t => !string.IsNullOrEmpty(t.CharacterName)
                && string.Equals(t.CharacterName, name, StringComparison.OrdinalIgnoreCase));
            if (thumb != null && !picked.Contains(thumb)) picked.Add(thumb);
        }
        if (picked.Count < LayoutAlign.MinItems(op)) return false;

        var before = picked.Select(t => (Name: t.CharacterName,
            Rect: new System.Drawing.Rectangle((int)t.Left, (int)t.Top, (int)t.Width, (int)t.Height))).ToList();
        var areas = workAreas ?? System.Windows.Forms.Screen.AllScreens.Select(s => s.WorkingArea).ToList();
        var (floorW, floorH) = ResizeFloor(_settings.Settings);   // a Match never sets a size smaller than a drag could
        var after = LayoutAlign.Apply(op, before, dx, dy, _settings.Settings.LockResizeAspect, areas, new System.Drawing.Size(floorW, floorH));
        if (after.Select(a => a.Rect).SequenceEqual(before.Select(b => b.Rect))) return false;

        PushLayoutSnapshot();   // one snapshot for the whole operation (layout undo)
        for (int i = 0; i < picked.Count; i++)
        {
            var was = before[i].Rect;
            var now = after[i].Rect;
            if (now == was) continue;
            if (now.Size != was.Size) picked[i].Resize(now.Width, now.Height);
            picked[i].MoveTo(now.X, now.Y);
            _settings.SaveThumbnailPosition(picked[i].CharacterName, now.X, now.Y, now.Width, now.Height);
        }
        _settings.Save();
        return true;
    }

    public void ApplySizeToCharacter(string name, int w, int h)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(name)) return;
            var thumb = _thumbnails.Values.FirstOrDefault(t => string.Equals(t.CharacterName, name, StringComparison.OrdinalIgnoreCase));
            if (thumb != null)
            {
                thumb.Resize(w, h);
                var pos = _settings.GetThumbnailPosition(name) ?? new ThumbnailRect { X = (int)thumb.Left, Y = (int)thumb.Top };
                pos.Width = w;
                pos.Height = h;
                _settings.SaveThumbnailPosition(name, (int)pos.X, (int)pos.Y, w, h);
                _settings.Save();
            }
        });
    }

    /// <summary>
    /// Apply only opacity to all thumbnails and PiPs without triggering a full ReapplySettings.
    /// Used by the opacity slider for live preview without resizing thumbnails.
    /// </summary>
    public void ApplyOpacityToAll()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var opacity = (byte)_settings.Settings.ThumbnailOpacity;
            bool hoverFull = _settings.Settings.OpacityOnHover;
            foreach (var (_, thumb) in _thumbnails)
            {
                thumb.SetOpacity(opacity);
                thumb.OpacityOnHover = hoverFull;
            }
            // Each second thumbnail keeps its own opacity, the value ReapplySettings applies.
            foreach (var (name, pip) in _secondaryThumbnails)
            {
                pip.SetOpacity(PipOpacityFor(_settings.Settings, name));
                pip.OpacityOnHover = hoverFull;
            }
        });
    }

    /// <summary>
    /// Re-apply current settings to all existing thumbnails.
    /// Called when user clicks Apply in settings window.
    /// </summary>
    public void ReapplySettings()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var s = _settings.Settings;

            // If auto-solo audio was just turned off, restore every client's audio.
            if (!s.AutoSoloClientAudio) UnmuteAllClientAudio();

            foreach (var (eveHwnd, thumb) in _thumbnails)
            {
                string title = Interop.User32.GetWindowTitle(eveHwnd);
                var fakeWindow = new EveWindow(eveHwnd, title, thumb.CharacterName);
                ApplySettings(thumb, fakeWindow);

                // Move thumbnail to saved position from new profile
                if (!string.IsNullOrEmpty(thumb.CharacterName))
                {
                    var savedPos = _settings.GetThumbnailPosition(thumb.CharacterName);
                    if (savedPos != null)
                    {
                        // Use the SAVED per-thumbnail size, matching CreateThumbnailForWindow.
                        // Previously this snapped every thumbnail to the global default size
                        // when IndividualThumbnailResize was off — but creation honors each
                        // saved size, so clicking Apply (which runs ReapplySettings) resized
                        // thumbnails until relaunch (#79). Fall back to the global default only
                        // when there's no valid saved size.
                        int applyW = savedPos.Width > 0 ? (int)savedPos.Width : (int)s.ThumbnailStartLocation.Width;
                        int applyH = savedPos.Height > 0 ? (int)savedPos.Height : (int)s.ThumbnailStartLocation.Height;

                        thumb.MoveTo((int)savedPos.X, (int)savedPos.Y);
                        thumb.Resize(applyW, applyH);
                    }
                    else
                    {
                        // Apply layout defaults instantly to thumbnails that have never
                        // been manually moved — on the preferred monitor (issue #70).
                        var wa = GetTargetMonitorWorkArea(s.PreferredMonitor);
                        thumb.MoveTo(wa.Left + (int)s.ThumbnailStartLocation.X,
                                     wa.Top + (int)s.ThumbnailStartLocation.Y);
                        thumb.Resize((int)s.ThumbnailStartLocation.Width, (int)s.ThumbnailStartLocation.Height);
                    }
                }

                // Respect a per-character Visibility-tab hide. ApplySettings
                // re-shows the text overlay (SetTextOverlayVisible) and the
                // MoveTo/Resize above can re-show the window, so a thumbnail
                // the user hid would otherwise pop back on any settings
                // re-apply (issues #50 / #51). Re-hide it last.
                if (!string.IsNullOrEmpty(thumb.CharacterName)
                    && s.ThumbnailVisibility.TryGetValue(thumb.CharacterName, out var visFlag)
                    && visFlag != 0)
                {
                    thumb.HideWithOverlay();
                }
            }

            // ── Sync PiP thumbnails: create missing, destroy removed ──
            // Create PiPs for enabled entries that don't exist yet
            foreach (var kv in s.SecondaryThumbnails)
            {
                if (kv.Value.Enabled == 0) continue;
                if (_secondaryThumbnails.ContainsKey(kv.Key)) continue;

                // Find the EVE HWND
                IntPtr eveHwnd = IntPtr.Zero;
                foreach (var (hwnd, thumb) in _thumbnails)
                {
                    if (string.Equals(thumb.CharacterName, kv.Key, StringComparison.OrdinalIgnoreCase))
                    { eveHwnd = hwnd; break; }
                }
                if (eveHwnd != IntPtr.Zero)
                    CreateSecondaryThumbnail(kv.Key, eveHwnd, kv.Value);
            }

            // Destroy PiPs that have been removed from settings or disabled
            var toRemove = _secondaryThumbnails.Keys
                .Where(k => !s.SecondaryThumbnails.TryGetValue(k, out var ss) || ss.Enabled == 0)
                .ToList();
            foreach (var key in toRemove)
                DestroySecondaryThumbnail(key);

            // Update existing PiP settings
            foreach (var (name, pip) in _secondaryThumbnails)
            {
                pip.SetTopmost(s.ShowThumbnailsAlwaysOnTop);
                pip.IsLocked = s.LockPositions;
                if (s.SecondaryThumbnails.TryGetValue(name, out var ss))
                    pip.SetOpacity((byte)ss.Opacity);
            }

            // Streamer mode (IDEAS #19) switched on or off: every overlay draws its name, label and system again, the
            // second thumbnails' too (ApplySettings above reaches only the first ones, and not their name).
            foreach (var (_, thumb) in _thumbnails) thumb.RefreshStreamerMask();
            foreach (var (_, pip) in _secondaryThumbnails) pip.RefreshStreamerMask();


            // Sync stat overlays with the master ON/OFF toggle. When the user
            // flips the switch in Settings, ReapplySettings is the path that
            // creates / tears down windows so the change is immediate.
            if (!s.StatOverlayEnabled)
            {
                // Master off: destroy every live overlay (positions are persisted
                // by DestroyStatWindowsForCharacter, so toggling back on restores them).
                foreach (var name in _statWindows.Keys.ToList())
                    DestroyStatWindowsForCharacter(name);
            }
            else
            {
                // Master on: create any missing overlays for currently-tracked characters.
                // CreateStatWindowsForCharacter is idempotent (it skips if the window already exists).
                foreach (var (eveHwnd, thumb) in _thumbnails)
                {
                    if (!string.IsNullOrEmpty(thumb.CharacterName))
                        CreateStatWindowsForCharacter(thumb.CharacterName, eveHwnd);
                }
            }

            // Re-apply stat overlay settings (font size, opacity, topmost)
            foreach (var (_, statWin) in _statWindows)
            {
                statWin.SetFontSize(s.StatOverlayFontSize);
                statWin.SetOpacity((byte)s.StatOverlayOpacity);
                statWin.SetBackgroundColor(s.StatOverlayBgColor);
                statWin.SetTextColor(s.StatOverlayTextColor);
                statWin.Topmost = s.ShowThumbnailsAlwaysOnTop;
                statWin.LockPositions = s.LockPositions;
                statWin.RefreshStreamerMask();
            }
        });
    }

    // ── Event Handlers ──────────────────────────────────────────────

    private void OnThumbnailPositionChanged(ThumbnailWindow thumb, int x, int y, int w, int h)
    {
        // Snap on release (before saving, so snapped position is persisted)
        if (_settings.Settings.ThumbnailSnap)
        {
            SnapThumbnail(thumb);
        }

        // Sync global settings layout size if uniform resize is enabled
        if (!_settings.Settings.IndividualThumbnailResize)
        {
            _settings.Settings.ThumbnailStartLocation.Width = (int)thumb.Width;
            _settings.Settings.ThumbnailStartLocation.Height = (int)thumb.Height;
        }

        // Save the (possibly snapped) position
        if (!string.IsNullOrEmpty(thumb.CharacterName))
        {
            _settings.SaveThumbnailPosition(thumb.CharacterName,
                (int)thumb.Left, (int)thumb.Top, (int)thumb.Width, (int)thumb.Height);
        }

        // The gesture is over: a drag-all (IDEAS P96) saves the others' places and clears its start table
        FinishDragAll();
    }

    private void OnSwitchRequested(ThumbnailWindow thumb)
    {
        // Diagnostic for #95 ("clicking a thumbnail brings up the previously active
        // client"). This is the discriminator: if a click logs here with the RIGHT
        // character but the wrong client comes forward, the fault is in activation;
        // if a click logs nothing at all, the click never reached the thumbnail
        // (something is sitting on top of it) and focus simply falls back to the
        // last active client.
        EveMultiPreview.Services.DiagnosticsService.LogWindowHook(
            $"[Thumb:Click] switch requested for '{thumb.CharacterName}' hwnd=0x{thumb.EveHwnd.ToInt64():X} " +
            $"fg=0x{Interop.User32.GetForegroundWindow().ToInt64():X} " +
            // #95: cursor + this thumbnail's rect. If successive clicks come from very
            // different cursor positions but all land on the SAME thumbnail, that window
            // is covering its neighbours (a stuck hover-zoom enlarges it by HoverScale).
            $"cursor={System.Windows.Forms.Cursor.Position.X},{System.Windows.Forms.Cursor.Position.Y} " +
            $"rect={ClickRect(thumb)}");
        ActivateEveWindow(thumb.EveHwnd, thumb.CharacterName);
    }

    /// <summary>The rectangle the click diagnostic of issue #95 writes: where the thumbnail's window stands. The line
    /// exists to show a window that covers its neighbours, and while a thumbnail is enlarged for an alert it does
    /// (T6-m12): so then it is the enlarged window's rectangle, said to be that, with the thumbnail's own place
    /// behind it. Left, Top, Width and Height alone answer with the own place while it is enlarged.</summary>
    public static string ClickRect(ThumbnailWindow thumb)
    {
        var own = FormattableString.Invariant($"{thumb.Left},{thumb.Top},{thumb.Width}x{thumb.Height}");
        if (!thumb.IsAlertEnlarged) return own;
        var real = thumb.Bounds;
        return FormattableString.Invariant($"{real.Left},{real.Top},{real.Width}x{real.Height} (enlarged for an alert; own {own})");
    }

    private void OnMinimizeRequested(ThumbnailWindow thumb)
    {
        Interop.User32.ShowWindowAsync(thumb.EveHwnd, Interop.User32.SW_FORCEMINIMIZE);
    }

    // Right-click → Close client (#115): ends the process at once, no prompt - picking
    // it from a menu is already deliberate, and the point is speed.
    private void OnCloseClientRequested(ThumbnailWindow thumb) => KillClient(thumb.EveHwnd);

    /// <summary>A client's title went from "EVE" to "EVE - Name" (its character reached the game): the name. App raises
    /// "Client ready" (ClientReady). On the window's thread.</summary>
    public event Action<string>? CharacterReady;

    /// <summary>A thumbnail's "Open ‹character›'s settings" (spec D6). App opens settings on that character's page.</summary>
    public event Action<string>? CharacterSettingsRequested;

    /// <summary>MultiPreview is about to end a client itself (Close client, Close all clients): its process id. App tells
    /// ClientExits, so that end raises no "Client closed unexpectedly" (IDEAS #22).</summary>
    public event Action<int>? EndingClient;

    /// <summary>The end announced by <see cref="EndingClient"/> failed (Windows refused it, or the client was already
    /// going): its process id. App takes the note back, so a later crash of that client is still told.</summary>
    public event Action<int>? ClientEndRefused;

    /// <summary>A thumbnail's "End this frozen client…" (IDEAS #22): its client window and its character. App asks first
    /// and ends only that process.</summary>
    public event Action<IntPtr, string>? EndFrozenClientRequested;

    private void OnEndFrozenRequested(ThumbnailWindow thumb) => EndFrozenClientRequested?.Invoke(thumb.EveHwnd, thumb.CharacterName);

    private void OnSettingsRequested(ThumbnailWindow thumb)
    {
        if (ThumbnailMenu.HasCharacter(thumb.CharacterName)) CharacterSettingsRequested?.Invoke(thumb.CharacterName);
    }

    /// <summary>Close-all hotkey (#115): ends every tracked EVE client after ONE
    /// confirmation, so a stray keypress cannot drop the whole fleet.</summary>
    public void CloseAllClients()
    {
        var hwnds = _thumbnails.Keys.ToList();
        if (hwnds.Count == 0) return;
        var answer = System.Windows.MessageBox.Show(
            string.Format(LocalizationService.Str("L.Msg.CloseAllConfirm", "End all {0} EVE clients now?"), hwnds.Count),
            "EVE MultiPreview", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No,
            System.Windows.MessageBoxOptions.DefaultDesktopOnly);   // shows above a full-screen client
        if (answer != MessageBoxResult.Yes) return;
        foreach (var hwnd in hwnds) KillClient(hwnd);
    }

    private void KillClient(IntPtr hwnd)
    {
        // The PID comes from the live window, and only an EVE client is ever ended.
        Interop.User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;
        bool ending = false;   // EndingClient was told: a failed end is taken back below (IDEAS #22)
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!Interop.User32.IsEveProcessName(p.ProcessName)) return;
            EndingClient?.Invoke((int)pid);
            ending = true;
            p.Kill();
            DiagnosticsService.LogWindowHook($"[CloseClient] ended EVE client pid={pid}");
        }
        catch (Exception ex)
        {
            // Already gone, or ACCESS_DENIED when EVE runs elevated and we do not.
            if (ending) ClientEndRefused?.Invoke((int)pid);
            DiagnosticsService.LogWindowHook($"[CloseClient] could not end pid={pid}: {ex.Message}");
        }
    }



    private void OnDragMoveAll(ThumbnailWindow source, double dx, double dy)
    {
        // On first call of drag-all, store all positions
        if (_dragAllStartPositions.Count == 0)
        {
            PushLayoutSnapshot();   // gesture start — snapshot for layout undo
            foreach (var (_, thumb) in _thumbnails)
            {
                _dragAllStartPositions[thumb] = (thumb.Left, thumb.Top);
            }
            _dragAllBaseX = source.Left - dx;
            _dragAllBaseY = source.Top - dy;
            Debug.WriteLine($"[DragAll:Move] 🔧 Started drag-all from ({_dragAllBaseX},{_dragAllBaseY}), {_thumbnails.Count} thumbnails");
        }

        // Move all other thumbnails relative to the same delta
        foreach (var (_, thumb) in _thumbnails)
        {
            if (thumb == source) continue;
            if (_dragAllStartPositions.TryGetValue(thumb, out var startPos))
            {
                thumb.Left = startPos.X + dx;
                thumb.Top = startPos.Y + dy;
            }
        }
    }

    /// <summary>Called when a drag ends (OnThumbnailPositionChanged): a drag-all saves every thumbnail's place and clears its
    /// start positions, so the next drag-all starts from where they are then. Does nothing when no drag-all ran.</summary>
    public void FinishDragAll()
    {
        if (_dragAllStartPositions.Count > 0)
        {
            Debug.WriteLine($"[DragAll:Move] ✅ Finished drag-all, saving {_dragAllStartPositions.Count} positions");
            _dragAllStartPositions.Clear();

            // Save all positions atomically
            foreach (var (_, thumb) in _thumbnails)
            {
                if (!string.IsNullOrEmpty(thumb.CharacterName))
                {
                    _settings.SaveThumbnailPosition(thumb.CharacterName,
                        (int)thumb.Left, (int)thumb.Top,
                        (int)thumb.Width, (int)thumb.Height);
                }
            }
        }
    }

    private void OnResizeAll(ThumbnailWindow source, int newW, int newH)
    {
        // Snapshot once per resize gesture (first event of a burst) for layout undo.
        if ((DateTime.Now - _lastResizeAllSnapshot).TotalMilliseconds > 500)
            PushLayoutSnapshot();
        _lastResizeAllSnapshot = DateTime.Now;

        // Individual-resize mode: the source thumbnail already resized itself during
        // the drag; do NOT propagate the new size to the other thumbnails. The gesture
        // in ThumbnailWindow only checks the Ctrl key, so it fires this handler even in
        // individual mode — the setting is the authority, and it's honoured here.
        if (_settings.Settings.IndividualThumbnailResize)
            return;

        _settings.Settings.ThumbnailStartLocation.Width = newW;
        _settings.Settings.ThumbnailStartLocation.Height = newH;

        foreach (var (_, thumb) in _thumbnails)
        {
            if (thumb == source) continue;
            thumb.Resize(newW, newH);
            if (!string.IsNullOrEmpty(thumb.CharacterName))
            {
                _settings.SaveThumbnailPosition(thumb.CharacterName, (int)thumb.Left, (int)thumb.Top, newW, newH);
            }
        }
    }

    // ── Window Activation (matches AHK ActivateEVEWindow) ───────────

    public void ActivateEveWindow(IntPtr hwnd, string? title = null, bool rapidSwitch = false)
    {
        try
        {
            // Resolve hwnd from character name if needed
            if (hwnd == IntPtr.Zero && !string.IsNullOrEmpty(title))
            {
                foreach (var (eveHwnd, thumb) in _thumbnails)
                {
                    if (string.Equals(thumb.CharacterName, title, StringComparison.OrdinalIgnoreCase))
                    { hwnd = eveHwnd; break; }
                }
            }

            if (hwnd == IntPtr.Zero) return;
            if (Interop.User32.GetForegroundWindow() == hwnd) return;

            // Resolve the character name regardless of which call path we came in
            // through: hotkey passes `title` directly; thumbnail click resolves
            // `hwnd` first and leaves `title` null. We need the name to clear the
            // alert flash for the window the user is intentionally moving focus to.
            // The badge and the unseen alerts are NOT cleared here (IDEAS P75, P78): this
            // is the ASK, and Windows may refuse the switch. The focus sweep clears them
            // when the client really is the foreground window; it runs above the
            // cycle shield, so rapid cycling does not lose the clear.
            string? activatedChar = !string.IsNullOrEmpty(title)
                ? title
                : (_thumbnails.TryGetValue(hwnd, out var hwndThumb) ? hwndThumb.CharacterName : null);
            if (!string.IsNullOrEmpty(activatedChar))
            {
                if (_alertFlashChars.TryRemove(activatedChar, out _))
                    ClearAlertBorderFor(activatedChar); // clear the nested inner border now (#77)
            }

            // Fixed slots own the client's rect: Always-maximize and Track positions
            // would fight the slot, so both are ignored in mode 3.
            bool slotsMode = _settings.Settings.ClientPositionMode == 3;

            // Apply the slot BEFORE restoring a minimized client: for an iconic window
            // it sets the restore rect, so the restore below lands straight in the slot.
            // (After the async restore it could race and re-minimize the window.)
            if (slotsMode)
                ApplyClientSlot(hwnd, activatedChar);

            if (Interop.User32.IsIconic(hwnd))
            {
                if (_settings.Settings.AlwaysMaximize && !slotsMode)
                    Interop.User32.ShowWindowAsync(hwnd, Interop.User32.SW_MAXIMIZE);
                else
                    Interop.User32.ShowWindowAsync(hwnd, Interop.User32.SW_RESTORE);
            }

            EveMultiPreview.Services.DiagnosticsService.LogWindowHook($"[ActivateEveWindow] 🚀 Executing Standard WIN32 Activation for HWND {hwnd} ({title ?? "unknown"})");

            // Re-apply the fixed client position on ACTIVATION, not just at spawn (#99).
            // This is what makes the ISBoxer-style "one main window, thumbnails around
            // it" layout hold: whichever client you switch to lands in the configured
            // slot, even if it was opened before the setting, dragged aside, or
            // restored from another position. No-ops when the mode is Off (the
            // default), and Track client positions still wins — that setting exists
            // precisely to give each character its OWN remembered spot.
            if (!slotsMode && !_settings.Settings.TrackClientPositions)
                ApplyFixedClientPosition(hwnd);

            // Swap the cover-taskbar band here too (#100). The focus poll alone is too
            // late for hotkey cycling — it skips the 500ms after a cycle, so the old
            // client stayed above the new one. Runs before activation so the incoming
            // client is already in the right band when it comes forward.
            ApplyClientTaskbarCover(hwnd);

            Interop.User32.ActivateWindow(hwnd);

            // Paint the highlight NOW, for every way of switching (#112). Only group
            // cycling did, through its own callback; a character's own hotkey and a
            // thumbnail click waited for the focus sweep, which stands down for 500ms
            // after any cycle, so their highlight visibly lagged. The shield stamp keeps
            // that sweep from repainting the OLD client while the OS finishes the switch,
            // and clearing its marker makes it re-check reality once the shield lifts
            // (correcting the highlight if the switch was refused, and applying CPU
            // affinity, which follows the settled client rather than every cycle step).
            _lastActiveEveHwnd = hwnd;
            NoteClientInFront(hwnd);
            _lastCycleTime = DateTime.UtcNow;
            _lastSweptHwnd = IntPtr.Zero;
            ApplyActiveBorders(hwnd);

            // Defer WPF z-order operations to prevent dispatch pumping from eating the OS input state interrupt
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Bring thumbnails + overlays + stat windows to front BEFORE activating EVE window.
                // This prevents them from flashing under the EVE window temporarily.
                foreach (var (_, t) in _thumbnails)
                    t.BringToFront();
                foreach (var (_, p) in _secondaryThumbnails)
                    p.BringToFront();
                foreach (var (_, sw) in _statWindows)
                    sw.BringToFront();
                RaiseEnlargedAgain();
            }, System.Windows.Threading.DispatcherPriority.Background);

            if (_settings.Settings.AlwaysMaximize && !slotsMode && !Interop.User32.IsZoomed(hwnd))
                Interop.User32.ShowWindowAsync(hwnd, Interop.User32.SW_MAXIMIZE);

            if (_settings.Settings.MinimizeInactiveClients)
            {
                // Fire minimize cycle asynchronously so we don't block the hotkey rapid-fire thread
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.Settings.MinimizeDelay) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        MinimizeInactiveClients(hwnd);
                    };
                    timer.Start();
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Activation:Error] ❌ ActivateEveWindow error: {ex.Message}");
        }
    }

    private void MinimizeInactiveClients(IntPtr activeHwnd)
    {
        var dontMinimize = _settings.CurrentProfile.DontMinimizeClients;

        foreach (var (eveHwnd, thumb) in _thumbnails)
        {
            if (eveHwnd == activeHwnd) continue;
            if (string.IsNullOrEmpty(thumb.CharacterName) || thumb.CharacterName == "EVE") continue;

            // Check don't-minimize list
            if (dontMinimize.Any(n => n.Equals(thumb.CharacterName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Don't minimize if it's the active window
            if (eveHwnd == Interop.User32.GetForegroundWindow()) continue;

            Interop.User32.ShowWindowAsync(eveHwnd, Interop.User32.SW_FORCEMINIMIZE);
        }
    }

    // ── Active Border / Focus Tracking ───────────────────────────────

    /// <summary>
    /// WinEventHook callback bridge: the hook delivers foreground / minimize
    /// transitions on the UI thread; we marshal a follow-up UpdateActiveBorders
    /// at Background priority so the hook callback stays cheap and the heavy
    /// sweep coalesces with whatever the dispatcher is already doing.
    /// </summary>
    private void OnForegroundOrMinimizeEvent(IntPtr hwnd)
    {
        // Immediately snapshot non-EVE focus so tracking state is correct even
        // if the queued UpdateActiveBorders runs too late to observe the
        // intermediate state (e.g. rapid alt+tab: switcher + EVE both queue
        // before either Background-priority invoke executes, GetForegroundWindow
        // already returns EVE, the transition is missed, BringToFront never fires).
        if (hwnd != IntPtr.Zero)
        {
            string? proc = null;
            try { proc = Interop.User32.GetProcessName(hwnd); } catch { }
            if (!Interop.User32.IsEveOrAppProcess(proc))
            {
                // Mute-all-when-inactive HERE, before we zero _lastZOrderHwnd below —
                // this synchronous clear runs ahead of the Background-priority
                // UpdateActiveBorders sweep (see comment above), so its own
                // "_lastZOrderHwnd still holds the previous client" guard always saw
                // Zero and never fired.
                var s = _settings.Settings;
                if (_lastZOrderHwnd != IntPtr.Zero && s.AutoSoloClientAudio && s.MuteAllClientAudioWhenNoneActive)
                    MuteAllClientAudio();

                _lastEveFocused = false;
                _lastZOrderHwnd = IntPtr.Zero;
            }
            else if (Interop.User32.IsEveProcessName(proc))
            {
                // IDEAS P80 (6): an EVE client took the foreground: a thumbnail's open right-click menu goes. Only for an
                // EVE window, never for MultiPreview's own (the menu is one of them, and would close itself).
                foreach (var (_, t) in _thumbnails) t.CloseMenu();
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(new Action(UpdateActiveBorders), DispatcherPriority.Background);
    }

    private void UpdateActiveBorders()
    {
        // Skip all heavy Win32/DWM work during drag — prevents contention
        if (_thumbnails.Values.Any(t => t.IsDragging))
            return;

        // IDEAS #13 (FX-m1, FX-m2): the tray's or a toast's menu that is counted as open while it is not on the screen
        // is counted closed, as a thumbnail's is below. Before the tray's pause, which a lost "closed" would hold too.
        ThumbnailWindow.AppMenu.EnsureStatesValid();

        // IDEAS P98 (7): and the tray's pause below is healed the same way: a flag left up while its menu is not on the screen.
        _trayMenuOpen = TrayMenuStillOpen(_trayMenuOpen, TrayMenuShown);

        // Tray menu open: leave visibility and z-order alone (see SetTrayMenuOpen).
        if (_trayMenuOpen)
            return;

        // #95 watchdog: a thumbnail left stuck in its enlarged hover state covers its
        // neighbours and eats their clicks. Cheap per-tick correction.
        // IDEAS #13 (RD-m4): a thumbnail's menu that is counted as open while it is not on the screen is counted
        // closed. While the count is up no thumbnail is enlarged, so it must not stay up for a "closed" that was lost.
        // IDEAS #13 watchdog (T6-C1): a thumbnail left enlarged for an alert that nothing will ever tell to go back
        // (its character logged off the window, or its "go back" failed) is put back here.
        // One walk over the thumbnails for all three (FY-m4): nothing here grows with the clients beyond that walk.
        foreach (var (_, t) in _thumbnails)
        {
            t.EnsureHoverStateValid();
            t.EnsureMenuStateValid();
            PutBackUnheld(Enlarge, t, ReportEnlarge);
        }

        var fgHwnd = Interop.User32.GetForegroundWindow();
        var s = _settings.Settings;

        // Check if EVE or our app is foreground
        string? fgProc = null;
        try { fgProc = Interop.User32.GetProcessName(fgHwnd); } catch { }
        bool eveFocused = Interop.User32.IsEveOrAppProcess(fgProc);

        // Per-HWND iconic state — drives the live-DWM ↔ frozen-frame swap.
        // When a source EVE window becomes iconic, DWM composes nothing into
        // its thumbnail, so we switch to painting the last PrintWindow snapshot.
        foreach (var (eveHwnd, thumb) in _thumbnails)
        {
            bool isIconic = Interop.User32.IsIconic(eveHwnd);
            bool wasIconic = _iconicHwnds.ContainsKey(eveHwnd);
            if (isIconic == wasIconic) continue;

            if (isIconic)
            {
                _iconicHwnds.TryAdd(eveHwnd, 0);
                var frame = _frozenFrames?.GetLastFrame(eveHwnd);
                thumb.SetFrozenFrame(frame);
                thumb.HideDwmOnly(); // DWM transparent so OnPaint's frozen bitmap shows.
            }
            else
            {
                _iconicHwnds.TryRemove(eveHwnd, out _);
                thumb.ClearFrozenFrame();
                if (!_thumbnailsHidden && !_primaryHidden)
                    thumb.ShowDwmOnly(); // Restore live DWM preview.
            }
        }

        // ── GPU-saving modes (StaticThumbnails / SuspendThumbnailsWhenBackground) ──
        // Replace live DWM compositing with the cached PrintWindow snapshot
        // for some or all thumbnails. The frozen-frame paint kicks in
        // automatically once DWM is hidden AND a frame is set on the thumb
        // (see ThumbnailWindow.OnPaint). Gate the capture cadence too: a
        // 1 s refresh is the user-visible "static thumbnail" rate, while
        // 5 s is the dormant safety cadence for the iconic-fallback path.
        bool staticAll = s.StaticThumbnails;
        bool suspendBg = s.SuspendThumbnailsWhenBackground && !eveFocused;
        bool gpuFreeze = staticAll || suspendBg;
        _frozenFrames?.SetCaptureInterval(staticAll
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(5));
        // Only run the periodic PrintWindow capture when a GPU-saving mode actually
        // needs continuous frames. Otherwise the large-bitmap churn (esp. many 4K
        // clients) drives a periodic GC pause that micro-stutters the mouse. Normal
        // minimize→frozen-frame is still covered by the eager MINIMIZESTART hook.
        _frozenFrames?.SetPeriodicCaptureEnabled(gpuFreeze);
        if (gpuFreeze != _lastGpuFreezeState)
        {
            foreach (var (eveHwnd, thumb) in _thumbnails)
            {
                if (_iconicHwnds.ContainsKey(eveHwnd)) continue;
                if (gpuFreeze)
                {
                    var frame = _frozenFrames?.GetLastFrame(eveHwnd);
                    if (frame != null) thumb.SetFrozenFrame(frame);
                    thumb.HideDwmOnly();
                }
                else
                {
                    thumb.ClearFrozenFrame();
                    if (!_thumbnailsHidden && !_primaryHidden)
                        thumb.ShowDwmOnly();
                }
            }
            _lastGpuFreezeState = gpuFreeze;
        }
        else if (gpuFreeze)
        {
            // Refresh frame on every tick while frozen — picks up new
            // snapshots produced by FrozenFrameService at its current cadence.
            foreach (var (eveHwnd, thumb) in _thumbnails)
            {
                if (_iconicHwnds.ContainsKey(eveHwnd)) continue;
                var frame = _frozenFrames?.GetLastFrame(eveHwnd);
                if (frame != null) thumb.SetFrozenFrame(frame);
            }
        }

        // Visibility gate for primary thumbnails under HideThumbnailsOnLostFocus
        // ("Hide When Alt-Tabbed"). Thumbnails are visible only while EVE or this
        // app holds the foreground (or Settings is open); alt-tabbing to any other
        // application fully hides them and refocusing EVE restores them. Per-character
        // Visibility-tab hides are respected on restore. Stat overlays / PiPs follow
        // the focus-aware block below.
        bool anyEveExists = _thumbnails.Count > 0;
        bool appVisibleContext = eveFocused || _settingsOpen;

        if (s.HideThumbnailsOnLostFocus)
        {
            foreach (var (eveHwnd, thumb) in _thumbnails)
            {
                bool charHidden = !string.IsNullOrEmpty(thumb.CharacterName)
                    && s.ThumbnailVisibility.TryGetValue(thumb.CharacterName, out var vf) && vf != 0;

                // The active-thumbnail block below owns the focused client's thumbnail.
                // Without this the two writers fight: switching INTO a client shows its
                // thumbnail here (it is genuinely hidden at that moment) and the active
                // block hides it again in the same sweep — a one-frame flash (#102).
                bool willHideAsActive = s.HideActiveThumbnail && eveHwnd == fgHwnd;

                if (!appVisibleContext)
                {
                    if (!_thumbnailsHidden)
                        thumb.HideWithOverlay();
                }
                else if (!_thumbnailsHidden && !_primaryHidden && !charHidden && !willHideAsActive)
                {
                    // State-guarded: unguarded this re-showed every thumbnail on every
                    // sweep, which is what made the active thumbnail flicker (#102).
                    if (!thumb.IsVisible) thumb.ShowWithOverlay();
                }
            }
            _primaryHiddenByFocus = !appVisibleContext && !_thumbnailsHidden;

            // Stat overlays and PiPs stay visible as long as any EVE client is
            // tracked — clicking off EVE shouldn't make them disappear, just
            // stop being on top. The focus-aware Topmost flip below handles the
            // "always on top" complaint from issue #30 without removing the
            // overlay entirely. HideStatsOnLostFocus is the legacy opt-in for
            // users who really do want them to disappear on focus loss.
            bool statsVisibleByFocus = !s.HideStatsOnLostFocus
                || eveFocused || _settingsOpen;
            var targetVisibility = anyEveExists && statsVisibleByFocus
                                   && !_thumbnailsHidden && !_primaryHidden
                ? Visibility.Visible
                : Visibility.Collapsed;
            foreach (var (_, sw) in _statWindows)
            {
                if (sw.Visibility != targetVisibility)
                    sw.Visibility = targetVisibility;
            }
            foreach (var (_, pip) in _secondaryThumbnails)
            {
                if (pip.Visibility != targetVisibility)
                    pip.Visibility = targetVisibility;
            }
        }
        else if (_primaryHiddenByFocus)
        {
            // The feature was switched off while thumbnails were focus-hidden —
            // restore them so they don't stay invisible. Per-character Visibility
            // hides and the manager-level hide toggles still take precedence.
            _primaryHiddenByFocus = false;
            foreach (var (_, thumb) in _thumbnails)
            {
                bool charHidden = !string.IsNullOrEmpty(thumb.CharacterName)
                    && s.ThumbnailVisibility.TryGetValue(thumb.CharacterName, out var vf) && vf != 0;
                if (!_thumbnailsHidden && !_primaryHidden && !charHidden)
                    thumb.ShowWithOverlay();
            }
        }

        // Sync text overlay positions (position-only, z-order handled by BringToFront)
        foreach (var (_, thumb) in _thumbnails)
        {
            thumb.SyncOverlayPosition();
        }
        foreach (var (_, pip) in _secondaryThumbnails)
        {
            pip.SyncOverlayPosition();
        }

        // Focus-aware Topmost — must happen BEFORE BringToFront so that BringToFront
        // uses the correct z-order band (HWND_TOPMOST vs HWND_TOP). Thumbnails (and
        // their text overlays), stats and PiPs are TOPMOST while EVE is focused so
        // EVE's DirectX surface can't push them under, and drop to NOTOPMOST on focus
        // loss — above EVE while you play, behind your other windows when you click
        // off (#30) — unless ShowThumbnailsAlwaysOnTop keeps them raised permanently.
        //
        // This is IDEMPOTENT (converge to the desired state every sweep), NOT gated on
        // an eveFocused transition: OnForegroundOrMinimizeEvent pre-sets
        // _lastEveFocused=false synchronously to fix the focus-GAIN race, which
        // consumes the true→false transition — so a transition-gated flip would never
        // DROP topmost on focus loss, leaving thumbnails AND their overlays stuck above
        // other windows. The per-window `Topmost != desired` guard keeps it a no-op
        // except on an actual change.
        // Topmost ONLY when the user opts in via Always-On-Top — NOT merely because
        // an EVE client is focused. The earlier `eveFocused ||` forced thumbnails
        // into the topmost band whenever any client had focus, so with Always-On-Top
        // OFF they covered every other window you opened while playing and wouldn't
        // drop back behind it. Now (OFF) thumbnails live in the normal z-band: the
        // per-focus BringToFront + client-switch re-raise still lift them above the
        // EVE client, but any window you focus stays above them. (ON = always topmost.)
        // KeepThumbnailsAboveClients (opt-in) makes thumbnails topmost ONLY while an
        // EVE client (or this app) is foreground, then drops them the instant you
        // focus a non-EVE app — so they're reliably above the clients while you play
        // (no activation-race "lost behind") but never sit over Discord/your browser
        // once you tab there. Topmost is deliberate: a non-topmost re-raise loses the
        // race when an EVE client re-activates and gets stuck behind it (confirmed by
        // LittlePhish). Always-On-Top keeps them topmost permanently (over everything).
        bool desiredTopmost = s.ShowThumbnailsAlwaysOnTop
                              || (s.KeepThumbnailsAboveClients && eveFocused);
        // On a CHANGE of the desired state, re-assert UNCONDITIONALLY on every window
        // (don't trust the cached _isTopmost guard): a freshly-launched thumbnail —
        // or its separate text-overlay window — can end up OS-topmost while the cache
        // reads not-topmost, so a purely cache-guarded flip would skip the drop and
        // leave it stuck above other windows until the user toggled Always-On-Top
        // (which resyncs via the same unconditional SetTopmost). Tracked via its own
        // field so the synchronous _lastEveFocused snapshot can't defeat it. When the
        // desired state is unchanged, fall back to the per-window guard so newly
        // created thumbnails still converge without re-asserting everything each sweep.
        bool topmostChanged = _lastAppliedTopmost != desiredTopmost;
        _lastAppliedTopmost = desiredTopmost;
        foreach (var (_, thumb) in _thumbnails)
        {
            if (topmostChanged || thumb.Topmost != desiredTopmost) thumb.SetTopmost(desiredTopmost);
        }
        foreach (var (_, sw) in _statWindows)
        {
            if (topmostChanged || sw.Topmost != desiredTopmost) sw.Topmost = desiredTopmost;
        }
        foreach (var (_, pip) in _secondaryThumbnails)
        {
            if (topmostChanged || pip.Topmost != desiredTopmost) pip.SetTopmost(desiredTopmost);
        }

        // One-time BringToFront when EVE gains focus (false→true transition).
        // Runs AFTER the SetTopmost flip above so BringToFront uses HWND_TOPMOST.
        if (eveFocused && !_lastEveFocused && !_suppressTopmost)
        {
            foreach (var (_, thumb) in _thumbnails)
                thumb.BringToFront();
            foreach (var (_, pip) in _secondaryThumbnails)
                pip.BringToFront();

            // Stat overlays also need Win32 SetWindowPos — WPF Topmost alone
            // is insufficient against EVE's DirectX surface
            foreach (var (_, sw) in _statWindows)
                sw.BringToFront();
            RaiseEnlargedAgain();
        }
        _lastEveFocused = eveFocused;

        // Unseen alerts that nobody looked at for AlertAttention.MaxAge go now, with their badge and the tray's red
        // icon, whether or not anything else looks at the list (IDEAS P78). Free while nothing is unseen.
        Attention.DropExpired();

        // Clear the alert badge of whichever EVE window is currently foreground.
        // Hoisted above the cycle-shield / no-change early-returns below because
        // badge state is per-character and has nothing to do with z-order
        // settling — without this, rapid cycling kept the badge visible on the
        // newly focused client until the next un-shielded sweep.
        foreach (var (eveHwnd, thumb) in _thumbnails)
        {
            if (eveHwnd != fgHwnd) continue;
            var charName = thumb.CharacterName;
            if (!string.IsNullOrEmpty(charName))
            {
                ClearAlertBadge(charName);
                Attention.Seen(charName);
            }
        }

        // ── Hide Active Thumbnail (issues #43 / #44) ──
        // Fully hide (window + text overlay) the thumbnail of whichever EVE
        // client is foreground — the user is already looking at the real
        // client, so its thumbnail is redundant.
        //   #43: HideWithOverlay removes the whole thumbnail. The previous
        //        implementation used HideDwmOnly, which only blanked the live
        //        preview and left a grey box with the name / system / border
        //        still showing — the user expected it gone entirely.
        //   #44: Hoisted above the cycle-shield early-return so it tracks
        //        cycle-hotkey focus changes immediately. Previously this lived
        //        in the shielded per-thumb loop, so during rapid cycling the
        //        hidden state lagged behind the active border.
        // Per-character Visibility-tab hides and the global hide-all flags
        // take precedence — this feature never un-hides a thumb the user
        // deliberately hid through those.
        // !_primaryHiddenByFocus (#102): while alt-tabbed the focus block above has just
        // hidden every thumbnail. Without this the foreground window matches no client,
        // so every thumbnail fell into the else branch below and was re-shown — undoing
        // that hide on every sweep, which read as constant flicker while alt-tabbed.
        bool hideActiveNow = s.HideActiveThumbnail && !_thumbnailsHidden && !_primaryHidden
                             && !_primaryHiddenByFocus;
        if (hideActiveNow)
        {
            foreach (var (eveHwnd, thumb) in _thumbnails)
            {
                bool userHidChar = s.ThumbnailVisibility.TryGetValue(thumb.CharacterName, out var vis) && vis != 0;
                if (userHidChar) continue;
                if (eveHwnd == fgHwnd)
                {
                    if (thumb.IsVisible) thumb.HideWithOverlay();
                }
                else
                {
                    if (!thumb.IsVisible) thumb.ShowWithOverlay();
                }
            }
        }
        else if (_lastHideActiveThumbnailState)
        {
            // Feature (or its enabling context) just turned off — restore
            // every thumbnail it may have hidden, minus explicit per-char
            // Visibility-tab hides and the global hide-all states.
            // !_primaryHiddenByFocus (#102): hunk 3 makes hideActiveNow flip to false on
            // alt-tab, which fires this restore once and flashes the thumbnails back on
            // the very tick the focus block hid them. Without it hunks 1-3 still leave a
            // single-frame flash on every alt-tab.
            if (!_thumbnailsHidden && !_primaryHidden && !_primaryHiddenByFocus)
            {
                foreach (var (_, thumb) in _thumbnails)
                {
                    bool userHidChar = s.ThumbnailVisibility.TryGetValue(thumb.CharacterName, out var vis) && vis != 0;
                    if (!userHidChar && !thumb.IsVisible) thumb.ShowWithOverlay();
                }
            }
        }
        _lastHideActiveThumbnailState = hideActiveNow;

        // Re-assert thumbnail z-order on ANY foreground change to an EVE client —
        // BEFORE the cycle-shield / fg-unchanged early-returns below, which would
        // otherwise skip it. Switching between EVE clients keeps eveFocused true, so
        // the false→true BringToFront earlier never fires; without this re-raise the
        // thumbnails get stuck behind the newly-focused client until you leave EVE
        // entirely. Deliberately NOT gated on Always-On-Top (matching that earlier
        // raise) so it also helps users who keep thumbnails non-topmost. This only
        // touches z-order — it never updates _lastActiveEveHwnd, so the fast-cycle
        // tracker and its shield below are unaffected.
        bool fgIsTrackedClient = _thumbnails.ContainsKey(fgHwnd);
        // KeepThumbnailsAboveClients (opt-in) re-raises on EVERY sweep a client is
        // foreground — not just when the foreground client CHANGES — so a thumbnail
        // that got covered after the last switch (including returning to the SAME
        // client, which the change-gate skips) is lifted back above the EVE clients
        // within a sweep (~250ms). This reinforces the topmost flip above (BringToFront
        // re-asserts HWND_TOPMOST) so a re-activated client can't leave it behind.
        // Default OFF keeps the original change-gated single re-raise for everyone else.
        bool clientFocusChanged = fgHwnd != _lastZOrderHwnd;
        if (eveFocused && fgIsTrackedClient && !_suppressTopmost
            && (clientFocusChanged || s.KeepThumbnailsAboveClients))
        {
            _lastZOrderHwnd = fgHwnd;

            // Audio on an actual client switch: auto-solo (if on) + re-apply this
            // client's saved per-client volume so Settings-slider levels persist.
            if (clientFocusChanged)
            {
                if (s.AutoSoloClientAudio) ApplyAudioSolo(fgHwnd);
                ReapplyClientVolume(fgHwnd);
            }
            foreach (var (_, thumb) in _thumbnails)
                thumb.BringToFront();
            foreach (var (_, pip) in _secondaryThumbnails)
                pip.BringToFront();
            foreach (var (_, sw) in _statWindows)
                sw.BringToFront();
            RaiseEnlargedAgain();
        }
        else if (!fgIsTrackedClient)
        {
            // Foreground is the MultiPreview app/settings or a non-EVE window — not an
            // EVE client. Clear the tracker so RETURNING to a client (even the same
            // one we last raised for) re-raises. Without this, app→same-client and
            // settings→same-client left the thumbnails stuck behind (LittlePhish).
            //
            // Fires exactly once on the transition (guarded by _lastZOrderHwnd still
            // holding the previously-active client) — otherwise auto-solo leaves that
            // client as the sole unmuted one for as long as focus stays off EVE.
            // Only reachable here for an app/Settings-window switch: a genuine
            // non-EVE switch is caught synchronously in OnForegroundOrMinimizeEvent
            // below, which clears _lastZOrderHwnd before this sweep ever runs.
            if (_lastZOrderHwnd != IntPtr.Zero && s.AutoSoloClientAudio && s.MuteAllClientAudioWhenNoneActive)
                MuteAllClientAudio();
            _lastZOrderHwnd = IntPtr.Zero;
        }

        // Shield the fast-cycle tracker and visual borders from asynchronous OS focus lag.
        // If we recently cycled, the native background window switch might still be resolving.
        // Letting the slow OS tracker overwrite our state here will illegally rewind the cycle sequence.
        if ((DateTime.UtcNow - _lastCycleTime).TotalMilliseconds < 500)
            return;

        // Its own marker, not _lastActiveEveHwnd (#112): a switch WE make sets that
        // tracker up front, because the cycle order reads it, so this check used to
        // conclude nothing had changed - global cycling never repainted the highlight
        // and CPU affinity never followed a cycle. ActivateEveWindow clears this marker
        // so the sweep re-checks reality once the shield above lifts.
        if (fgHwnd == _lastSweptHwnd) return;
        _lastSweptHwnd = fgHwnd;
        _lastActiveEveHwnd = fgHwnd;
        NoteClientInFront(fgHwnd);

        // Raise the newly-focused client over the taskbar / drop the previous one (#99).
        // Runs only on an actual focus CHANGE, so this is not a per-tick SetWindowPos storm.
        ApplyClientTaskbarCover(fgHwnd);

        // Flash toggle moved to dedicated FlashAlertTick

        ApplyActiveBorders(fgHwnd);

        // ── CPU Affinity & Priority Management ──
        ManageCpuAffinity(fgHwnd, s);
    }

    /// <summary>Paint the resting highlight: <paramref name="activeHwnd"/>'s thumbnail
    /// active, every other one inactive, plus each one's nested alert border (#71).
    /// Called by the focus sweep and, synchronously, by every switch we make.</summary>
    private void ApplyActiveBorders(IntPtr activeHwnd)
    {
        var s = _settings.Settings;
        foreach (var (eveHwnd, thumb) in _thumbnails)
        {
            bool isActive = eveHwnd == activeHwnd;
            string charName = thumb.CharacterName;

            // (Hide Active Thumbnail moved to a hoisted block above the
            //  cycle-shield — see issues #43 / #44.)

            // Main border = resting active/inactive highlight, ALWAYS. The alert
            // pulse lives on a separate nested inner border (#71) so a flashing
            // client never loses its selection highlight.
            if (isActive)
            {
                // Active border — honor ShowClientHighlightBorder. When disabled,
                // the active thumbnail gets no highlight at all (thickness 0) even
                // while focused.
                if (s.ShowClientHighlightBorder)
                {
                    var color = GetBorderColor(charName, true);
                    thumb.SetBorder(color, s.ClientHighlightBorderThickness);
                }
                else
                {
                    thumb.SetBorder(Color.FromArgb(0, 0, 0, 0), 0);
                }
            }
            else
            {
                // Show inactive border only if a color is configured
                var color = GetBorderColor(charName, false);
                int thickness = ShouldShowInactiveBorder(charName) ? s.InactiveClientBorderThickness : 0;
                thumb.SetBorder(color, thickness);
            }

            // Nested inner alert border (independent of the main border above).
            ApplyAlertBorder(thumb, _alertFlashChars.TryGetValue(charName, out var fi) ? fi : null);
        }
    }

    private bool _lastManageAffinityState = false;

    private void ManageCpuAffinity(IntPtr activeHwnd, AppSettings s)
    {
        // Detect on→off transition and restore every tracked EVE process to
        // Normal priority + full-core affinity once, otherwise the user's
        // clients stay stuck at Idle / restricted cores after they uncheck the
        // master toggle. Then bail. The tracker persists across calls so we
        // only fire the reset once per disable, not on every tick.
        if (!s.ManageAffinity)
        {
            if (_lastManageAffinityState)
            {
                ResetAllEveAffinity();
                _lastManageAffinityState = false;
            }
            return;
        }
        _lastManageAffinityState = true;

        int totalCores = Environment.ProcessorCount;
        int eCoreStart = totalCores / 2; // Bottom half of logical processors

        // Collect all running EVE processes that we are tracking
        var activeEves = new List<(Process Proc, string CharName, bool IsActive)>();
        foreach (var (hwnd, thumb) in _thumbnails)
        {
            try
            {
                int pid = thumb.GetProcessId();
                if (pid > 0)
                {
                    var p = Process.GetProcessById(pid);
                    activeEves.Add((p, thumb.CharacterName, hwnd == activeHwnd));
                }
            }
            catch { /* Process might have just exited */ }
        }

        int eCoreIndex = eCoreStart;

        foreach (var eve in activeEves)
        {
            try
            {
                if (eve.IsActive)
                {
                    // Active client: Normal priority, all cores
                    if (eve.Proc.PriorityClass != ProcessPriorityClass.Normal)
                        eve.Proc.PriorityClass = ProcessPriorityClass.Normal;

                    long allCoresMask = (1L << totalCores) - 1;
                    if ((long)eve.Proc.ProcessorAffinity != allCoresMask)
                        eve.Proc.ProcessorAffinity = (IntPtr)allCoresMask;
                }
                else
                {
                    // Inactive client: Idle priority
                    if (eve.Proc.PriorityClass != ProcessPriorityClass.Idle)
                        eve.Proc.PriorityClass = ProcessPriorityClass.Idle;

                    // Inactive Core Assignment
                    long affinityMask = 0;

                    if (s.PerClientCores.TryGetValue(eve.CharName, out int overrideCore))
                    {
                        // Explicit user override
                        affinityMask = 1L << overrideCore;
                    }

                    else if (s.AutoBalanceCores)
                    {
                        // Auto-balance round robin on E-cores
                        affinityMask = 1L << eCoreIndex;
                        eCoreIndex++;
                        if (eCoreIndex >= totalCores)
                            eCoreIndex = eCoreStart;
                    }

                    // If zero, it means auto-balance is off and no override exists. Don't restrict cores.
                    if (affinityMask != 0)
                    {
                        if ((long)eve.Proc.ProcessorAffinity != affinityMask)
                            eve.Proc.ProcessorAffinity = (IntPtr)affinityMask;
                    }
                    else
                    {
                        // Restore to all cores but keep 'Idle' priority
                        long allCoresMask = (1L << totalCores) - 1;
                        if ((long)eve.Proc.ProcessorAffinity != allCoresMask)
                            eve.Proc.ProcessorAffinity = (IntPtr)allCoresMask;
                    }
                }
            }
            catch { /* Ignore Access Denied or Exited processes */ }
            finally
            {
                eve.Proc.Dispose();
            }
        }
    }

    /// <summary>Restore every tracked EVE process to Normal priority and the
    /// full-cores affinity mask. Called once when the user disables
    /// ManageAffinity so previously-throttled inactive clients aren't left
    /// stuck on Idle priority + a single E-Core after the master toggle goes
    /// off.</summary>
    private void ResetAllEveAffinity()
    {
        int totalCores = Environment.ProcessorCount;
        long allCoresMask = (1L << totalCores) - 1;

        foreach (var (_, thumb) in _thumbnails)
        {
            int pid;
            try { pid = thumb.GetProcessId(); }
            catch { continue; }
            if (pid <= 0) continue;

            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.PriorityClass != ProcessPriorityClass.Normal)
                    p.PriorityClass = ProcessPriorityClass.Normal;
                if ((long)p.ProcessorAffinity != allCoresMask)
                    p.ProcessorAffinity = (IntPtr)allCoresMask;
                Debug.WriteLine($"[Affinity:Reset] '{thumb.CharacterName}' restored to Normal + all cores");
            }
            catch { /* process exited / access denied */ }
        }
    }

    /// <summary>Check whether an inactive border should be shown for this character.
    /// Returns false when InactiveClientBorderColor is empty and no custom/group/automatic color applies.
    /// The rule is BorderColorRule's (pure, tested).</summary>
    private bool ShouldShowInactiveBorder(string charName)
    {
        var s = _settings.Settings;
        return BorderColorRule.ShowsInactiveBorder(s, charName);
    }

    /// <summary>Get the correct border color for a character (custom → group → automatic → default), by
    /// BorderColorRule. The automatic colour (IDEAS P19) is chosen among the characters that have a thumbnail.</summary>
    private Color GetBorderColor(string charName, bool isActive)
    {
        var s = _settings.Settings;
        return ParseColor(BorderColorRule.Resolve(s, charName, isActive, RunningCharacters));
    }

    /// <summary>The characters that have a thumbnail now (a client at the login screen has none): the roster the
    /// automatic border colours are kept apart within.</summary>
    private IReadOnlyCollection<string> RunningCharacters() =>
        _thumbnails.Values.Select(t => t.CharacterName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

    /// <summary>Resolve per-event alert flash color: AlertColors[eventType] → SeverityColors[severity] → default red.
    /// The user's global AlertOpacityPercent (0-100, default 100) is applied to
    /// the resolved RGB color as the alpha channel — ThumbnailWindow.OnPaint
    /// honors that alpha when drawing the flash border.</summary>
    private Color ResolveAlertFlashColor(string eventType, string severity)
    {
        var s = _settings.Settings;

        // Per-event override, severity, AlertFlashColor, red: one rule shared with the alert hub's toasts.
        var baseColor = ParseColor(AlertColours.Resolve(s, eventType, severity));

        int opacity = Math.Clamp(s.AlertOpacityPercent, 0, 100);
        byte alpha = (byte)(opacity * 255 / 100);
        return Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>Show a brief tooltip near the cursor for user feedback on toggle actions (matches AHK ToolTip).</summary>
    private System.Windows.Window? _tooltipWindow;
    private System.Windows.Threading.DispatcherTimer? _tooltipTimer;

    public void ShowTooltipFeedback(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                // Close any existing tooltip
                _tooltipWindow?.Close();
                _tooltipTimer?.Stop();

                // Get cursor position (physical pixels) and convert to DIPs for WPF Left/Top
                var cursorPos = System.Windows.Forms.Cursor.Position;
                double dpi = Interop.DpiHelper.GetScaleFactorForPoint(cursorPos.X, cursorPos.Y);

                // Create a small borderless tooltip window near the cursor (AHK ToolTip style)
                _tooltipWindow = new System.Windows.Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(230, 40, 40, 40)),
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false, // Passive overlay: showing it must never take focus from the EVE client (#110).
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Left = Interop.DpiHelper.PhysicalToDip(cursorPos.X, dpi) + 16,
                    Top = Interop.DpiHelper.PhysicalToDip(cursorPos.Y, dpi) + 16,
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = message,
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 12,
                        Margin = new Thickness(8, 4, 8, 4)
                    }
                };
                _tooltipWindow.Show();

                // Auto-dismiss after 1500ms (AHK: SetTimer(() => ToolTip(), -1500))
                _tooltipTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                _tooltipTimer.Tick += (_, _) =>
                {
                    _tooltipTimer.Stop();
                    _tooltipWindow?.Close();
                    _tooltipWindow = null;
                };
                _tooltipTimer.Start();
            }
            catch
            {
                Debug.WriteLine($"[Tooltip] {message}");
            }
        });
    }

    /// <summary>Show tooltip feedback for suspend toggle state (called from App.xaml.cs).</summary>
    public void ShowSuspendTooltip(bool isSuspended)
    {
        ShowTooltipFeedback(isSuspended
            ? LocalizationService.Str("L.Msg.HotkeysSuspended", "Hotkeys: SUSPENDED")
            : LocalizationService.Str("L.Msg.HotkeysActive", "Hotkeys: ACTIVE"));
    }

    // ── Indicator legend (IDEAS #37) ─────────────────────────────────
    // Alt held over a thumbnail says why it looks the way it does. The window watches the pointer and Alt
    // (ThumbnailWindow.LegendWanted); the lines are IndicatorLegend's; this reads the state and draws the popup.

    /// <summary>The alert flash a character has now: false when it has none. The legend reads its event and start.</summary>
    public bool TryGetFlash(string character, out AlertFlashInfo? info)
    {
        info = null;
        return !string.IsNullOrEmpty(character) && _alertFlashChars.TryGetValue(character, out info);
    }

    /// <summary>The alert mute a character has now (null when it has none, or it ran out): until it is cleared, or how long is
    /// left on <see cref="MuteClock"/>. Same rule as <see cref="IsCharacterAlertMuted"/>.</summary>
    public (bool UntilCleared, TimeSpan Left)? MuteOf(string characterName)
    {
        if (!IsCharacterAlertMuted(characterName) || !_alertMutedChars.TryGetValue(characterName, out var mute)) return null;
        return mute.Until == DateTime.MaxValue ? (true, TimeSpan.Zero) : (false, mute.Until - MuteClock());
    }

    /// <summary>How many seconds a flash lasts: the event's own number from the settings, else its severity's. 0: until the
    /// player switches to that client. The same rule FlashAlertTick follows.</summary>
    private static int FlashSeconds(Models.AppSettings s, AlertFlashInfo info) =>
        s.AlertDurations.TryGetValue(info.EventType, out var dur) && dur >= 0 ? dur : FlashExpiry.GetValueOrDefault(info.Severity, 6);

    private static LegendEvent LegendEventOf(string eventKey) => IndicatorLegend.EventOf(eventKey, Views.AlertHub.EventDisplayName);

    /// <summary>Everything the legend says about one thumbnail, read from the state the manager already holds: its group, whether it
    /// is the active client, the flash (seconds left on <see cref="FlashClock"/>), the unseen alerts, the cycle exclusion, the
    /// alert mute and the event snoozes (on <see cref="MuteClock"/>), the client's mark and the focus-mode hold. Reads only. The
    /// window's thread.</summary>
    public IndicatorSnapshot SnapshotFor(ThumbnailWindow thumb)
    {
        var s = _settings.Settings;
        string who = ThumbnailMenu.HasCharacter(thumb.CharacterName) ? thumb.CharacterName : "";

        var group = who.Length == 0 ? null
            : s.ThumbnailGroups.FirstOrDefault(g => g.Members.Any(m => m.Equals(who, StringComparison.OrdinalIgnoreCase)));   // the first, as the border rule takes it

        LegendAlert? alert = null;
        if (who.Length > 0 && TryGetFlash(who, out var flash) && flash != null)
        {
            int seconds = FlashSeconds(s, flash);
            var end = flash.StartTime.AddSeconds(seconds);
            var now = FlashClock();
            if (seconds == 0)
                alert = new LegendAlert(LegendEventOf(flash.EventType), null);
            else if (now >= flash.StartTime && end > now)   // over, or the clock was set back: the tick removes it
                alert = new LegendAlert(LegendEventOf(flash.EventType), (int)Math.Ceiling((end - now).TotalSeconds));
        }

        var mute = who.Length == 0 ? null : MuteOf(who);
        var snoozeNow = MuteClock();
        var snoozes = who.Length == 0 ? Array.Empty<LegendSnooze>()
            : SnoozesOf(who).Select(n => new LegendSnooze(LegendEventOf(n.EventKey), n.UntilRestart, n.UntilRestart ? TimeSpan.Zero : n.UntilUtc - snoozeNow)).ToArray();

        bool active = thumb.EveHwnd != IntPtr.Zero && thumb.EveHwnd == ActiveClient();
        // The swatch is the group's colour only where the border really is that colour: a colour of the character's own wins over
        // the group's, and the highlight can keep the active client out of it (BorderColorRule). Where they disagree, no swatch.
        string? swatch = group != null && s.ShowAllColoredBorders
            && string.Equals(BorderColorRule.Resolve(s, who, active, RunningCharacters), group.Color, StringComparison.OrdinalIgnoreCase)
            ? group.Color : null;

        return new IndicatorSnapshot
        {
            Character = who,
            GroupName = group?.Name,
            GroupColour = swatch,
            Active = active,
            ActiveBorderShown = s.ShowClientHighlightBorder,
            Alert = alert,
            Unseen = who.Length == 0 ? 0 : Attention.For(who)?.Count ?? 0,
            CycleExcluded = IsExcludedFromCycle(who),
            NotLoggedIn = who.Length == 0,
            Mute = mute is { } held ? new LegendMute(held.UntilCleared, held.Left) : null,
            Snoozes = snoozes,
            Mark = thumb.Mark,
            HeldHidden = thumb.HeldHidden,
        };
    }

    private System.Windows.Window? _legendWindow;
    private ThumbnailWindow? _legendFor;
    private string? _legendKey;   // what the popup says now (its heading, lines and swatches), to see whether a refresh changes it
    private (double PointerX, double PointerY, double Left, double Top, double Right, double Bottom)? _legendAnchor;   // where it was placed from (DIPs)

    private void OnLegendWanted(ThumbnailWindow thumb, bool wanted)
    {
        if (wanted) ShowIndicatorLegend(thumb);
        else if (ReferenceEquals(_legendFor, thumb)) HideIndicatorLegend();
    }

    /// <summary>About once a second while the popup shows: its lines are made again from the state as it is now.</summary>
    private void OnLegendRefresh(ThumbnailWindow thumb)
    {
        try { RefreshIndicatorLegend(thumb); }
        catch (Exception ex)
        {
            HideIndicatorLegend();
            ErrorLog.Shared.Record(new InvalidOperationException("The indicator legend could not be refreshed: " + ex.GetType().Name), "Thumbnails:Legend", DateTime.Now);
        }
    }

    /// <summary>Makes the popup's lines again (SnapshotFor and Describe, on the clocks the manager has) and, only when the texts
    /// differ from what it shows, puts them in the window: a countdown, an alert that began or ran out, a mute that ended. The
    /// popup is not made again, so it neither flickers nor moves off the pointer. False: nothing changed, or no popup is up
    /// for <paramref name="thumb"/>.</summary>
    public bool RefreshIndicatorLegend(ThumbnailWindow thumb)
    {
        var window = _legendWindow;
        if (window == null || !ReferenceEquals(_legendFor, thumb)) return false;
        var panel = BuildLegendPanel(SnapshotFor(thumb), out var key);
        if (key == _legendKey) return false;
        _legendKey = key;
        window.Content = panel;
        PlaceLegend(window, panel);
        return true;
    }

    /// <summary>The popup's contents, and a key that changes when any of its texts or swatches do.</summary>
    private static System.Windows.Controls.StackPanel BuildLegendPanel(IndicatorSnapshot snapshot, out string key)
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        var keyed = new System.Text.StringBuilder();
        var title = IndicatorLegend.Title(snapshot);
        keyed.Append(title).Append('\n');
        if (title.Length > 0)
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title, Foreground = System.Windows.Media.Brushes.White, FontSize = 12,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
            });
        foreach (var line in IndicatorLegend.Describe(snapshot))
        {
            keyed.Append(line.Colour).Append('|').Append(line.Text).Append('\n');
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            if (line.Colour is { } colour)
                row.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = new System.Windows.Media.SolidColorBrush(ParseColor(colour)),
                });
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = line.Text, Foreground = System.Windows.Media.Brushes.White, FontSize = 12,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
            });
            panel.Children.Add(row);
        }
        key = keyed.ToString();
        return panel;
    }

    /// <summary>Stands the popup where its pointer was when it came up, for the size its contents have now (no anchor: unchanged).</summary>
    private void PlaceLegend(System.Windows.Window window, System.Windows.Controls.StackPanel panel)
    {
        if (_legendAnchor is not { } a) return;
        panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var (x, y) = IndicatorLegend.Place(a.PointerX, a.PointerY, panel.DesiredSize.Width, panel.DesiredSize.Height, a.Left, a.Top, a.Right, a.Bottom);
        window.Left = x;
        window.Top = y;
    }

    /// <summary>The popup: a borderless, topmost window like the cursor note of <see cref="ShowTooltipFeedback"/>, and like it
    /// never activated (#110): ShowActivated is off and the window carries WS_EX_NOACTIVATE, so it can't take focus from EVE.
    /// Click-through too, so it never takes the pointer from the thumbnail under it. Not shown over a menu or during a drag.
    /// Placed near the pointer, inside the work area of the monitor the pointer is on.</summary>
    private void ShowIndicatorLegend(ThumbnailWindow thumb)
    {
        try
        {
            HideIndicatorLegend();
            if (ThumbnailWindow.MenuOpen || thumb.IsDragging) return;

            var panel = BuildLegendPanel(SnapshotFor(thumb), out var key);

            // Where: the cursor in physical pixels, the work area of its monitor, both in DIPs (as the cursor note does).
            var cursorPos = System.Windows.Forms.Cursor.Position;
            double dpi = Interop.DpiHelper.GetScaleFactorForPoint(cursorPos.X, cursorPos.Y);
            var area = System.Windows.Forms.Screen.FromPoint(cursorPos).WorkingArea;
            panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var anchor = (PointerX: Interop.DpiHelper.PhysicalToDip(cursorPos.X, dpi), PointerY: Interop.DpiHelper.PhysicalToDip(cursorPos.Y, dpi),
                Left: Interop.DpiHelper.PhysicalToDip(area.Left, dpi), Top: Interop.DpiHelper.PhysicalToDip(area.Top, dpi),
                Right: Interop.DpiHelper.PhysicalToDip(area.Right, dpi), Bottom: Interop.DpiHelper.PhysicalToDip(area.Bottom, dpi));
            var (x, y) = IndicatorLegend.Place(anchor.PointerX, anchor.PointerY, panel.DesiredSize.Width, panel.DesiredSize.Height,
                anchor.Left, anchor.Top, anchor.Right, anchor.Bottom);

            var window = new System.Windows.Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 40, 40, 40)),
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,   // Passive overlay: showing it must never take focus from the EVE client (#110).
                SizeToContent = SizeToContent.WidthAndHeight,
                Left = x,
                Top = y,
                Content = panel,
            };
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                int ex = Interop.User32.GetWindowLong(hwnd, Interop.User32.GWL_EXSTYLE);
                Interop.User32.SetWindowLong(hwnd, Interop.User32.GWL_EXSTYLE,
                    ex | Interop.User32.WS_EX_NOACTIVATE | Interop.User32.WS_EX_TOOLWINDOW | Interop.User32.WS_EX_TRANSPARENT);
            };
            _legendWindow = window;
            _legendFor = thumb;
            _legendKey = key;
            _legendAnchor = anchor;
            window.Show();
        }
        catch (Exception ex)
        {
            HideIndicatorLegend();
            // The exception's type only: its message can quote a character or group name.
            ErrorLog.Shared.Record(new InvalidOperationException("The indicator legend could not be shown: " + ex.GetType().Name), "Thumbnails:Legend", DateTime.Now);
        }
    }

    private void HideIndicatorLegend()
    {
        var window = _legendWindow;
        _legendWindow = null;
        _legendFor = null;
        _legendKey = null;
        _legendAnchor = null;
        try { window?.Close(); }
        catch (InvalidOperationException) { /* already closing */ }
    }

    // ── Snap ─────────────────────────────────────────────────────────

    // Edge-snap: snaps the dragged window's Left and Top independently to the
    // nearest of —
    //   • a neighbour's aligned edge (left/right/top/bottom aligned), or
    //   • flush adjacency to a neighbour (edges touching, no gap), or
    //   • the monitor work-area edge (flush).
    // Aligning left+top to a neighbour reproduces the old corner snap.
    private void SnapThumbnail(ThumbnailWindow target)
    {
        int snapRange = _settings.Settings.ThumbnailSnapDistance;
        if (snapRange <= 0) return;

        double L = target.Left, T = target.Top, W = target.Width, H = target.Height;

        var xCands = new List<double>();
        var yCands = new List<double>();
        foreach (var (_, other) in _thumbnails)
        {
            if (other == target) continue;
            double oL = other.Left, oT = other.Top, oW = other.Width, oH = other.Height;
            // Side-by-side snapping is FLUSH ONLY — edges touching, no gap.
            // The gutter-offset candidates (oL + oW + gutter etc.) were removed:
            // they created an invisible stop a few pixels short of the real edge,
            // and because both it and the flush position sat inside the snap
            // range, thumbnails would often catch on the gap instead of meeting.
            // ThumbnailGutter spaces the column MultiPreview lays out (FlowSlot);
            // it doesn't influence drag snapping.
            // X: align left, align right, sit flush right of, sit flush left of
            xCands.Add(oL);
            xCands.Add(oL + oW - W);
            xCands.Add(oL + oW);
            xCands.Add(oL - W);
            // Y: align top, align bottom, sit flush below, sit flush above
            yCands.Add(oT);
            yCands.Add(oT + oH - H);
            yCands.Add(oT + oH);
            yCands.Add(oT - H);
        }

        // Monitor work-area edges (flush to screen).
        var wa = System.Windows.Forms.Screen.FromRectangle(
            new System.Drawing.Rectangle((int)L, (int)T, (int)Math.Max(1, W), (int)Math.Max(1, H))).WorkingArea;
        xCands.Add(wa.Left);
        xCands.Add(wa.Right - W);
        yCands.Add(wa.Top);
        yCands.Add(wa.Bottom - H);

        double newL = SnapAxis(L, xCands, snapRange);
        double newT = SnapAxis(T, yCands, snapRange);

        if (newL != L || newT != T)
        {
            target.Left = newL;
            target.Top = newT;
            target.SyncOverlayPosition();
        }
    }

    /// <summary>Snap a coordinate to the nearest candidate within snapRange, else unchanged.</summary>
    private static double SnapAxis(double current, List<double> candidates, int snapRange)
    {
        double best = current, bestDelta = snapRange + 1;
        foreach (var c in candidates)
        {
            double d = Math.Abs(c - current);
            if (d <= snapRange && d < bestDelta) { bestDelta = d; best = c; }
        }
        return best;
    }





    // ── Session Timer ────────────────────────────────────────────────

    // ── Jump count (IDEAS P10) ───────────────────────────────────────

    /// <summary>The jumps each character has made this session. App feeds it from the log monitor's SystemChanged; the
    /// session timer's tick hands each thumbnail its character's count for a text template's {jumps}.</summary>
    public JumpCounter Jumps { get; } = new();

    /// <summary>Forget this character (Clients › Characters): its count goes, and the number on its thumbnail with it.</summary>
    public void ForgetJumps(string name)
    {
        Jumps.Forget(name);
        UpdateJumpCounts();
    }

    // ── Weekly playtime (IDEAS P11) ──────────────────────────────────

    /// <summary>Seconds on screen per character per local day, for a text template's {week}. Held (and playtime.json read)
    /// only while Count playtime is on; with it off the ledger is empty, nothing is read or written and {week} shows nothing.</summary>
    public PlaytimeLedger Playtime { get; }

    private PlaytimeStore? _playtimeFiles;
    /// <summary>The file, in the settings folder (a test hands in a store of its own).</summary>
    public PlaytimeStore PlaytimeFiles { get => _playtimeFiles ??= new PlaytimeStore(AppPaths.ConfigDir); set => _playtimeFiles = value; }

    /// <summary>The local time the ledger takes its days from (a test hands in a fixed one).</summary>
    public Func<DateTime> PlaytimeClock { get; set; } = () => DateTime.Now;

    /// <summary>The monotonic timestamp the tick measures time by, in <see cref="Stopwatch.Frequency"/> units (a test hands in a scripted one).</summary>
    public Func<long> PlaytimeTimestamp { get; set; } = Stopwatch.GetTimestamp;

    private const int PlaytimeSaveSeconds = 300;
    /// <summary>A playtime.json that could not be opened (another program holds it) is tried again this often (seconds of play) ...</summary>
    public const int PlaytimeRetrySeconds = 30;
    /// <summary>... and this many times in all, the first included; then it is left alone until the switch goes off and on.</summary>
    public const int PlaytimeLoadAttempts = 8;
    // _playtimeLoaded: the file was READ (or there was none), so the ledger holds what it held. Nothing is counted or saved before
    // that: a save of a ledger that never saw the file would write this session's time over the stored week.
    private bool _playtimeLoaded, _playtimeDirty;
    private int _playtimeLoadTries;
    private double _playtimeSinceTry;
    private long _playtimeStamp;                 // Stopwatch timestamp of the previous tick, 0 before the first
    private double _playtimeCarry, _playtimeSinceSave;

    /// <summary>The session timer's one-second tick: the time since the last one, from a clock that no wall-clock step moves.</summary>
    private void UpdatePlaytime()
    {
        if (!_settings.Settings.TrackPlaytime) { StopPlaytime(); return; }
        long now = PlaytimeTimestamp();
        double elapsed = _playtimeStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_playtimeStamp, now).TotalSeconds;
        _playtimeStamp = now;
        TickPlaytime(elapsed);
    }

    /// <summary>Counts <paramref name="elapsedSeconds"/> for every character that has a thumbnail (not a login screen), loads the
    /// file on the first call after the switch went on, saves every five minutes, and hands each thumbnail its week. A fraction
    /// is carried to the next tick. More than a minute at once (the PC slept) counts for nothing.</summary>
    public void TickPlaytime(double elapsedSeconds)
    {
        if (!_settings.Settings.TrackPlaytime) { StopPlaytime(); return; }
        if (!(elapsedSeconds > 0) || double.IsInfinity(elapsedSeconds)) elapsedSeconds = 0;
        if (!_playtimeLoaded)
        {
            _playtimeSinceTry += Math.Min(elapsedSeconds, PlaytimeRetrySeconds);
            if (_playtimeLoadTries == 0 || (_playtimeLoadTries < PlaytimeLoadAttempts && _playtimeSinceTry >= PlaytimeRetrySeconds))
            {
                _playtimeLoadTries++;
                _playtimeSinceTry = 0;
                if (PlaytimeFiles.TryLoad(out var held)) { Playtime.Import(held); _playtimeLoaded = true; }
            }
            if (!_playtimeLoaded) return;   // the file could not be read: count nothing, write nothing, try again later
        }
        _playtimeSinceSave += Math.Min(elapsedSeconds, PlaytimeSaveSeconds);
        double total = _playtimeCarry + elapsedSeconds;
        int whole = (int)Math.Floor(Math.Min(total, 1_000_000));
        _playtimeCarry = whole > PlaytimeLedger.MaxAddSeconds ? 0 : total - whole;
        if (whole is > 0 and <= PlaytimeLedger.MaxAddSeconds)
        {
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Playtime.BeginRound();   // one round per tick, however many characters it counts
            foreach (var (_, thumb) in _thumbnails)
                if (ThumbnailMenu.HasCharacter(thumb.CharacterName) && counted.Add(thumb.CharacterName) && Playtime.Add(thumb.CharacterName, whole))
                    _playtimeDirty = true;
        }
        if (_playtimeDirty && _playtimeSinceSave >= PlaytimeSaveSeconds) SavePlaytime();
        PushPlaytime();
    }

    /// <summary>Writes the ledger; false when it did not (not read yet, or the file could not be written: the dirty mark stays).</summary>
    private bool SavePlaytime()
    {
        _playtimeSinceSave = 0;
        if (!_playtimeLoaded) return false;
        if (!PlaytimeFiles.Save(Playtime.Export())) return false;
        _playtimeDirty = false;
        return true;
    }

    /// <summary>The week on each thumbnail that has a character; a thumbnail compares it with what it shows, so this is cheap.</summary>
    private void PushPlaytime()
    {
        foreach (var (_, thumb) in _thumbnails)
            if (ThumbnailMenu.HasCharacter(thumb.CharacterName))
                thumb.SetWeek(PlaytimeLedger.Format(Playtime.WeekSeconds(thumb.CharacterName)));
    }

    /// <summary>The switch is off: what is held is dropped without a write (the file keeps what was last saved) and {week} goes.</summary>
    private void StopPlaytime()
    {
        if (!_playtimeLoaded && _playtimeLoadTries == 0) return;
        _playtimeLoaded = _playtimeDirty = false;
        _playtimeLoadTries = 0;
        _playtimeSinceTry = 0;
        _playtimeStamp = 0;
        _playtimeCarry = _playtimeSinceSave = 0;
        Playtime.Clear();
        foreach (var (_, thumb) in _thumbnails) thumb.SetWeek(null);
    }

    /// <summary>At exit: what was counted since the last save is written, if the switch is on.</summary>
    public void FlushPlaytime()
    {
        if (_playtimeLoaded && _playtimeDirty && _settings.Settings.TrackPlaytime) SavePlaytime();
    }

    /// <summary>Forget this character (Clients › Characters): the name leaves the ledger and the file at once. With the switch off
    /// nothing is held, but the file may still hold the name, so it is taken out of the file (and nothing is written if it is not there).
    /// False when the name may still be in the file: it could not be read or written. A save that failed is tried again at the next
    /// save and at the exit; a file that could not be read is not.</summary>
    public bool ForgetPlaytime(string name)
    {
        if (_playtimeLoaded)
        {
            // The file still holds the name until it is written: dirty, so a save that fails is tried again (the tick, the exit).
            bool saved = true;
            if (Playtime.Forget(name)) { _playtimeDirty = true; saved = SavePlaytime(); }
            PushPlaytime();
            return saved;
        }
        return PlaytimeFiles.Forget(name);
    }

    /// <summary>Clear playtime history: the file (and the ones set aside) is deleted and the ledger emptied, whatever the switch says.</summary>
    public void ClearPlaytime()
    {
        Playtime.Clear();
        _playtimeDirty = false;
        PlaytimeFiles.Delete();
        if (_playtimeLoaded) PushPlaytime();
    }

    private void UpdateJumpCounts()
    {
        foreach (var (_, thumb) in _thumbnails)
            if (ThumbnailMenu.HasCharacter(thumb.CharacterName)) thumb.SetJumps(Jumps.CountOf(thumb.CharacterName));
    }

    private void UpdateSessionTimers()
    {
        UpdatePlaytime();   // IDEAS P11: the existing one-second tick, no timer of its own
        // Off: hide the timer, or the last time stays frozen on every thumbnail until a restart.
        bool show = _settings.Settings.ShowSessionTimer;
        foreach (var (_, thumb) in _thumbnails)
        {
            if (!show) thumb.HideSessionTimer();
            else if (!string.IsNullOrEmpty(thumb.CharacterName)) thumb.UpdateSessionTimer(thumb.SessionDuration);
        }
    }

    private void UpdateFpsOverlays()
    {
        bool isEnabled = _settings.Settings.ShowRtssFps;
        
        if (!isEnabled)
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                thumb.UpdateFpsStats(0, false);
            }
            return;
        }

        var allFps = RtssMemoryReader.GetAllFps();

        foreach (var (_, thumb) in _thumbnails)
        {
            int pid = thumb.GetProcessId();
            if (pid > 0 && allFps.TryGetValue(pid, out double fps))
            {
                thumb.UpdateFpsStats(fps, true);
            }
            else
            {
                thumb.UpdateFpsStats(0, false);
            }
        }
    }

    // ── System Name Updates ─────────────────────────────────────────

    public void UpdateCharacterSystem(string characterName, string systemName)
    {
        // Capture the previous system BEFORE overwriting so we can show a
        // "old → new" transition animation (issue #25). First sighting (no
        // previous entry) takes the plain-update path with no animation.
        bool hadPrevious = _charSystems.TryGetValue(characterName, out var previousSystem);
        bool changed = !hadPrevious
            || !string.Equals(previousSystem, systemName, StringComparison.OrdinalIgnoreCase);
        _charSystems[characterName] = systemName;

        if (!_settings.Settings.ShowSystemName) return;

        // Posted, not waited for (IDEAS P57): this runs on the log reader's thread, and everything above is already
        // captured. A throw inside a posted lambda reaches the app's unhandled-exception handler, so it is recorded
        // here, by type only.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                foreach (var (_, thumb) in _thumbnails)
                {
                    if (!thumb.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (hadPrevious && changed && !string.IsNullOrEmpty(previousSystem))
                        thumb.AnimateSystemTransition(previousSystem!, systemName);
                    else
                        thumb.UpdateSystemName(systemName);
                }
            }
            catch (Exception ex)
            {
                // The exception's type only: its message can quote a character or system name.
                ErrorLog.Shared.Record(new InvalidOperationException("A system label could not be updated: " + ex.GetType().Name), "Thumbnails:SystemLabel", DateTime.Now);
            }
        });
    }

    // ── Per-character alert mute / snooze ───────────────────────────

    /// <summary>A character's alerts were muted (the thumbnail's menu, or the --mute command; not an unmute, and not a
    /// mute that ran out): its name. App hands it to the Windows notifications, which take that client's away (IDEAS P86).
    /// On the window's thread.</summary>
    public event Action<string>? AlertMuted;

    /// <summary>True while this character's alerts are muted/snoozed. Runtime-only;
    /// expired snoozes auto-clean. Checked by the central alert handler so a muted
    /// client raises no flash / badge / toast / sound.</summary>
    public bool IsCharacterAlertMuted(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return false;
        if (!_alertMutedChars.TryGetValue(characterName, out var mute)) return false;
        if (mute.Until == DateTime.MaxValue) return true;
        // FY-I1: an end further ahead than the mute is long can't be its own (the clock was set back): it is over.
        var left = mute.Until - _muteClock();
        if (left > TimeSpan.Zero && left <= mute.Length) return true;
        _alertMutedChars.TryRemove(characterName, out _); // snooze expired
        return false;
    }

    /// <summary>Context-menu handler: minutes &gt;0 snoozes that long, 0 unmutes,
    /// int.MaxValue mutes until explicitly cleared.</summary>
    private void OnAlertMuteRequested(ThumbnailWindow thumb, int minutes)
    {
        var charName = thumb.CharacterName;
        SetCharacterAlertMute(charName, minutes);
    }

    /// <summary>The alert mute for one character by name, whatever asked for it: the thumbnail's menu (through
    /// OnAlertMuteRequested) and the --mute / --unmute commands (IDEAS P129) both end here, so both leave the same state.
    /// Minutes &gt;0 mutes that long, 0 unmutes, int.MaxValue mutes until cleared. Runs on the window's thread.</summary>
    public void SetCharacterAlertMute(string charName, int minutes)
    {
        if (string.IsNullOrEmpty(charName)) return;

        DateTime? until;
        string note;
        string who = StreamerMask.Name(StreamerKind.Character, charName);   // streamer mode (IDEAS #19): the note only; charName keys the mute
        if (minutes <= 0)
        {
            _alertMutedChars.TryRemove(charName, out _);
            until = null;
            note = string.Format(LocalizationService.Str("L.Msg.AlertsUnmuted", "{0}: 🔔 Alerts unmuted"), who);
        }
        else if (minutes == int.MaxValue)
        {
            until = DateTime.MaxValue;
            _alertMutedChars[charName] = (until.Value, TimeSpan.MaxValue);
            note = string.Format(LocalizationService.Str("L.Msg.AlertsMuted", "{0}: 🔇 Alerts muted"), who);
        }
        else
        {
            until = _muteClock().AddMinutes(minutes);
            _alertMutedChars[charName] = (until.Value, TimeSpan.FromMinutes(minutes));
            note = string.Format(LocalizationService.Str("L.Msg.AlertsMutedMins", "{0}: 🔇 Alerts muted {1}m"), who, minutes);
        }

        // Reflect on every thumbnail showing this character (menu header), its second thumbnail too (IDEAS P36).
        foreach (var (_, t) in _thumbnails)
            if (t.CharacterName.Equals(charName, StringComparison.OrdinalIgnoreCase))
                t.AlertMutedUntil = until;
        foreach (var (_, pip) in _secondaryThumbnails)
            if (pip.CharacterName.Equals(charName, StringComparison.OrdinalIgnoreCase))
                pip.AlertMutedUntil = until;

        // Clearing an in-progress flash on mute so a currently-flashing alt goes quiet now.
        if (until != null) ClearAlertFlash(charName);
        // Its thumbnail, enlarged for an alert, goes back now too (IDEAS #13, T6-m3), not when its seconds are over.
        // And the enlargement is forgotten (RD-m2): held on, it would swallow every alert that follows an unmute
        // within its seconds (each would only make it longer, and enlarge nothing).
        if (until != null)
        {
            GetThumbnailForCharacter(charName)?.SetAlertScale(1.0);
            Enlarge.Forget(charName);
            AlertMuted?.Invoke(charName);
        }

        ShowTooltipFeedback(note);
    }

    // ── Per-process audio (auto-solo + per-client mute/volume) ──────

    private HashSet<uint> CollectClientPids(IntPtr hwndToMatch, out uint pidForHwnd)
    {
        var pids = new HashSet<uint>();
        pidForHwnd = 0;
        foreach (var (hwnd, _) in _thumbnails)
        {
            Interop.User32.GetWindowThreadProcessId(hwnd, out uint p);
            if (p == 0) continue;
            pids.Add(p);
            if (hwnd == hwndToMatch) pidForHwnd = p;
        }
        return pids;
    }

    /// <summary>Mute every tracked client except the active one (auto-solo).</summary>
    private void ApplyAudioSolo(IntPtr activeHwnd)
    {
        var pids = CollectClientPids(activeHwnd, out uint activePid);
        if (activePid != 0 && pids.Count > 0) _audio.ApplySolo(activePid, pids);
    }

    /// <summary>Unmute every tracked client's audio (auto-solo turned off / on exit).</summary>
    public void UnmuteAllClientAudio()
    {
        var pids = CollectClientPids(IntPtr.Zero, out _);
        if (pids.Count > 0) _audio.UnmuteAll(pids);
    }

    /// <summary>Mute every tracked client's audio. Used when focus leaves EVE
    /// entirely and MuteAllClientAudioWhenNoneActive is on, so the previously-active
    /// client doesn't stay the sole unmuted one.</summary>
    private void MuteAllClientAudio()
    {
        // One session enumeration for all clients, not one per client: this runs
        // synchronously on every switch away from EVE.
        var pids = CollectClientPids(IntPtr.Zero, out _);
        if (pids.Count > 0) _audio.MuteAll(pids);
    }

    private void OnAudioRequested(ThumbnailWindow thumb, int code)
    {
        if (code < 0)
        {
            // Mute is a transient toggle (not persisted), like the auto-solo path.
            Interop.User32.GetWindowThreadProcessId(thumb.EveHwnd, out uint pid);
            if (pid != 0) _audio.SetMute(pid, true);
            ShowTooltipFeedback(string.Format(LocalizationService.Str("L.Msg.AudioMuted", "{0}: audio muted"), StreamerMask.Name(StreamerKind.Character, thumb.CharacterName)));
        }
        else
        {
            // Volume from the right-click slider persists, same as the Settings slider.
            SetClientVolume(thumb.CharacterName, code);
        }
    }

    /// <summary>Set + persist a client's audio volume (0-100; 100 = default). Applied
    /// live and re-applied when the client is next activated. Used by Settings sliders.</summary>
    public void SetClientVolume(string characterName, int percent)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        percent = Math.Clamp(percent, 0, 100);
        var map = _settings.Settings.PerClientAudioVolume;
        if (percent >= 100) map.Remove(characterName);
        else map[characterName] = percent;
        _settings.SaveDelayed();   // sliders fire per-tick — debounce the write

        foreach (var (hwnd, thumb) in _thumbnails)
            if (string.Equals(thumb.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
            {
                thumb.AudioVolume = percent;   // keep the right-click slider in sync
                Interop.User32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0) _audio.SetVolume(pid, percent / 100f);
            }
        // The second thumbnail's slider shows the same client volume (IDEAS P36); the loop above set it.
        foreach (var (_, pip) in _secondaryThumbnails)
            if (string.Equals(pip.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                pip.AudioVolume = percent;
    }

    /// <summary>Persisted volume for a character (0-100; 100 if unset).</summary>
    public int GetClientVolume(string characterName)
        => _settings.Settings.PerClientAudioVolume.TryGetValue(characterName ?? "", out var v) ? v : 100;

    private void ReapplyClientVolume(IntPtr hwnd)
    {
        if (!_thumbnails.TryGetValue(hwnd, out var thumb) || string.IsNullOrEmpty(thumb.CharacterName)) return;
        if (_settings.Settings.PerClientAudioVolume.TryGetValue(thumb.CharacterName, out var v) && v < 100)
        {
            Interop.User32.GetWindowThreadProcessId(hwnd, out uint p);
            if (p != 0) _audio.SetVolume(p, v / 100f);
        }
    }

    // ── Alert Flash (per-severity rates + expiry) ────────────────────

    public void SetAlertFlash(string characterName, string severity = "critical", string eventType = "attack")
    {
        var now = _flashClock();
        StartFlash(characterName, severity, eventType, now);
        Debug.WriteLine($"[AlertFlash:Start] 🚨 Flash started: '{characterName}' severity={severity} event={eventType}");
    }

    /// <summary>The flash of a character's border begins at <paramref name="now"/>. There is one entry per character, and
    /// the one flash timer walks the table: a flash that begins while another one is on takes its place.</summary>
    private void StartFlash(string characterName, string severity, string eventType, DateTime now) =>
        _alertFlashChars[characterName] = new AlertFlashInfo
        {
            StartTime = now,
            Severity = severity,
            EventType = eventType,
            ShowFlash = true,
            LastToggleTime = now
        };

    /// <summary>Whether a repeat of a critical alert flashes the client's border again (IDEAS P82 (1)): the border flash is
    /// on (a border width of 0 switches it off), the character has a client, and that client is not the window in front.
    /// The repeats of a do-not-disturb that holds the sounds never get here (AlertEscalation does not ask for one).</summary>
    public static bool RepeatFlashes(int alertBorderThickness, IntPtr client, IntPtr foreground) =>
        alertBorderThickness > 0 && client != IntPtr.Zero && !InFront(client, foreground);

    /// <summary>A repeat of a critical alert flashes the client's border again, with the flash the alert raised: its
    /// severity, the event's own duration, colour and steadiness (FlashAlertTick reads those from the event). Called by
    /// App for each repeat that is counted. The table has one entry per character, so a repeat renews the flash that is
    /// on: it never adds a second one. Writes no line that names the character.</summary>
    /// <returns>Whether the border flashes again.</returns>
    public bool FlashAgain(string characterName, string severity, string eventType) =>
        FlashAgain(characterName, severity, eventType, Interop.User32.GetForegroundWindow());

    /// <summary><see cref="FlashAgain(string, string, string)"/> with the window in front handed in (tests).</summary>
    public bool FlashAgain(string characterName, string severity, string eventType, IntPtr foreground)
    {
        if (string.IsNullOrEmpty(characterName)
            || !RepeatFlashes(_settings.Settings.AlertBorderThickness, GetHwndForCharacter(characterName), foreground)) return false;
        var now = FlashClock();
        // Another event's flash that is still on is newer than this repeat's alert (an old critical repeating over a
        // warning raised since): the repeat leaves it be. Its own event's flash is the one a repeat renews.
        if (_alertFlashChars.TryGetValue(characterName, out var current)
            && !string.Equals(current.EventType, eventType, StringComparison.Ordinal)
            && !FlashIsOver(characterName, current, now, foreground, out _)) return false;
        StartFlash(characterName, severity, eventType, now);
        return true;
    }

    /// <summary>Whether a character's flash has run its course at <paramref name="now"/>: the event's own duration
    /// (it overrides the severity's), where 0 means until the player switches to that client - or right away when
    /// the client is the window in front, which no switch would clear. A moment ahead of now (the clock was set
    /// back) ends it. <paramref name="seconds"/> is the duration that was used.</summary>
    private bool FlashIsOver(string characterName, AlertFlashInfo info, DateTime now, IntPtr foreground, out int seconds)
    {
        var s = _settings.Settings;
        int expirySec = s.AlertDurations.TryGetValue(info.EventType, out var dur) && dur >= 0
            ? dur
            : FlashExpiry.GetValueOrDefault(info.Severity, 6);
        seconds = expirySec;
        return expirySec == 0
            ? FindThumbnailByCharacter(characterName) is not { } alertThumb || alertThumb.EveHwnd == foreground   // gone = over
            : now < info.StartTime || (now - info.StartTime).TotalSeconds >= expirySec;   // ahead of now = the clock was set back
    }

    public void ClearAlertFlash(string characterName)
    {
        if (_alertFlashChars.TryRemove(characterName, out _))
        {
            ClearAlertBorderFor(characterName);
            Debug.WriteLine($"[AlertFlash:Tick] ✅ Flash cleared: '{characterName}'");
        }
    }

    /// <summary>Explicitly clear the nested inner alert border for a character (#77).
    /// When an alert entry is removed OUTSIDE FlashAlertTick (e.g. activating/cycling
    /// to the client clears _alertFlashChars), FlashAlertTick no longer tracks it and
    /// UpdateActiveBorders can skip its per-thumb clear under the post-cycle shield —
    /// leaving the inner border stuck on until a click. Clearing here closes that gap.
    /// Marshals to the UI thread for the WinForms SetAlertBorder call.</summary>
    private void ClearAlertBorderFor(string characterName)
    {
        var thumb = FindThumbnailByCharacter(characterName);
        if (thumb == null) return;
        void Clear() { thumb.SetAlertBorder(null, 0); thumb.SetAlertText(null, default); }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(Clear));
        else
            Clear();
    }

    /// <summary>Bump the per-character unread-alert badge counter. Called from
    /// App's AlertTriggered handler, after <see cref="NoteUnseenAlert"/> (the dispatcher's order): the badge asks
    /// the unseen list, and does not read the foreground window itself (IDEAS P75). Skips entirely when the list
    /// holds nothing for the character: its client was in front as the alert arrived (the user is looking at it, no
    /// need to badge them about something they're seeing in real time), or it has no client.</summary>
    public void IncrementAlertBadge(string characterName, string severity, string alertType = "")
    {
        if (string.IsNullOrEmpty(characterName))
        {
            DiagnosticsService.LogAlerts($"[Badge] DROPPED — empty character name");
            return;
        }
        if (!_settings.Settings.ShowAlertBadgeOnThumbnails)
        {
            DiagnosticsService.LogAlerts($"[Badge] DROPPED — ShowAlertBadgeOnThumbnails=false for '{characterName}'");
            return;
        }

        // Per-event opt-out. Missing key = on (badge fires) so existing
        // installs keep behaving the same; the user can uncheck specific
        // events in Settings → Alerts → Alert Events to silence their
        // thumbnail badge while keeping toast / sound / pulse intact.
        if (!string.IsNullOrEmpty(alertType)
            && _settings.Settings.BadgeOnThumbnailAlertTypes.TryGetValue(alertType, out var perEvtBadge)
            && !perEvtBadge)
        {
            DiagnosticsService.LogAlerts($"[Badge] DROPPED — badge disabled for event '{alertType}' on '{characterName}'");
            return;
        }

        // One answer to "is the player looking at this client": the unseen list's. NoteUnseenAlert has just
        // taken this alert as unseen, or as seen at once because the client was in front, or not at all because
        // the character has no client. AlertBadgeInfo.CountAlert asks it, counts through the table's own update (alerts
        // come from two threads, and no stored value is changed in place), and keeps the badge to what the list holds.
        if (AlertBadgeInfo.CountAlert(Attention, _alertBadges, characterName, severity) is not { } shown)
        {
            DiagnosticsService.LogAlerts($"[Badge] SUPPRESSED — '{characterName}' has nothing unseen (its client is in front, or it has none)");
            return;
        }

        DiagnosticsService.LogAlerts($"[Badge] INCREMENTED — '{characterName}' count={shown} severity={severity}");
        ApplyBadgeToThumbnail(characterName);
    }

    /// <summary>Clear the per-character unread-alert badge. Called when the
    /// user activates that character (cycle hotkey or thumbnail click brings
    /// the EVE window to foreground), or whenever the settings toggle is
    /// turned off.</summary>
    public void ClearAlertBadge(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        if (_alertBadges.TryRemove(characterName, out _))
            ApplyBadgeToThumbnail(characterName);
    }

    /// <summary>A character lost an unseen alert to age (<see cref="AlertAttention.Expired"/>): the badge goes with the
    /// last one, and otherwise counts no more than what is left. It never grows, and a character with no badge gets none
    /// (the badge may be off, or off for the event).</summary>
    private void TrimAlertBadge(string characterName)
    {
        if (Attention.For(characterName) is not { } left) ClearAlertBadge(characterName);
        else if (AlertBadgeInfo.CapAt(_alertBadges, characterName, left.Count)) ApplyBadgeToThumbnail(characterName);
    }

    /// <summary>Push the badge count to the thumbnail's text overlay. The
    /// badge is always painted red — the user explicitly wants it loud, not
    /// severity-coded, so it stands out on the thumbnail at a glance. The count is not handed over by the caller: it
    /// is read from the table when the posted action runs, on the window's thread. Alerts come from two threads, and
    /// their posted actions can run in the other order than the alerts were counted; each paints what the table holds
    /// then, so the one that runs last paints the newest count, and a clear paints nothing only while nothing came
    /// after it.</summary>
    private void ApplyBadgeToThumbnail(string characterName)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            int count = AlertBadgeInfo.CountOf(_alertBadges, characterName);
            int matched = 0;
            foreach (var (_, thumb) in _thumbnails)
            {
                if (!string.Equals(thumb.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                    continue;
                thumb.SetAlertBadge(count, "#FF0000");
                matched++;
            }
            DiagnosticsService.LogAlerts(
                $"[Badge] ApplyBadgeToThumbnail char='{characterName}' count={count} matchedThumbs={matched}");
        });
    }

    /// <summary>Dedicated flash timer tick — handles per-severity flash rates, expiry, and border updates.</summary>
    /// <summary>Apply the nested inner alert border for a thumbnail (#71). The
    /// inner band pulses on alert without disturbing the main highlight border, so
    /// the selected client stays identifiable during a fleet-wide flash. Pass a
    /// null/absent <paramref name="info"/> to clear it. Thickness is held at the
    /// configured value for the whole alert (only the colour toggles while pulsing)
    /// so the composited thumbnail doesn't jump on each pulse.</summary>
    private void ApplyAlertBorder(ThumbnailWindow thumb, AlertFlashInfo? info)
    {
        var s = _settings.Settings;
        int thickness = s.AlertBorderThickness;

        // 0 disables the nested band entirely (per the Alerts-page note); also clear
        // it when no alert is active.
        if (info == null || thickness <= 0)
        {
            thumb.SetAlertBorder(null, 0);
            return;
        }

        // Visible during the "on" phase, and continuously for info-severity
        // (which doesn't pulse) or an event set to a steady border (#115). Off-phase
        // keeps the reserved thickness but paints nothing, so the band blinks while
        // the main border stays put.
        bool paint = info.ShowFlash || info.Severity == "info"
                     || s.AlertSolidBorder.GetValueOrDefault(info.EventType);
        var color = ResolveAlertFlashColor(info.EventType, info.Severity);
        thumb.SetAlertBorder(paint ? color : (Color?)null, thickness);
    }

    private void FlashAlertTick()
    {
        if (_alertFlashChars.IsEmpty) return;

        var now = _flashClock();
        var s = _settings.Settings;
        var toRemove = new List<string>();

        IntPtr fgNow = Interop.User32.GetForegroundWindow();
        foreach (var (charName, info) in _alertFlashChars)
        {
            // Check expiry. Per-event duration overrides the severity default (#115);
            // 0 = keep it until you switch to that client - or right away when the
            // alert is on the client you are already in, which no switch would clear.
            bool expired = FlashIsOver(charName, info, now, fgNow, out int expirySec);
            if (expired)
            {
                toRemove.Add(charName);
                Debug.WriteLine($"[AlertFlash:Expire] ⏰ Flash expired: '{charName}' after {expirySec}s ({info.Severity})");
                continue;
            }

            // Check if it's time to toggle based on severity rate. User-
            // configurable via Settings → Alerts → Severity Settings (#38);
            // falls back to the legacy 200 / 500 / 1000 ms defaults if a
            // severity isn't in the settings dict.
            int rateMs = s.SeverityFlashRates.TryGetValue(info.Severity, out var sr)
                ? sr
                : FlashRates.GetValueOrDefault(info.Severity, 500);
            if ((now - info.LastToggleTime).TotalMilliseconds >= rateMs || now < info.LastToggleTime)
            {
                info.ShowFlash = !info.ShowFlash;
                info.LastToggleTime = now;
            }

            // Apply flash border color directly (fixes: UpdateActiveBorders skips this via early return)
            var thumb = FindThumbnailByCharacter(charName);
            if (thumb != null)
            {
                var flashColor = ResolveAlertFlashColor(info.EventType, info.Severity);

                // Resolve the resting border that should show *between* flash
                // pulses — the configured active/group/per-character/inactive
                // colour. Pulsing between this and the alert colour preserves
                // visual context (which client is active, which group it
                // belongs to) instead of stomping the configured colour
                // entirely while the alert is up. Reported as #36 by darkscion0.
                bool isActive = thumb.EveHwnd == Interop.User32.GetForegroundWindow();
                var restingColor = GetBorderColor(charName, isActive);
                int restingThickness = isActive
                    ? s.ClientHighlightBorderThickness
                    : (ShouldShowInactiveBorder(charName) ? s.InactiveClientBorderThickness : 0);

                // Main border stays at the resting highlight at all times; the
                // alert pulse rides the nested inner border (#71).
                thumb.SetBorder(restingColor, restingThickness);
                ApplyAlertBorder(thumb, info);

                // What happened, written on the thumbnail in the alert's colour (#115).
                // Full opacity: the opacity setting softens the border, not the words.
                thumb.SetAlertText(s.ShowAlertTextOnThumbnails ? Views.AlertHub.EventDisplayName(info.EventType) : null,
                    Color.FromRgb(flashColor.R, flashColor.G, flashColor.B));
            }
        }

        // Remove expired flashes and restore the correct resting border.
        // The flashing character may be the foreground client — e.g. a
        // system_change flash on the client you're actively flying after a
        // jump. Restoring the inactive border unconditionally left the
        // focused client with the wrong border colour until the next focus
        // change (the bug: border didn't show after jumping system until you
        // clicked the thumbnail or cycled away and back). Match the canonical
        // active/inactive border logic from UpdateActiveBorders here.
        IntPtr fg = Interop.User32.GetForegroundWindow();
        foreach (var name in toRemove)
        {
            _alertFlashChars.TryRemove(name, out _);
            var thumb = FindThumbnailByCharacter(name);
            if (thumb == null) continue;

            // Alert over — remove the nested inner band; the main border was never
            // disturbed so it already shows the correct resting highlight (#71).
            thumb.SetAlertBorder(null, 0);
            thumb.SetAlertText(null, default);

            if (thumb.EveHwnd == fg)
            {
                // Active client — restore the highlight border (honoring the
                // ShowClientHighlightBorder toggle, same as UpdateActiveBorders).
                if (s.ShowClientHighlightBorder)
                    thumb.SetBorder(GetBorderColor(name, true), s.ClientHighlightBorderThickness);
                else
                    thumb.SetBorder(Color.FromArgb(0, 0, 0, 0), 0);
            }
            else
            {
                var color = GetBorderColor(name, false);
                int thickness = ShouldShowInactiveBorder(name) ? s.InactiveClientBorderThickness : 0;
                thumb.SetBorder(color, thickness);
            }
        }
    }

    // ── Under Fire Indicator ────────────────────────────────────────

    /// <summary>
    /// Signal that a character is under fire (taking damage).
    /// Updates the timestamp and activates the pulsing red border.
    /// Called from App.xaml.cs when DamageReceived fires.
    /// </summary>
    public void SignalUnderFire(string characterName)
    {
        if (!_settings.Settings.EnableUnderFireIndicator) return;

        _underFireChars[characterName] = _flashClock();

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var thumb = FindThumbnailByCharacter(characterName);
            thumb?.SetUnderFire(true);
        });
    }

    /// <summary>Check under-fire expiry and deactivate expired indicators.</summary>
    private void CheckUnderFireExpiry()
    {
        if (_underFireChars.IsEmpty) return;

        var now = _flashClock();
        int timeoutSec = _settings.Settings.UnderFireTimeoutSeconds;
        var expired = new List<string>();

        foreach (var (charName, lastDamage) in _underFireChars)
        {
            if (now < lastDamage || (now - lastDamage).TotalSeconds >= timeoutSec)
                expired.Add(charName);
        }

        foreach (var charName in expired)
        {
            _underFireChars.TryRemove(charName, out _);
            var thumb = FindThumbnailByCharacter(charName);
            thumb?.SetUnderFire(false);
        }
    }

    private ThumbnailWindow? FindThumbnailByCharacter(string characterName)
    {
        foreach (var (_, thumb) in _thumbnails)
        {
            if (thumb.CharacterName == characterName) return thumb;
        }
        return null;
    }

    // ── Quick-Switch Wheel ──────────────────────────────────────────

    private Views.QuickSwitchWheel? _quickSwitchWheel;

    /// <summary>Open the Quick-Switch radial wheel with all active characters.</summary>
    public void ShowQuickSwitch()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Toggle: close if already visible
                if (_quickSwitchWheel != null && _quickSwitchWheel.IsVisible)
                {
                    _quickSwitchWheel.ForceClose();
                    return;
                }

                var characters = _thumbnails.Values
                    .Where(t => !string.IsNullOrEmpty(t.CharacterName) && t.CharacterName != "EVE")
                    .Select(t => t.CharacterName)
                    .Distinct()
                    .ToList();

                if (characters.Count == 0) return;

                // Apply saved order — saved chars first, then any new chars at end
                var savedOrder = _settings.Settings.QuickSwitchCardOrder;
                var ordered = savedOrder
                    .Where(n => characters.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var unsaved = characters
                    .Where(n => !savedOrder.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(n => n)
                    .ToList();
                ordered.AddRange(unsaved);

                // Recreate wheel each time
                if (_quickSwitchWheel != null)
                {
                    try { _quickSwitchWheel.Close(); } catch { }
                }

                _quickSwitchWheel = new Views.QuickSwitchWheel();
                _quickSwitchWheel.CharacterSelected += (charName) =>
                {
                    ActivateEveWindow(IntPtr.Zero, charName);
                };
                _quickSwitchWheel.OrderSaved += (newOrder) =>
                {
                    _settings.Settings.QuickSwitchCardOrder = newOrder;
                    _settings.Save();
                    Debug.WriteLine($"[QuickSwitch] 💾 Card order saved: {string.Join(", ", newOrder)}");
                };

                _quickSwitchWheel.ShowWheel(ordered, GetCharacterSystem, Attention.For, _settings.Settings);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[QuickSwitch] ❌ Error: {ex.Message}");
                Debug.WriteLine(ex.ToString());
                _quickSwitchWheel = null;
            }
        });
    }


    // ── Char Select Cycling (AHK CharSelectCycling) ─────────────────

    private int _charSelectIndex = -1;

    /// <summary>Cycle through characters at the login/char-select screen.</summary>
    public void CycleCharSelect(bool forward)
    {
        try
        {
            // Find all char-select windows (title == "EVE" without character name)
            var charSelectWindows = _thumbnails
                .Where(kv => string.IsNullOrEmpty(kv.Value.CharacterName) || kv.Value.CharacterName == "EVE")
                .Select(kv => kv.Key)
                .OrderBy(h => h.ToInt64()) // Sort by HWND for stable ordering (matches AHK)
                .ToList();

            if (charSelectWindows.Count == 0)
            {
                Debug.WriteLine("[CharCycle:Select] ⚠ No char-select (login-screen) windows found — feature only cycles between EVE clients sitting on the character-select screen, not logged-in characters");
                return;
            }
            if (charSelectWindows.Count == 1)
            {
                Debug.WriteLine("[CharCycle:Select] ⚠ Only one char-select window — nothing to cycle to");
                // Still activate it so the hotkey at least feels responsive
                var solo = charSelectWindows[0];
                if (Interop.User32.IsIconic(solo)) Interop.User32.ShowWindowAsync(solo, Interop.User32.SW_RESTORE);
                Interop.User32.ActivateWindow(solo);
                return;
            }

            // Step from the client that is actually in front (#116). The stored index
            // alone went stale as soon as you clicked a client, so the next press could
            // "switch" to the one you were already in.
            int fgIdx = charSelectWindows.IndexOf(Interop.User32.GetForegroundWindow());
            if (fgIdx >= 0) _charSelectIndex = fgIdx;

            if (forward)
                _charSelectIndex = (_charSelectIndex + 1) % charSelectWindows.Count;
            else
                _charSelectIndex = (_charSelectIndex - 1 + charSelectWindows.Count) % charSelectWindows.Count;

            var targetHwnd = charSelectWindows[_charSelectIndex];

            // Restore if minimized
            if (Interop.User32.IsIconic(targetHwnd))
                Interop.User32.ShowWindowAsync(targetHwnd, Interop.User32.SW_RESTORE);

            // The same activation every other switch uses (#116): the vk0xE8 bridge. A bare
            // SetForegroundWindow is refused when the hotkey's keystroke was hidden from
            // the clients (2.3.38+): the press never reaches Windows as our input, so we
            // hold no right to take the foreground - unless a thumbnail was just clicked.
            Interop.User32.ActivateWindow(targetHwnd);
            
            // Defer WPF z-order operations to prevent dispatch pumping from eating the OS input state interrupt
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var (_, t) in _thumbnails) t.BringToFront();
                foreach (var (_, p) in _secondaryThumbnails) p.BringToFront();
                foreach (var (_, sw) in _statWindows) sw.BringToFront();
                RaiseEnlargedAgain();
            }, System.Windows.Threading.DispatcherPriority.Background);

            Debug.WriteLine($"[CharCycle:Select] 🔄 Cycled to char-select window idx={_charSelectIndex}/{charSelectWindows.Count} (hwnd=0x{targetHwnd:X})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CharCycle:Error] ❌ CycleCharSelect error: {ex.Message}");
        }
    }

    // ── Client Position Save/Restore ────────────────────────────────

    public void SaveClientPositions()
    {
        foreach (var (eveHwnd, thumb) in _thumbnails)
        {
            if (string.IsNullOrEmpty(thumb.CharacterName)) continue;

            try
            {
                Interop.User32.GetWindowRect(eveHwnd, out var rect);
                bool isMaximized = Interop.User32.IsZoomed(eveHwnd);

                var pos = new ClientPosition
                {
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height,
                    IsMaximized = isMaximized ? 1 : 0
                };

                _settings.CurrentProfile.ClientPositions[thumb.CharacterName] = pos;
            }
            catch { }
        }
        _settings.Save();
    }

    private void RestoreClientPosition(IntPtr hwnd, string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        if (!_settings.CurrentProfile.ClientPositions.TryGetValue(characterName, out var pos)) return;

        try
        {
            // One placement call, not MoveWindow / SW_MAXIMIZE. SW_MAXIMIZE maximizes on
            // the monitor the window is CURRENTLY on, so a client saved maximized on
            // another monitor never moved there; and MoveWindow on a maximized or
            // minimized window doesn't change where it restores to. rcNormalPosition
            // picks the monitor for every state. It is in WORKSPACE coordinates, which
            // a top/left taskbar on the primary monitor offsets from the saved screen rect.
            var wp = new Interop.User32.WINDOWPLACEMENT
            {
                length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Interop.User32.WINDOWPLACEMENT>()
            };
            if (!Interop.User32.GetWindowPlacement(hwnd, ref wp)) return;

            var prim = System.Windows.Forms.Screen.PrimaryScreen!;
            int x = (int)pos.X - (prim.WorkingArea.Left - prim.Bounds.Left);
            int y = (int)pos.Y - (prim.WorkingArea.Top - prim.Bounds.Top);
            wp.rcNormalPosition = new Interop.DwmApi.RECT(x, y, x + (int)pos.Width, y + (int)pos.Height);

            bool max = pos.IsMaximized == 1;
            if (Interop.User32.IsIconic(hwnd))
            {
                // Leave minimized clients minimized; they restore onto the saved monitor.
                wp.showCmd = Interop.User32.SW_SHOWMINNOACTIVE;
                wp.flags = max ? Interop.User32.WPF_RESTORETOMAXIMIZED : 0;
            }
            else
            {
                wp.showCmd = (uint)(max ? Interop.User32.SW_MAXIMIZE : Interop.User32.SW_SHOWNOACTIVATE);
            }
            Interop.User32.SetWindowPlacement(hwnd, ref wp);
        }
        catch { }
    }

    /// <summary>Profile switch: move every running client to the NEW profile's saved
    /// position, which may be on another monitor. Restore used to run only when a
    /// client was first detected, so switching profiles never moved open clients.</summary>
    public void RestoreAllClientPositions()
    {
        foreach (var (hwnd, thumb) in _thumbnails)
            RestoreClientPosition(hwnd, thumb.CharacterName);
    }

    /// <summary>Move a freshly-discovered EVE client to a fixed spawn position —
    /// centered on its current monitor, or a user-defined desktop X/Y — so
    /// fixed-window clients don't all open in the top-left corner on large /
    /// ultrawide monitors (issue #85). Keeps the window's current size (SWP_NOSIZE).
    /// Skipped when the mode is Off or the window is maximized / minimized (nothing
    /// to place). Only reached when position tracking is disabled.</summary>
    /// <summary>Tracks whether we currently have any EVE client parked in the topmost
    /// band for "cover taskbar" mode, so turning the option off can put them back.</summary>
    private bool _taskbarCoverApplied;

    /// <summary>Client currently promoted into the topmost band for "cover taskbar",
    /// so a switch only has to demote that one and promote the new one (#100).</summary>
    private IntPtr _taskbarCoverTop = IntPtr.Zero;

    /// <summary>Raise the FOCUSED client above the taskbar and drop every other client
    /// back out of the topmost band (#99).
    ///
    /// The taskbar is a topmost window, so a client in the normal band is always drawn
    /// under it — SWP_NOZORDER elsewhere doesn't put it "below the taskbar", it just
    /// leaves the band alone. Only topmost membership can cover it, which is how
    /// ISBoxer/AHK achieve the same thing. Applied to the active client ONLY, and
    /// reversed on blur, so the taskbar and Start menu stay reachable the moment you
    /// switch away. Our thumbnails share the topmost band, so they are re-raised after.
    /// </summary>
    private void ApplyClientTaskbarCover(IntPtr fgHwnd)
    {
        bool enabled = _settings.Settings.ClientCoverTaskbar;
        if (!enabled && !_taskbarCoverApplied) return;   // nothing to do, nothing to undo

        if (!enabled)
        {
            // Option switched off: sweep every client out of the band once, or one
            // would stay stuck over the taskbar with nothing left to put it back.
            foreach (var (eveHwnd, _) in _thumbnails) SetTaskbarCoverBand(eveHwnd, false);
            _taskbarCoverApplied = false;
            _taskbarCoverTop = IntPtr.Zero;
            return;
        }

        if (_taskbarCoverTop == fgHwnd) return;   // already the promoted client

        // Demote the outgoing client BEFORE promoting the new one. Doing this only
        // from the focus poll was the #100 regression: the poll ignores the 500ms
        // after a cycle, so hotkey-switching left the previous client topmost and it
        // stayed visible over the client you just switched to.
        if (_taskbarCoverTop != IntPtr.Zero) SetTaskbarCoverBand(_taskbarCoverTop, false);
        _taskbarCoverTop = IntPtr.Zero;

        // Foreground may be our own app or a non-EVE window — then nothing is promoted
        // and the taskbar is reachable again, which is the intended blur behaviour.
        if (fgHwnd != IntPtr.Zero && _thumbnails.ContainsKey(fgHwnd))
        {
            SetTaskbarCoverBand(fgHwnd, true);
            _taskbarCoverTop = fgHwnd;
        }

        _taskbarCoverApplied = true;

        // Keep thumbnails above the promoted client — but off this path, so switching
        // stays O(1) no matter how many clients are open (#100).
        _raiseOverlaysDebounce?.Stop();
        _raiseOverlaysDebounce?.Start();
    }

    private static void SetTaskbarCoverBand(IntPtr hwnd, bool topmost)
    {
        try
        {
            Interop.User32.SetWindowPos(hwnd,
                topmost ? Interop.User32.HWND_TOPMOST : Interop.User32.HWND_NOTOPMOST,
                0, 0, 0, 0,
                Interop.User32.SWP_NOMOVE | Interop.User32.SWP_NOSIZE |
                Interop.User32.SWP_NOACTIVATE | Interop.User32.SWP_ASYNCWINDOWPOS);
        }
        catch { }
    }

    // ── Fixed client slots (ClientPositionMode = 3) ─────────────────

    /// <summary>
    /// Snap an EVE client into its fixed slot: the character's own slot, else the
    /// profile's default slot (see <see cref="ClientSlotRules"/>). The slot rect is the
    /// VISIBLE WINDOW — a Fixed Window (borderless) client fills it exactly; a windowed
    /// client's title bar and borders sit inside it. Clients sharing a slot stack on top
    /// of each other; the active one is brought forward by activation as usual.
    ///
    /// Safe by construction: no-op unless mode 3 is on and a slot resolves; the rect
    /// is validated against its monitor before any SetWindowPos; already-placed
    /// windows cost nothing (visible-frame early-out). Minimized / maximized windows
    /// get their restore rect set instead, so they land in the slot when restored.
    /// </summary>
    private void ApplyClientSlot(IntPtr hwnd, string? characterName, bool allowRecheck = true)
    {
        var s = _settings.Settings;
        if (s.ClientPositionMode != 3 || hwnd == IntPtr.Zero) return;

        var slot = ClientSlotRules.Resolve(_settings.CurrentProfile, characterName);
        if (slot == null) return;   // excluded, or no own/default slot: leave it alone

        var screen = FindSlotScreen(slot);
        if (screen == null)
        {
            DiagnosticsService.LogWindowHook(
                $"[ClientSlot] ⏭ '{characterName}': monitor {slot.MonitorDeviceName} ({slot.MonitorWidth}x{slot.MonitorHeight}) for slot '{slot.Name}' not found");
            return;
        }

        // Never send an absurd rect to the client (spike lesson: a bad height once went
        // out as 14,817,394 px). Slots must fit their monitor and be a usable size.
        var b = screen.Bounds;
        if (slot.Width < 640 || slot.Height < 360 || slot.Width > b.Width || slot.Height > b.Height
            || slot.X < 0 || slot.Y < 0 || slot.X + slot.Width > b.Width || slot.Y + slot.Height > b.Height)
        {
            DiagnosticsService.LogWindowHook(
                $"[ClientSlot] ⛔ slot '{slot.Name}' {slot.X},{slot.Y} {slot.Width}x{slot.Height} doesn't fit {screen.DeviceName} {b.Width}x{b.Height}");
            return;
        }

        // The slot is the VISIBLE window: a Fixed Window client fills it exactly; a
        // windowed client's title bar and borders sit inside it. SetWindowPos works on
        // the window rect, which for captioned windows also includes invisible resize
        // borders (~9 px left/right/bottom) — add those back so the VISIBLE frame lands
        // on the slot.
        int vx = b.Left + slot.X, vy = b.Top + slot.Y, vw = slot.Width, vh = slot.Height;
        bool resize = s.ClientSlotsResize;
        bool windowed = (Interop.User32.GetWindowLong(hwnd, Interop.User32.GWL_STYLE)
                         & Interop.User32.WS_CAPTION) == Interop.User32.WS_CAPTION;

        try
        {
            bool iconic = Interop.User32.IsIconic(hwnd);
            if (iconic || Interop.User32.IsZoomed(hwnd))
            {
                // Set the restore rect; SetWindowPos on a minimized/maximized window
                // doesn't change where it restores to. Insets can't be measured while
                // minimized, so use the last ones seen for this window (0 for Fixed
                // Window clients). rcNormalPosition is in WORKSPACE coordinates (offset
                // by a top/left taskbar on the primary monitor).
                _slotInsets.TryGetValue(hwnd, out var ins);
                var wp = new Interop.User32.WINDOWPLACEMENT
                {
                    length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Interop.User32.WINDOWPLACEMENT>()
                };
                if (!Interop.User32.GetWindowPlacement(hwnd, ref wp)) return;
                var prim = System.Windows.Forms.Screen.PrimaryScreen!;
                int wx = vx - ins.Left - (prim.WorkingArea.Left - prim.Bounds.Left);
                int wy = vy - ins.Top - (prim.WorkingArea.Top - prim.Bounds.Top);
                int ww = vw + ins.Left + ins.Right, wh = vh + ins.Top + ins.Bottom;
                if (!resize)
                {
                    ww = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                    wh = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
                }
                wp.rcNormalPosition = new Interop.DwmApi.RECT(wx, wy, wx + ww, wy + wh);
                // Minimized stays minimized (restores into the slot); maximized is
                // restored into the slot — a maximized client can't sit in one.
                wp.showCmd = iconic ? (uint)Interop.User32.SW_SHOWMINNOACTIVE : (uint)Interop.User32.SW_SHOWNOACTIVATE;
                wp.flags = 0;
                Interop.User32.SetWindowPlacement(hwnd, ref wp);
                DiagnosticsService.LogWindowHook(
                    $"[ClientSlot] 📌 '{characterName}' → '{slot.Name}' (restore rect, was {(iconic ? "minimized" : "maximized")})");
                return;
            }

            if (!Interop.DwmApi.TryGetVisibleFrame(hwnd, out var vis, out var insets, out bool measured)) return;

            // A windowed client's invisible borders can't be measured while it sits on a
            // monitor whose scaling differs from the primary's (DWM reports physical px,
            // this system-aware app sees scaled px). Use the borders last measured for it,
            // skip the early-out (the visible rect isn't trustworthy), and re-check once
            // after the move, when it's on the target monitor.
            bool reliable = measured || !windowed;   // Fixed Window clients have no invisible border
            if (measured) _slotInsets[hwnd] = insets;
            else if (windowed && _slotInsets.TryGetValue(hwnd, out var cached)) insets = cached;

            // Early-out: the visible frame is already exactly the slot.
            if (reliable)
            {
                bool posOk = vis.Left == vx && vis.Top == vy;
                bool sizeOk = vis.Right - vis.Left == vw && vis.Bottom - vis.Top == vh;
                if (posOk && (sizeOk || !resize)) return;
            }

            int tx = vx - insets.Left, ty = vy - insets.Top;
            int tw = vw + insets.Left + insets.Right, th = vh + insets.Top + insets.Bottom;
            uint flags = Interop.User32.SWP_NOZORDER | Interop.User32.SWP_NOACTIVATE | Interop.User32.SWP_ASYNCWINDOWPOS;
            if (!resize) flags |= Interop.User32.SWP_NOSIZE;
            Interop.User32.SetWindowPos(hwnd, IntPtr.Zero, tx, ty, tw, th, flags);
            DiagnosticsService.LogWindowHook(
                $"[ClientSlot] 📌 '{characterName}' → '{slot.Name}' visible {vx},{vy} {vw}x{vh} " +
                $"({(windowed ? "windowed" : "fixed")}, window rect {tx},{ty} {tw}x{th}, resize={resize}" +
                $"{(reliable ? "" : ", borders unmeasured")})");

            if (!reliable && allowRecheck)
                ScheduleSlotRecheck(hwnd, characterName);
        }
        catch (Exception ex)
        {
            DiagnosticsService.LogWindowHook($"[ClientSlot] ❌ '{characterName}': {ex.Message}");
        }
    }

    /// <summary>Apply the slot once more shortly after a move whose invisible borders
    /// couldn't be measured — by then the window is on the target monitor. One recheck
    /// only (allowRecheck: false), so this can never loop.</summary>
    private void ScheduleSlotRecheck(IntPtr hwnd, string? characterName)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_thumbnails.ContainsKey(hwnd))
                    ApplyClientSlot(hwnd, characterName, allowRecheck: false);
            };
            timer.Start();
        }));
    }

    /// <summary>Invisible resize-border insets last measured per client window, used to
    /// set the restore rect of a minimized/maximized windowed client.</summary>
    private readonly ConcurrentDictionary<IntPtr, Interop.DwmApi.RECT> _slotInsets = new();

    /// <summary>Find a slot's monitor: same device name and resolution, else the first
    /// monitor with that resolution (Windows can renumber DISPLAYn). Never falls back
    /// to a different-sized monitor — the slot would land somewhere unintended.</summary>
    private static System.Windows.Forms.Screen? FindSlotScreen(ClientSlot slot)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        bool hasSize = slot.MonitorWidth > 0 && slot.MonitorHeight > 0;
        bool SizeMatch(System.Windows.Forms.Screen sc) =>
            !hasSize || (sc.Bounds.Width == slot.MonitorWidth && sc.Bounds.Height == slot.MonitorHeight);

        return screens.FirstOrDefault(sc => sc.DeviceName == slot.MonitorDeviceName && SizeMatch(sc))
            ?? (hasSize ? screens.FirstOrDefault(SizeMatch) : null);
    }

    /// <summary>Snap every tracked client into its slot (tray "Snap to slots", profile
    /// switch). No-op unless Fixed slots mode is on.</summary>
    public void ApplyAllClientSlots()
    {
        if (_settings.Settings.ClientPositionMode != 3) return;
        foreach (var (hwnd, thumb) in _thumbnails)
            ApplyClientSlot(hwnd, thumb.CharacterName);
    }

    private void ApplyFixedClientPosition(IntPtr hwnd)
    {
        int mode = _settings.Settings.ClientPositionMode; // 0=Off, 1=Center, 2=Custom, 3=Fixed slots
        if (mode != 1 && mode != 2) return;   // 3 is handled by ApplyClientSlot
        if (Interop.User32.IsIconic(hwnd) || Interop.User32.IsZoomed(hwnd)) return;
        if (!Interop.User32.GetWindowRect(hwnd, out var rect)) return;

        int w = rect.Width, h = rect.Height;
        if (w <= 0 || h <= 0) return;

        int tx, ty;
        if (mode == 1)
        {
            // Centre within the WORK AREA, not Screen.Bounds (#96). Bounds includes the
            // taskbar strip, so centring in it pushes the window roughly half the taskbar
            // height too low — a windowed client then extends underneath the taskbar,
            // which is topmost and draws over it. WorkingArea excludes the taskbar (on
            // whichever edge it lives), so the client lands fully visible.
            // GetWindowRect + Screen bounds + SetWindowPos are all physical device
            // pixels, so no DPI conversion is needed here.
            // "Cover taskbar" mode (#99) deliberately uses the FULL screen: EVE's fixed
            // window list offers the monitor's exact resolution, and such a window is
            // taller than the work area — centring it there would push it down so the
            // taskbar clips the bottom. Screen bounds centres it flush with the display.
            var scr = System.Windows.Forms.Screen.FromHandle(hwnd);
            var wa = _settings.Settings.ClientCoverTaskbar ? scr.Bounds : scr.WorkingArea;
            tx = wa.Left + (wa.Width - w) / 2;
            ty = wa.Top + (wa.Height - h) / 2;

            // A window taller/wider than the area would still centre to a negative
            // offset and slide under the taskbar; pin it to the area origin instead.
            if (w > wa.Width) tx = wa.Left;
            if (h > wa.Height) ty = wa.Top;
        }
        else
        {
            tx = _settings.Settings.ClientPositionX;
            ty = _settings.Settings.ClientPositionY;
        }

        // Already there? Don't spend a cross-process SetWindowPos on it. This runs on
        // every client switch, and after the first switch the client is normally already
        // in position, so this skips the call entirely most of the time (#100 delay).
        if (rect.Left == tx && rect.Top == ty) return;

        try
        {
            Interop.User32.SetWindowPos(hwnd, IntPtr.Zero, tx, ty, 0, 0,
                Interop.User32.SWP_NOSIZE | Interop.User32.SWP_NOZORDER |
                Interop.User32.SWP_NOACTIVATE | Interop.User32.SWP_ASYNCWINDOWPOS);
        }
        catch { }
    }

    // ── Active Character Query ──────────────────────────────────────

    /// <summary>Get names of all currently active EVE characters with thumbnails.</summary>
    public IEnumerable<string> GetActiveCharacterNames()
    {
        return _thumbnails.Values
            .Select(t => t.CharacterName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct();
    }

    /// <summary>True when ANY EVE client is tracked — including ones still on the
    /// character-select screen (no character name yet). Hotkey activation keys off
    /// this rather than logged-in character names so cycle/hotkeys work as soon as
    /// clients are open, not only after a character logs in (issue #72).</summary>
    public bool HasTrackedClients() => !_thumbnails.IsEmpty;

    /// <summary>Get the EVE window handle for a given character name.</summary>
    public IntPtr GetHwndForCharacter(string characterName)
    {
        foreach (var (hwnd, thumb) in _thumbnails)
        {
            if (string.Equals(thumb.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>Every client window and its character ("" at the login screen), from any thread: a copy. App's probe runs on
    /// the process monitor's pool thread (IDEAS #22, #47).</summary>
    public IReadOnlyList<(IntPtr Window, string Character)> ClientWindows() =>
        _thumbnails.Select(kv => (kv.Key, kv.Value.CharacterName ?? "")).ToList();

    /// <summary>Shows on a client's main thumbnail what ClientWatch said of it, and (IDEAS P114) the same on its second
    /// thumbnail; its crop windows hear of it through <see cref="ClientMarkChanged"/>. A client at character select has
    /// neither. The window's thread.</summary>
    public void SetClientMark(IntPtr window, ClientMark mark)
    {
        if (_thumbnails.TryGetValue(window, out var thumb)) thumb.SetClientMark(mark);
        else return;
        var who = thumb.CharacterName ?? "";
        if (who.Length == 0) return;
        if (_secondaryThumbnails.TryGetValue(who, out var pip)) pip.SetClientMark(mark);
        ClientMarkChanged?.Invoke(who, mark);
    }

    /// <summary>IDEAS P114: a logged-in client's mark changed (its character, the mark). CropManager draws it on that
    /// character's crop windows. The window's thread.</summary>
    public event Action<string, ClientMark>? ClientMarkChanged;

    /// <summary>The mark a character's client has now, for a window made after it was set (a second thumbnail, a crop window).</summary>
    public ClientMark MarkOf(string character) => GetThumbnailForCharacter(character)?.Mark ?? ClientMark.None;

    /// <summary>The character of the client MultiPreview counts as active (the one it last switched to while that is still a
    /// client, else the window in front), or "" when that is no client with a character. IDEAS #35. The window's thread.</summary>
    public string ActiveCharacter() =>
        _thumbnails.TryGetValue(ActiveClient(), out var thumb) ? thumb.CharacterName ?? "" : "";

    /// <summary>IDEAS #35: holds the main thumbnails of these characters hidden and lets go of every other. A thumbnail let
    /// go shows only when nothing else hides it. The window's thread; a thumbnail whose hold stays is hidden again with its
    /// text: a settings Apply (opacity) or the not-logged-in mark shows the text overlay on its own, and this bounds that to
    /// one tick. Nothing is touched for a thumbnail that stays let go.</summary>
    public void HoldForFocus(IReadOnlySet<string> held)
    {
        _focusHeld = held;
        foreach (var (hwnd, thumb) in _thumbnails)
        {
            bool hold = _nameBarHold || (!string.IsNullOrEmpty(thumb.CharacterName) && held.Contains(thumb.CharacterName));   // IDEAS #38: focus mode OR the name bar
            if (hold == thumb.HeldHidden)
            {
                if (hold) thumb.HideWithOverlay();
                continue;
            }
            thumb.SetHeldHidden(hold);
            if (!hold && ShowsWhenLetGo(hwnd, thumb)) thumb.ShowWithOverlay();
        }
        // IDEAS P124: the second thumbnails and stats windows of the same characters, held the same way (they are keyed by
        // character); the crop windows hear of it through FocusHeldChanged. Letting go shows one only when its own
        // switches (Hide all, hide second thumbnails, hide primary) allow.
        foreach (var (name, pip) in _secondaryThumbnails)
        {
            bool hold = FocusMode.Holds(held, name);
            if (hold == pip.HeldHidden)
            {
                if (hold) pip.HideWithOverlay();
                continue;
            }
            pip.SetHeldHidden(hold);
            if (!hold && !_thumbnailsHidden && !_secondaryHidden) pip.ShowWithOverlay();
        }
        foreach (var (name, statWin) in _statWindows)
        {
            bool hold = FocusMode.Holds(held, name);
            if (hold == statWin.HeldHidden) continue;
            statWin.SetHeldHidden(hold);
            if (!hold && !_thumbnailsHidden && !_primaryHidden) statWin.Visibility = Visibility.Visible;
        }
        FocusHeldChanged?.Invoke(held);
    }

    /// <summary>The characters whose windows focus mode holds hidden now (IDEAS #35, P124); a window made later asks it.</summary>
    private IReadOnlySet<string> _focusHeld = FocusMode.Nobody;

    /// <summary>IDEAS #38: the name bar holds every main thumbnail hidden (it shows the clients as buttons instead). The same
    /// hold as focus mode's, so a thumbnail is held when focus mode OR the bar holds it and is let go only when neither does;
    /// ShowsWhenLetGo asks both. Crops, second thumbnails and stats windows are not touched. The window's thread.</summary>
    private bool _nameBarHold;

    /// <summary>IDEAS #38: the name bar holds the main thumbnails hidden (<paramref name="hold"/>), or lets go of them: those
    /// focus mode still holds stay held, the others show when nothing else hides them. A thumbnail made while the bar is on
    /// is held when it is made. The window's thread.</summary>
    public void SetNameBarHold(bool hold)
    {
        if (_nameBarHold == hold) return;
        _nameBarHold = hold;
        HoldForFocus(_focusHeld);
    }

    /// <summary>IDEAS P124: every look of focus mode, with the characters it holds. CropManager holds and lets go of their
    /// crop windows. The window's thread.</summary>
    public event Action<IReadOnlySet<string>>? FocusHeldChanged;

    /// <summary>Whether a thumbnail focus mode lets go of is shown: not while Hide all, Hide primary or hidden-while-alt-tabbed
    /// hides every thumbnail, not when the player hid this character on Clients › Characters, and not for the client in front
    /// while Hide the active thumbnail is on.</summary>
    private bool ShowsWhenLetGo(IntPtr hwnd, ThumbnailWindow thumb) =>
        !_thumbnailsHidden && !_primaryHidden && !_primaryHiddenByFocus && !_nameBarHold
        && !IsCharacterUserHidden(thumb.CharacterName ?? "")
        && !(_settings.Settings.HideActiveThumbnail && hwnd == Interop.User32.GetForegroundWindow());

    /// <summary>Get the ThumbnailWindow for a given character name, or null if not present.</summary>
    public ThumbnailWindow? GetThumbnailForCharacter(string characterName)
    {
        foreach (var thumb in _thumbnails.Values)
        {
            if (string.Equals(thumb.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                return thumb;
        }
        return null;
    }

    /// <summary>Snapshot the current on-screen bounds of every live primary thumbnail.
    /// Used by other windows (e.g. CropWindow) to snap against thumbnails.</summary>
    public IReadOnlyList<(double L, double T, double W, double H)> GetThumbnailBounds()
    {
        var list = new List<(double, double, double, double)>();
        foreach (var thumb in _thumbnails.Values)
            list.Add((thumb.Left, thumb.Top, thumb.Width, thumb.Height));
        return list;
    }

    // ── Visibility Toggles ──────────────────────────────────────────

    /// <summary>Fires after the global "Hide/Show All" toggle flips, with the new
    /// hidden state. CropManager subscribes so crops follow the keybind (issue #66).</summary>
    public event Action<bool>? AllVisibilityChanged;

    /// <summary>Fires when the dedicated "Hide/Show Crops" keybind is pressed.
    /// CropManager owns the crop hidden-state, so it handles the actual toggle
    /// (issue #66). Routed through here so HotkeyService needs no CropManager ref.</summary>
    public event Action? CropsVisibilityToggleRequested;

    /// <summary>Lift the thumbnails (and PiPs / stat overlays) back to the top of their
    /// z-order band.
    ///
    /// Crops are topmost too, and SetWindowPos(HWND_TOPMOST) doesn't just set the flag —
    /// it moves the window to the TOP of the topmost band, i.e. above the thumbnails. A
    /// crop is hit-testable, so a crop sitting over a thumbnail swallowed its click and
    /// you could no longer click a thumbnail to switch to that client. The thumbnails are
    /// the interactive surface; the crops are passive overlays, so the thumbnails must win.
    /// CropManager calls this right after it (re)asserts crop topmost.</summary>
    public void RaiseThumbnailsAboveOverlays()
    {
        foreach (var (_, thumb) in _thumbnails) thumb.BringToFront();
        foreach (var (_, pip) in _secondaryThumbnails) pip.BringToFront();
        foreach (var (_, sw) in _statWindows) sw.BringToFront();
        RaiseEnlargedAgain();
    }

    /// <summary>Behind every loop that raises all thumbnails (IDEAS #13, T6-I2): the thumbnails that are enlarged for
    /// an alert are raised once more. A loop raises them one by one, so every neighbour raised behind an enlarged
    /// thumbnail stood above it again, and "above its neighbours" lasted only until the next client switch (or a
    /// quarter of a second with "keep thumbnails above clients"). The raise is the window's own (raw SetWindowPos,
    /// no activation), and the window decides: only while it is enlarged, in the topmost band, and while raising
    /// is not held off for the settings window. One read per thumbnail while none is enlarged.</summary>
    private void RaiseEnlargedAgain()
    {
        if (!ThumbnailWindow.AnyEnlarged) return;   // FY-m4: one read while none is enlarged
        foreach (var (_, thumb) in _thumbnails) thumb.RaiseWhileEnlarged();
    }

    public void ToggleThumbnailVisibility()
    {
        _thumbnailsHidden = !_thumbnailsHidden;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                if (_thumbnailsHidden)
                    thumb.HideWithOverlay();
                else
                    thumb.ShowWithOverlay();
            }
            // AHK: also toggles secondary (PiP) thumbnails
            foreach (var (_, pip) in _secondaryThumbnails)
            {
                if (_thumbnailsHidden)
                    pip.HideWithOverlay();
                else
                    pip.ShowWithOverlay();
            }
            // Also toggle stat overlay windows
            foreach (var (_, sw) in _statWindows)
            {
                sw.Visibility = _thumbnailsHidden
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible;
            }
        });
        // Crops follow the Hide/Show All keybind (issue #66).
        AllVisibilityChanged?.Invoke(_thumbnailsHidden);
        ShowTooltipFeedback(_thumbnailsHidden
            ? LocalizationService.Str("L.Msg.ThumbsHidden", "Thumbnails: Hidden")
            : LocalizationService.Str("L.Msg.ThumbsVisible", "Thumbnails: Visible"));
    }

    /// <summary>Alias for ToggleThumbnailVisibility — matches AHK HideShowThumbnailsHotkey.</summary>
    public void ToggleAllVisibility() => ToggleThumbnailVisibility();

    /// <summary>Dedicated "Hide/Show Crops" keybind entry point (issue #66). The
    /// crop hidden-state lives in CropManager, which subscribes to this.</summary>
    public void ToggleCropsVisibility() => CropsVisibilityToggleRequested?.Invoke();

    public void TogglePrimaryVisibility()
    {
        _primaryHidden = !_primaryHidden;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                if (_primaryHidden)
                    thumb.HideWithOverlay();
                else
                    thumb.ShowWithOverlay();
            }
            // Also toggle stat overlay windows with primary
            foreach (var (_, sw) in _statWindows)
            {
                sw.Visibility = _primaryHidden
                    ? System.Windows.Visibility.Collapsed
                    : System.Windows.Visibility.Visible;
            }
        });
        ShowTooltipFeedback(_primaryHidden
            ? LocalizationService.Str("L.Msg.PrimaryHidden", "Primary: Hidden")
            : LocalizationService.Str("L.Msg.PrimaryVisible", "Primary: Visible"));
    }

    private bool _secondaryHidden = false;

    /// <summary>True while "hide second thumbnails" has them hidden (runtime state, not saved). The tray menu's check.</summary>
    public bool SecondaryThumbnailsHidden => _secondaryHidden;

    /// <summary>Toggle visibility of secondary (PiP) thumbnails only.</summary>
    public void ToggleSecondaryVisibility()
    {
        _secondaryHidden = !_secondaryHidden;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var (_, pip) in _secondaryThumbnails)
            {
                if (_secondaryHidden)
                    pip.HideWithOverlay();
                else
                    pip.ShowWithOverlay();
            }
        });
        Debug.WriteLine($"[Thumbnail:Visibility] 🔧 ToggleSecondaryVisibility: hidden={_secondaryHidden}, count={_secondaryThumbnails.Count}");
        ShowTooltipFeedback(_secondaryHidden
            ? LocalizationService.Str("L.Msg.PipHidden", "PiP: Hidden")
            : LocalizationService.Str("L.Msg.PipVisible", "PiP: Visible"));
    }

    // ── Back to the client before this one (IDEAS #56) ──────────────

    private readonly ClientHistory _clientHistory = new();

    /// <summary>A window that came to the front is noted when it is a client. A browser in front is not: from
    /// another program, "back" is the client the player was in last. A client that takes the front before it has a
    /// thumbnail (just started, or logged in again) is no client yet when the focus sweep sees it, and the sweep
    /// does not look again until the window in front changes: it is noted where its thumbnail is made.</summary>
    private void NoteClientInFront(IntPtr hwnd)
    {
        if (_thumbnails.ContainsKey(hwnd)) _clientHistory.Front(hwnd, Environment.TickCount64);
    }

    /// <summary>The client MultiPreview counts as active: the one it last switched to while that is still a client
    /// (the cycle order reads the same, so a switch still in flight is not asked of Windows), else the window in front.</summary>
    private IntPtr ActiveClient() =>
        _lastActiveEveHwnd != IntPtr.Zero && _thumbnails.ContainsKey(_lastActiveEveHwnd)
            ? _lastActiveEveHwnd
            : Interop.User32.GetForegroundWindow();

    /// <summary>A client MultiPreview may bring to the front: it has a thumbnail and its window still exists. A client
    /// that is closing keeps its thumbnail until the next poll notices; its window is gone before that.</summary>
    private bool ClientAlive(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && _thumbnails.ContainsKey(hwnd) && Interop.User32.IsWindow(hwnd);

    /// <summary>The "Back to the client before this one" hotkey (IDEAS #56): the client used before the active one,
    /// or, from another program, the client used last. With nothing to go back to it says so near the cursor, as the
    /// other hotkeys' feedback does, and switches nothing.</summary>
    public void SwitchToPreviousClient()
    {
        var active = ActiveClient();
        var target = _clientHistory.Back(active, _thumbnails.ContainsKey(active), Environment.TickCount64, ClientAlive);
        if (target == IntPtr.Zero)
        {
            ShowTooltipFeedback(LocalizationService.Str("L.Switch.NoPrevious", "No other client to go back to"));
            return;
        }
        ActivateEveWindow(target, _thumbnails.TryGetValue(target, out var thumb) ? thumb.CharacterName : null);
    }

    // ── Go to the last alert (IDEAS #17) ────────────────────────────

    private readonly AlertWalk _alertWalk = new();

    /// <summary>This session's alerts, newest first (App sets it to AlertHistory.Recent). The "Go to the last alert"
    /// hotkey walks back through them once nothing is unseen.</summary>
    public Func<IReadOnlyList<AlertHistoryEntry>>? RecentAlerts { get; set; }

    /// <summary>The "Go to the last alert" hotkey (IDEAS #17): the client of the most severe alert the player has
    /// not looked at; pressed again, the next; with none left, back through this session's alerts (AlertWalk). Never
    /// a client that is gone or closing. With nothing to go to it says so near the cursor and switches nothing; and
    /// when the last alert is on the client in front, moments ago, it says that and stays (AlertWalk.Kind.Here).</summary>
    public void GoToLastAlert()
    {
        var active = ActiveClient();
        var foreground = Interop.User32.GetForegroundWindow();
        string? activeChar = _thumbnails.TryGetValue(active, out var front) ? front.CharacterName : null;
        // An alert of the active client that is still unseen although that client is the window in front (it
        // arrived while a switch was in flight) is looked at now: the player is on that client. Left unseen, the
        // walk would name this client, and the press would switch nothing and say nothing. When the active client
        // is not in front (the tray menu, a drag, a switch Windows refused), its alert stays unseen: the walk names
        // it, and the key goes there.
        SeeClientInFront(Attention, activeChar, active, foreground);
        // And only a client that really is in front is "this client" for the walk (IDEAS P78 (4)).
        string? inFront = CharacterInFront(activeChar, active, foreground);
        var step = _alertWalk.Next(Attention.Unseen(),
            () => RecentAlerts?.Invoke() ?? Array.Empty<AlertHistoryEntry>(),
            name => ClientAlive(GetHwndForCharacter(name)), inFront, DateTime.UtcNow);
        if (!step.Goes)
        {
            ShowTooltipFeedback(step.Kind switch
            {
                AlertWalk.Kind.Here => LocalizationService.Str("L.Switch.LastAlertHere", "The last alert is on this client"),
                AlertWalk.Kind.NoEarlier => LocalizationService.Str("L.Switch.NoEarlierAlert", "No earlier alert"),
                _ => LocalizationService.Str("L.Switch.NoAlert", "No alert to go to"),
            });
            return;
        }
        // A client that is the window in front already is looked at now: ActivateEveWindow returns early for it, and
        // no change of the foreground window will follow. Any other client's alert stays unseen until that client
        // really comes to the front (the focus sweep): a switch Windows refuses leaves it as it was (IDEAS P78 (1)).
        SeeClientInFront(Attention, step.Character, GetHwndForCharacter(step.Character), foreground);
        ActivateEveWindow(IntPtr.Zero, step.Character);
    }

    // ── Cycle filters (IDEAS #40) ───────────────────────────────────

    /// <summary>The switches as the last press of a cycle key found them.</summary>
    private CycleFilterOptions _cycleSwitches;

    /// <summary>Whether any cycle filter is switched on: all a cycle press pays when none is (the three switches read
    /// once, and compared with what the last press found). When they have changed, all off included, what the keys
    /// remember starts afresh: both walks are forgotten, and the note is said again when it next has to be. With
    /// the filters off the filter step is not run, so this is the one place that sees them go off.</summary>
    private bool CycleFiltersOn
    {
        get
        {
            var now = CycleFilterOptions.From(_settings.Settings);
            if (CycleFilter.Changed(ref _cycleSwitches, now))
            {
                _groupCycleWalk.Forget();
                _allCycleWalk.Forget();
            }
            return now.Any;
        }
    }

    /// <summary>What the cycle keys remember between presses (CycleFilter.Walk): one for the keys of groups, which go
    /// by character, and one for the two keys that go through all running clients, which go by window. A forward
    /// key and its backward key share one, so the two mirror each other in a walk through all clients as well.</summary>
    private readonly CycleFilter.Walk<string> _groupCycleWalk = new(StringComparer.OrdinalIgnoreCase);
    private readonly CycleFilter.Walk<IntPtr> _allCycleWalk = new();

    /// <summary>Narrows a group cycle's characters to those that pass the filters that are on (CycleFilter.Press).</summary>
    private void FilterCycle(List<string> names, string? activeChar)
    {
        if (CycleFilter.Press(names, CycleFilterOptions.From(_settings.Settings),
                isActive: name => !string.IsNullOrEmpty(activeChar) && string.Equals(name, activeChar, StringComparison.OrdinalIgnoreCase),
                activeName: activeChar, nameOf: name => name,
                systemOf: GetCharacterSystem, alerting: Attention.Has, muted: IsCharacterAlertMuted, walk: _groupCycleWalk))
            SayCycleFellBack();
    }

    /// <summary>The same for the cycle through all running clients, which is a list of windows.</summary>
    private void FilterCycle(List<IntPtr> hwnds, IntPtr active)
    {
        string NameOf(IntPtr hwnd) => _thumbnails.TryGetValue(hwnd, out var thumb) ? thumb.CharacterName ?? "" : "";
        if (CycleFilter.Press(hwnds, CycleFilterOptions.From(_settings.Settings),
                isActive: hwnd => hwnd == active, activeName: NameOf(active), nameOf: NameOf,
                systemOf: GetCharacterSystem, alerting: Attention.Has, muted: IsCharacterAlertMuted, walk: _allCycleWalk))
            SayCycleFellBack();
    }

    /// <summary>A filter that left no other client is said once, near the cursor: the key then cycles through all
    /// clients, and the player should know why it went where it went. Posted: the note is a window of its own, and
    /// the switch this press makes must not wait for it to be built.</summary>
    private void SayCycleFellBack() =>
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowTooltipFeedback(
            LocalizationService.Str("L.Switch.CycleFilterEmpty", "No other client matches the cycle filter: switching through all of them"))));

    private DateTime _lastCycleTime = DateTime.MinValue;

    /// <summary>Fired by CycleGroup / CycleAll whenever a cycle hotkey wraps
    /// from the last active client back to the first (or first to last when
    /// reversing). App.xaml.cs subscribes to play the cycle-wrap sound (#24).</summary>
    public event Action? CycleWrapped;

    /// <summary>Cycle through characters in a hotkey group.</summary>
    public void CycleGroup(string groupName, List<string> members, bool forward)
    {
        if (members == null || members.Count == 0) return;

        // Cadence is governed upstream by HotkeyService: one fire per press, plus a
        // held-key repeat timer that paces at CycleDelayMs (issues #59/#60). No
        // CycleDelayMs throttle here, or it would swallow deliberate fast taps.
        _lastCycleTime = DateTime.UtcNow;

        void LogCycle(string msg)
        {
            EveMultiPreview.Services.DiagnosticsService.LogCycling(msg);
        }

        // Filter to only online characters using HashSet for O(M+N) instead of O(M×N).
        // Also drop anyone the user has shift-click-excluded this session (issue #16).
        var onlineSet = new HashSet<string>(
            _thumbnails.Values.Select(t => t.CharacterName),
            StringComparer.OrdinalIgnoreCase);
        var onlineMembers = members
            .Where(m => onlineSet.Contains(m) && !_excludedFromCycle.ContainsKey(m))
            .ToList();
        
        LogCycle($"[CycleGroup] 🔄 Group '{groupName}' cycle {(forward ? "FWD" : "BWD")} requested. Target group has {members.Count} members. Currently Online: {onlineMembers.Count}");

        if (onlineMembers.Count == 0) return;

        // Priority 1: Use our internally tracked _lastActiveEveHwnd. This prevents OS-level DirectX focus races from confusing the cycle order when hotkeys are spammed.
        // Priority 2: Fallback to the literal OS foreground window if our tracker is uninitialized.
        var fgHwnd = _lastActiveEveHwnd != IntPtr.Zero && _thumbnails.ContainsKey(_lastActiveEveHwnd)
                     ? _lastActiveEveHwnd
                     : Interop.User32.GetForegroundWindow();
        
        int currentIdx = -1;
        string? activeChar = null;

        // O(1) lookup instead of iterating all thumbnails
        if (_thumbnails.TryGetValue(fgHwnd, out var activeThumbnail))
            activeChar = activeThumbnail.CharacterName;

        // IDEAS #40: the one filter step. With no filter switched on this is three reads, one comparison with what the
        // last press found, and nothing else.
        if (CycleFiltersOn) FilterCycle(onlineMembers, activeChar);

        if (activeChar != null)
        {
            for (int i = 0; i < onlineMembers.Count; i++)
            {
                if (string.Equals(onlineMembers[i], activeChar, StringComparison.OrdinalIgnoreCase))
                {
                    currentIdx = i;
                    break;
                }
            }
        }

        // From outside the list (currentIdx -1) the forward key goes to the first client and the backward key to the
        // last (IDEAS P79 (4)).
        int nextIdx = CycleStep.Next(currentIdx, onlineMembers.Count, forward);
        bool wrapped;
        if (forward)
        {
            // Wrap: forward cycle landed on index 0 from the end of the list.
            // Skip if currentIdx was -1 (no prior active) or onlineMembers.Count == 1.
            wrapped = currentIdx > 0 && nextIdx == 0 && onlineMembers.Count > 1;
        }
        else
        {
            wrapped = currentIdx >= 0 && currentIdx < onlineMembers.Count - 1
                && nextIdx == onlineMembers.Count - 1 && onlineMembers.Count > 1;
        }

        if (wrapped) CycleWrapped?.Invoke();

        string charName = onlineMembers[nextIdx];

        // Update _lastActiveEveHwnd immediately so rapid subsequent cycles
        // (holding key down) don't get stale data from the 100ms timer
        IntPtr targetHwnd = IntPtr.Zero;
        foreach (var (hwnd, thumb) in _thumbnails)
        {
            if (string.Equals(thumb.CharacterName, charName, StringComparison.OrdinalIgnoreCase))
            {
                targetHwnd = hwnd;
                _lastActiveEveHwnd = hwnd;
                break;
            }
        }

        LogCycle($"[CycleGroup] ➡️ Activating '{charName}' (HWND: {targetHwnd}). Online Members in pool: {string.Join(", ", onlineMembers)}");
        ActivateEveWindow(targetHwnd, charName, rapidSwitch: true);
    }

    /// <summary>Cycle across every tracked client regardless of profile or group
    /// membership (issue #9). Keyed on HWND so it can include login-screen
    /// windows (empty CharacterName) when IncludeLoginScreensInCycle is on
    /// (issue #8). Shift-excluded characters (#16) are always skipped.</summary>
    public void CycleAll(bool forward)
    {
        // Cadence governed upstream by HotkeyService (issues #59/#60) — no
        // CycleDelayMs throttle here, which would block deliberate fast taps.
        bool includeLogins = _settings.Settings.IncludeLoginScreensInCycle;
        var hwnds = _thumbnails
            .OrderBy(kv => kv.Key.ToInt64())
            .Where(kv =>
            {
                var name = kv.Value.CharacterName;
                if (_excludedFromCycle.ContainsKey(name ?? "")) return false;
                bool isLogin = string.IsNullOrEmpty(name);
                return isLogin ? includeLogins : true;
            })
            .Select(kv => kv.Key)
            .ToList();

        if (hwnds.Count == 0) return;
        _lastCycleTime = DateTime.UtcNow;

        var fgHwnd = _lastActiveEveHwnd != IntPtr.Zero && _thumbnails.ContainsKey(_lastActiveEveHwnd)
                     ? _lastActiveEveHwnd
                     : Interop.User32.GetForegroundWindow();

        // IDEAS #40: the one filter step. With no filter switched on this is three reads, one comparison with what the
        // last press found, and nothing else.
        if (CycleFiltersOn) FilterCycle(hwnds, fgHwnd);

        int currentIdx = hwnds.IndexOf(fgHwnd);
        int nextIdx = CycleStep.Next(currentIdx, hwnds.Count, forward);

        bool wrapped = forward
            ? currentIdx > 0 && nextIdx == 0 && hwnds.Count > 1
            : currentIdx >= 0 && currentIdx < hwnds.Count - 1
                && nextIdx == hwnds.Count - 1 && hwnds.Count > 1;
        if (wrapped) CycleWrapped?.Invoke();

        var targetHwnd = hwnds[nextIdx];
        _lastActiveEveHwnd = targetHwnd;

        string targetTitle = _thumbnails.TryGetValue(targetHwnd, out var tThumb) ? tThumb.CharacterName : "";
        ActivateEveWindow(targetHwnd, targetTitle, rapidSwitch: true);
    }

    /// <summary>Push a new label text and per-label style override to the live
    /// thumbnail for a character (v2.0.6). Caller is responsible for updating
    /// persisted settings; this only drives the UI for immediate feedback.</summary>
    public void UpdateCharacterLabel(string characterName, string? labelText, string? colorHex, int sizePt)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                if (!string.Equals(thumb.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                    continue;
                thumb.UpdateAnnotation(string.IsNullOrWhiteSpace(labelText) ? null : labelText);
                thumb.SetLabelStyle(colorHex, sizePt);
            }
        }));
    }

    /// <summary>Show or hide the thumbnail for a specific character (issue #21).
    /// Mirrors what the Settings visibility checkbox expresses; does not modify
    /// any persisted settings itself — caller is responsible for updating
    /// S.ThumbnailVisibility and calling SaveDelayed.</summary>
    public void SetCharacterVisibility(string characterName, bool visible)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                if (!string.Equals(thumb.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (visible) thumb.ShowWithOverlay();
                else thumb.HideWithOverlay();
            }
        }));
    }

    // ── Session cycle-exclusion (issue #16) ─────────────────────────

    /// <summary>True when the user has shift-click-excluded this character
    /// from cycle rotations this session.</summary>
    public bool IsExcludedFromCycle(string characterName)
    {
        return !string.IsNullOrEmpty(characterName)
            && _excludedFromCycle.ContainsKey(characterName);
    }

    /// <summary>The system this character was last seen in (from its logs), or null.</summary>
    public string? GetCharacterSystem(string characterName) =>
        !string.IsNullOrEmpty(characterName) && _charSystems.TryGetValue(characterName, out var system) ? system : null;

    /// <summary>True while "Hide/Show all" has the thumbnails hidden (runtime state, not saved).</summary>
    public bool AllThumbnailsHidden => _thumbnailsHidden;

    /// <summary>True while the click-through toggle is on (runtime state, not saved).</summary>
    public bool ClickThroughOn => _clickThroughActive;

    /// <summary>Flip session cycle-exclusion for a character. Also forces a
    /// repaint of the matching thumbnail so the visual indicator updates.</summary>
    public void ToggleCycleExclusion(string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        bool nowExcluded;
        if (_excludedFromCycle.TryRemove(characterName, out _))
        {
            nowExcluded = false;
        }
        else
        {
            _excludedFromCycle[characterName] = 0;
            nowExcluded = true;
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                if (string.Equals(thumb.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                {
                    thumb.SetCycleExcluded(nowExcluded);
                }
            }
        }));

        ShowTooltipFeedback(nowExcluded
            ? string.Format(LocalizationService.Str("L.Msg.CycleExcluded", "'{0}' excluded from cycle"), StreamerMask.Name(StreamerKind.Character, characterName))
            : string.Format(LocalizationService.Str("L.Msg.CycleIncluded", "'{0}' re-included in cycle"), StreamerMask.Name(StreamerKind.Character, characterName)));
    }

    // ── Click-Through Toggle ────────────────────────────────────────

    public void ToggleClickThrough()
    {
        _clickThroughActive = !_clickThroughActive;
        ApplyClickThroughStateToAll();
        ShowTooltipFeedback(_clickThroughActive
            ? LocalizationService.Str("L.Msg.ClickThroughOn", "Thumbnails: Click-Through ON")
            : LocalizationService.Str("L.Msg.ClickThroughOff", "Thumbnails: Click-Through OFF"));
    }

    /// <summary>
    /// Forces all thumbnails click-through while the Settings UI is open so
    /// clicks pass through them to Settings regardless of z-order. Independent
    /// of the user-controlled <see cref="ToggleClickThrough"/> toggle — if either
    /// is active, the thumbnails are click-through.
    /// </summary>
    public void SetSettingsClickSuppression(bool suppressed)
    {
        if (_settingsClickSuppressed == suppressed) return;
        _settingsClickSuppressed = suppressed;
        ApplyClickThroughStateToAll();
    }

    private void ApplyClickThroughStateToAll()
    {
        bool transparent = _clickThroughActive || _settingsClickSuppressed;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
                ApplyClickThroughExStyle(thumb, transparent);
            foreach (var (_, pip) in _secondaryThumbnails)
                ApplyClickThroughExStyle(pip, transparent);
        });
    }

    private static void ApplyClickThroughExStyle(ThumbnailWindow window, bool transparent)
    {
        if (!window.IsHandleCreated) return;
        var hwnd = window.Handle;
        int exStyle = Interop.User32.GetWindowLong(hwnd, Interop.User32.GWL_EXSTYLE);
        int newStyle = transparent
            ? exStyle | Interop.User32.WS_EX_TRANSPARENT | Interop.User32.WS_EX_LAYERED
            : exStyle & ~Interop.User32.WS_EX_TRANSPARENT;
        if (newStyle != exStyle)
            Interop.User32.SetWindowLong(hwnd, Interop.User32.GWL_EXSTYLE, newStyle);
    }

    // ── Lock Positions ──────────────────────────────────────────────

    public void ToggleLockPositions()
    {
        var s = _settings.Settings;
        s.LockPositions = !s.LockPositions;
        _settings.Save();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
                thumb.IsLocked = s.LockPositions;
            foreach (var (_, pip) in _secondaryThumbnails)
                pip.IsLocked = s.LockPositions;
            foreach (var (_, sw) in _statWindows)
                sw.LockPositions = s.LockPositions;
        });
        // M9: Tooltip feedback matching AHK
        ShowTooltipFeedback(s.LockPositions
            ? LocalizationService.Str("L.Msg.PositionsLocked", "Positions: Locked")
            : LocalizationService.Str("L.Msg.PositionsUnlocked", "Positions: Unlocked"));
    }

    // ── Close All EVE Windows ───────────────────────────────────────

    public void CloseAllEveWindows()
    {
        foreach (var (eveHwnd, _) in _thumbnails)
        {
            Interop.User32.PostMessage(eveHwnd, Interop.User32.WM_SYSCOMMAND,
                (IntPtr)Interop.User32.SC_CLOSE, IntPtr.Zero);
        }
    }

    /// <summary>Create or re-create a secondary (PiP) thumbnail for a character from saved settings.</summary>
    public void CreateSecondaryForCharacter(string charName)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_secondaryThumbnails.ContainsKey(charName)) return;

            // Get SecondaryThumbnailSettings from current profile
            if (!_settings.Settings.SecondaryThumbnails.TryGetValue(charName, out var secSettings) ||
                secSettings.Enabled == 0)
            {
                Debug.WriteLine($"[PiP:Create] ⚠ No enabled PiP settings for '{charName}'");
                return;
            }

            // Find the EVE HWND from existing primary thumbnails
            IntPtr eveHwnd = IntPtr.Zero;
            foreach (var (hwnd, thumb) in _thumbnails)
            {
                if (string.Equals(thumb.CharacterName, charName, StringComparison.OrdinalIgnoreCase))
                {
                    eveHwnd = hwnd;
                    break;
                }
            }

            if (eveHwnd == IntPtr.Zero)
            {
                Debug.WriteLine($"[PiP:Create] ⚠ No active EVE window for '{charName}' — PiP will be created when they log in");
                return;
            }

            CreateSecondaryThumbnail(charName, eveHwnd, secSettings);
        });
    }

    /// <summary>Destroy a secondary (PiP) thumbnail for a specific character.</summary>
    public void DestroySecondaryForCharacter(string charName)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_secondaryThumbnails.TryRemove(charName, out var pip))
            {
                UnwireMenu(pip);
                pip.Close();
                Debug.WriteLine($"[PiP:Destroy] 🗑 Destroyed PiP for '{charName}'");
            }
        });
    }

    // ── Apply Size To All ───────────────────────────────────────────

    public void ApplyNewSize(int width, int height)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var (_, thumb) in _thumbnails)
            {
                thumb.Resize(width, height);
                if (!string.IsNullOrEmpty(thumb.CharacterName))
                    _settings.SaveThumbnailPosition(thumb.CharacterName,
                        (int)thumb.Left, (int)thumb.Top, width, height);
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// <summary>
    /// Guarantee a thumbnail is created at least partially visible. If the
    /// computed/saved position lands FULLY outside every connected monitor's work
    /// area — e.g. a default placed on a Preferred Monitor that isn't where the
    /// user is looking (issue #74), a saved position from a monitor that has since
    /// been disconnected or renumbered, or column-overflow off a screen edge — clamp
    /// it back onto the primary screen so it can't silently render off-screen
    /// ("character is tracked, settings work, but no thumbnail shows").
    /// All coordinates are physical pixels, matching Screen.WorkingArea.
    /// </summary>
    private static (int X, int Y) EnsureOnScreen(int x, int y, int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        var rect = new System.Drawing.Rectangle(x, y, w, h);

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            if (screen.WorkingArea.IntersectsWith(rect))
                return (x, y); // at least partially visible — leave the user's placement alone
        }

        // Fully off every screen → anchor onto the primary work area.
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        int nx = w >= wa.Width ? wa.Left : Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        int ny = h >= wa.Height ? wa.Top : Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        Debug.WriteLine($"[Thumbnail:OnScreen] ⛑ Clamped off-screen thumbnail ({x},{y}) → ({nx},{ny})");
        return (nx, ny);
    }

    /// Returns the working area (excludes taskbar) for the preferred monitor.
    /// Falls back to the primary screen if the index is out of range.
    /// </summary>
    private static System.Drawing.Rectangle GetTargetMonitorWorkArea(int preferredMonitor)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        int idx = preferredMonitor - 1; // Settings stores 1-based index
        if (idx >= 0 && idx < screens.Length)
            return screens[idx].WorkingArea;
        return System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
               ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
    }

    public static Color ParseColor(string hex)
    {
        try
        {
            if (string.IsNullOrEmpty(hex)) return Color.FromRgb(80, 80, 80);

            hex = hex.TrimStart('#');
            // Handle AHK "0x" prefix format
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return Color.FromRgb(80, 80, 80);
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        if (title.StartsWith("EVE - ", StringComparison.OrdinalIgnoreCase))
            return title[6..].Trim();
        if (title.Equals("EVE", StringComparison.OrdinalIgnoreCase))
            return "";
        return title;
    }

    private void UnwireEvents(ThumbnailWindow thumb)
    {
        thumb.PositionChanged -= OnThumbnailPositionChanged;
        thumb.SwitchRequested -= OnSwitchRequested;
        thumb.MinimizeRequested -= OnMinimizeRequested;
        thumb.DragMoveAll -= OnDragMoveAll;
        thumb.ResizeAll -= OnResizeAll;
        thumb.CycleExclusionRequested -= OnCycleExclusionRequested;
        UnwireMenu(thumb);
    }

    private void OnCycleExclusionRequested(ThumbnailWindow thumb)
    {
        ToggleCycleExclusion(thumb.CharacterName);
    }

    /// <summary>The right-click menu's five actions and the state it shows (system, game volume, mute), for a thumbnail
    /// and a second thumbnail alike (IDEAS P36).</summary>
    private void WireMenu(ThumbnailWindow thumb)
    {
        thumb.LabelEditRequested += OnLabelEditRequested;
        thumb.LegendWanted += OnLegendWanted;   // IDEAS #37
        thumb.LegendRefreshWanted += OnLegendRefresh;
        thumb.AlertMuteRequested += OnAlertMuteRequested;
        thumb.AudioRequested += OnAudioRequested;
        thumb.CloseClientRequested += OnCloseClientRequested;
        thumb.EndFrozenRequested += OnEndFrozenRequested;
        thumb.SettingsRequested += OnSettingsRequested;
        thumb.SnoozeEndRequested += OnSnoozeEndRequested;
        thumb.SnoozeLookup = SnoozesOf;
        thumb.SystemLookup = GetCharacterSystem;
        thumb.MuteCheck = IsCharacterAlertMuted;   // IDEAS P98 (4): its menu says "muted" by this rule
        thumb.AudioVolume = GetClientVolume(thumb.CharacterName);
        // A new thumbnail picks up any active mute for its character (e.g. relaunch).
        if (!string.IsNullOrEmpty(thumb.CharacterName)
            && _alertMutedChars.TryGetValue(thumb.CharacterName, out var mute))
            thumb.AlertMutedUntil = mute.Until;
    }

    /// <summary>Undoes <see cref="WireMenu"/>, so a destroyed second thumbnail leaves nothing subscribed to the manager.</summary>
    private void UnwireMenu(ThumbnailWindow thumb)
    {
        thumb.LabelEditRequested -= OnLabelEditRequested;
        thumb.LegendWanted -= OnLegendWanted;   // IDEAS #37
        thumb.LegendRefreshWanted -= OnLegendRefresh;
        if (ReferenceEquals(_legendFor, thumb)) HideIndicatorLegend();   // a thumbnail that goes while its legend shows takes it away
        thumb.AlertMuteRequested -= OnAlertMuteRequested;
        thumb.AudioRequested -= OnAudioRequested;
        thumb.CloseClientRequested -= OnCloseClientRequested;
        thumb.EndFrozenRequested -= OnEndFrozenRequested;
        thumb.SettingsRequested -= OnSettingsRequested;
        thumb.SnoozeEndRequested -= OnSnoozeEndRequested;
        thumb.SnoozeLookup = null;
        thumb.SystemLookup = null;
        thumb.MuteCheck = null;
    }

    private void OnLabelEditRequested(ThumbnailWindow thumb)
    {
        if (string.IsNullOrEmpty(thumb.CharacterName)) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = new Views.LabelEditorWindow(
                thumb.CharacterName, _settings.Settings, this,
                onSaved: () => _settings.SaveDelayed());

            // Pick a sensible owner so the dialog centers and z-orders nicely
            // without stealing activation from EVE clients behind it.
            var owner = Application.Current?.Windows
                .OfType<System.Windows.Window>()
                .FirstOrDefault(w => w.IsLoaded && w.IsVisible);
            if (owner != null) editor.Owner = owner;

            editor.Topmost = true;
            editor.ShowDialog();
        }));
    }

    /// <summary>Set the stat tracker service for stat window updates.</summary>
    public void SetStatTracker(StatTrackerService tracker)
    {
        _statTracker = tracker;
        Debug.WriteLine("[StatWindow:Init] 🔧 StatTrackerService connected");
    }

    // ── Secondary PiP Thumbnail Lifecycle ────────────────────────────

    private void CreateSecondaryThumbnail(string charName, IntPtr eveHwnd, Models.SecondaryThumbnailSettings secSettings)
    {
        if (_secondaryThumbnails.ContainsKey(charName)) return;

        try
        {
            int x = secSettings.X, y = secSettings.Y;
            int w = secSettings.Width > 0 ? secSettings.Width : 160;
            int h = secSettings.Height > 0 ? secSettings.Height : 120;

            var pip = new ThumbnailWindow();
            pip.Initialize(eveHwnd, charName, w, h, x, y);
            pip.SetOpacity((byte)secSettings.Opacity);
            pip.SetBorder(Color.FromArgb(0, 0, 0, 0), 0); // No borders on PiP
            pip.SetTextOverlayVisible(true); // Show character name only (system/timer stay collapsed)
            pip.IsLocked = _settings.Settings.LockPositions;

            pip.PositionChanged += (thumb, px, py, pw, ph) =>
            {
                if (_settings.Settings.SecondaryThumbnails.TryGetValue(charName, out var ss))
                {
                    ss.X = px; ss.Y = py; ss.Width = pw; ss.Height = ph;
                    _settings.SaveDelayed();
                }
            };
            // Its right-click menu does what the main thumbnail's does (IDEAS P36: it raised events nobody handled).
            WireMenu(pip);

            pip.Show();
            pip.SetHeldHidden(FocusMode.Holds(_focusHeld, charName));   // IDEAS P124: made while focus mode holds its character
            var mark = MarkOf(charName);                                // IDEAS P114: and while its client is marked
            if (mark != ClientMark.None) pip.SetClientMark(mark);
            _secondaryThumbnails[charName] = pip;
            Debug.WriteLine($"[PiP:Create] ✅ PiP for '{charName}' @ ({x},{y}) {w}x{h} opacity={secSettings.Opacity}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PiP:Error] ❌ Failed to create PiP for '{charName}': {ex.Message}");
        }
    }

    private void DestroySecondaryThumbnail(string charName)
    {
        if (_secondaryThumbnails.TryRemove(charName, out var pip))
        {
            UnwireMenu(pip);
            pip.Cleanup();
            pip.Close();
            Debug.WriteLine($"[PiP:Destroy] 🛑 PiP destroyed for '{charName}'");
        }
    }


    // ── Stat Overlay Window Lifecycle ────────────────────────────────

    private void CreateStatWindowsForCharacter(string charName, IntPtr eveHwnd)
    {
        if (string.IsNullOrEmpty(charName) || _statTracker == null) return;

        // Master ON/OFF gate — when the user disables the Stats Overlay,
        // no windows are ever created, regardless of per-metric / per-character settings.
        var s = _settings.Settings;
        if (!s.StatOverlayEnabled) return;

        // Resolve per-character effective metric set — skip window if no metric is enabled.
        s.PerCharacterStats.TryGetValue(charName, out var statConfig);
        var effective = CharacterStatSettings.Resolve(s.GlobalStatMetrics, statConfig);
        if ((effective & StatMetrics.AllMetrics) == 0) return;

        // AHK: ONE stat window per character (not per stat type)
        string key = charName;
        if (_statWindows.ContainsKey(key)) return;

        try
        {
            // Load saved position or default
            // AHK format stores under plain charName; C# used _Stats suffix — try both
            int sx = 100, sy = 100;
            ThumbnailRect? savedPos = null;
            if (_settings.Settings.StatWindowPositions.TryGetValue(charName, out savedPos) ||
                _settings.Settings.StatWindowPositions.TryGetValue($"{charName}_Stats", out savedPos))
            {
                sx = (int)savedPos.X;
                sy = (int)savedPos.Y;
            }

            var statWin = new StatOverlayWindow();
            statWin.Initialize(charName, "Stats", sx, sy);
            statWin.SetFontSize(_settings.Settings.StatOverlayFontSize);
            statWin.SetOpacity((byte)_settings.Settings.StatOverlayOpacity);
            statWin.SetBackgroundColor(_settings.Settings.StatOverlayBgColor);
            statWin.SetTextColor(_settings.Settings.StatOverlayTextColor);

            // Load saved size if available
            if (savedPos != null && savedPos.Width > 0 && savedPos.Height > 0)
            {
                statWin.Width = savedPos.Width;
                statWin.Height = savedPos.Height;
            }
            // Always on top
            statWin.Topmost = _settings.Settings.ShowThumbnailsAlwaysOnTop;

            statWin.PositionAndSizeChanged += (win, px, py, pw, ph) =>
            {
                // Save under plain charName (AHK-compatible); remove legacy _Stats key
                _settings.Settings.StatWindowPositions[win.CharacterName] = new Models.ThumbnailRect
                {
                    X = px, Y = py, Width = pw, Height = ph
                };
                _settings.Settings.StatWindowPositions.Remove($"{win.CharacterName}_Stats");
                _settings.SaveDelayed();
            };

            // Snap delegate — corner-to-corner snapping (matches AHK _SnapStatWindow)
            // Snaps on release: finds closest corner pair between dragged window and targets
            statWin.SnapPosition = (x, y, w, h) =>
            {
                if (!_settings.Settings.ThumbnailSnap) return (x, y);
                int snapRange = _settings.Settings.ThumbnailSnapDistance;

                // 4 corners of the dragged window
                var myCorners = new[] {
                    (x, y),                 // TL
                    (x + w, y),             // TR
                    (x, y + h),             // BL
                    (x + w, y + h)          // BR
                };
                bool[] isRight = { false, true, false, true };
                bool[] isBottom = { false, false, true, true };

                double bestDist = snapRange + 1;
                double destX = x, destY = y;
                bool shouldMove = false;

                // Collect all snap targets: other stat windows + primary thumbnails
                var targets = new System.Collections.Generic.List<(double L, double T, double W, double H)>();
                foreach (var (_, sw) in _statWindows)
                {
                    if (sw == statWin) continue;
                    targets.Add((sw.Left, sw.Top, sw.Width, sw.Height));
                }
                foreach (var (_, thumb) in _thumbnails)
                    targets.Add((thumb.Left, thumb.Top, thumb.Width, thumb.Height));

                foreach (var (oL, oT, oW, oH) in targets)
                {
                    var targetCorners = new[] { (oL, oT), (oL + oW, oT), (oL, oT + oH), (oL + oW, oT + oH) };
                    for (int i = 0; i < 4; i++)
                    {
                        for (int j = 0; j < 4; j++)
                        {
                            double dx = myCorners[i].Item1 - targetCorners[j].Item1;
                            double dy = myCorners[i].Item2 - targetCorners[j].Item2;
                            double dist = Math.Sqrt(dx * dx + dy * dy);
                            if (dist <= snapRange && dist < bestDist)
                            {
                                bestDist = dist;
                                shouldMove = true;
                                destX = targetCorners[j].Item1 - (isRight[i] ? w : 0);
                                destY = targetCorners[j].Item2 - (isBottom[i] ? h : 0);
                            }
                        }
                    }
                }

                return shouldMove ? (destX, destY) : (x, y);
            };

            // Uniform resize: when one stat window is resized, resize all others
            // (unless Ctrl is held for individual resize)
            statWin.ResizeAll += (source, newW, newH) =>
            {
                foreach (var (_, sw) in _statWindows)
                {
                    if (sw == source) continue;
                    sw.Width = newW;
                    sw.Height = newH;
                }
            };

            statWin.Show();
            statWin.SetHeldHidden(FocusMode.Holds(_focusHeld, charName));   // IDEAS P124
            _statWindows[key] = statWin;
            Debug.WriteLine($"[StatWindow:Create] ✅ Stats overlay for '{charName}' @ ({sx},{sy})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatWindow:Error] \u274c Failed to create stats for '{charName}': {ex.Message}");
        }
    }

    private void DestroyStatWindowsForCharacter(string charName)
    {
        // Single key per character (AHK: one stat window per char)
        if (_statWindows.TryRemove(charName, out var statWin))
        {
            // Save position before closing — use plain charName (AHK-compatible)
            _settings.Settings.StatWindowPositions[charName] = new Models.ThumbnailRect
            {
                X = (int)statWin.Left, Y = (int)statWin.Top,
                Width = (int)statWin.Width, Height = (int)statWin.Height
            };

            statWin.Close();
            Debug.WriteLine($"[StatWindow:Destroy] \ud83d\uded1 Stats overlay destroyed for '{charName}'");
        }
    }

    /// <summary>Update all stat overlay windows with current values from StatTrackerService.</summary>
    private void UpdateStatWindows()
    {
        if (_statTracker == null || _statWindows.IsEmpty) return;

        // Auto-destroy stat windows for characters no longer online
        var activeNames = _thumbnails.Values
            .Select(t => t.CharacterName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var staleKeys = _statWindows.Keys
            .Where(k => !activeNames.Contains(k))
            .ToList();
        foreach (var key in staleKeys)
        {
            if (_statWindows.TryRemove(key, out var staleWin))
            {
                Debug.WriteLine($"[StatWindow:AutoClean] 🧹 Removing stale stat window: {key}");
                staleWin.Close();
            }
        }

        foreach (var (key, statWin) in _statWindows)
        {
            try
            {
                string charName = statWin.CharacterName;

                // Resolve per-character effective metric set then render only those bits.
                var s = _settings.Settings;
                s.PerCharacterStats.TryGetValue(charName, out var statConfig);
                var effective = CharacterStatSettings.Resolve(s.GlobalStatMetrics, statConfig);
                string overlayText = _statTracker.GetOverlayText(charName, effective);
                if (!string.IsNullOrEmpty(overlayText))
                    statWin.UpdateOverlayText(overlayText);
                else
                    statWin.UpdateOverlayText(LocalizationService.Str("L.Stats.Waiting", "Waiting for data..."));
            }
            catch { }
        }

        // Visibility toggling on lost focus is handled in UpdateActiveBorders (50ms tick,
        // uses appVisibleContext = eveFocused || _settingsOpen) so stats/PiPs stay in
        // lockstep with primary thumbnails.
    }

    /// <summary>Save all stat window positions (called on app exit).</summary>
    public void SaveStatWindowPositions()
    {
        foreach (var (_, statWin) in _statWindows)
        {
            // Save under plain charName (AHK-compatible)
            _settings.Settings.StatWindowPositions[statWin.CharacterName] = new Models.ThumbnailRect
            {
                X = (int)statWin.Left, Y = (int)statWin.Top,
                Width = (int)statWin.Width, Height = (int)statWin.Height
            };
            // Remove legacy _Stats key if present
            _settings.Settings.StatWindowPositions.Remove($"{statWin.CharacterName}_Stats");
        }
    }

    public void Dispose()
    {
        try { FlushPlaytime(); } catch { }   // IDEAS P11: what was counted since the last save

        // Don't leave clients muted from auto-solo after we exit.
        try { UnmuteAllClientAudio(); } catch { }

        // Same principle for "cover taskbar" (#99): a client left in the topmost band
        // would keep sitting over the taskbar after MultiPreview closes, with nothing
        // left running to put it back. Drop them all out of it on the way out.
        try
        {
            if (_taskbarCoverApplied)
            {
                foreach (var (eveHwnd, _) in _thumbnails) SetTaskbarCoverBand(eveHwnd, false);
                _taskbarCoverApplied = false;
                _taskbarCoverTop = IntPtr.Zero;
            }
        }
        catch { }

        _focusTimer?.Stop();
        _sessionTimer?.Stop();
        _flashTimer?.Stop();
        _statTimer?.Stop();
        // Previously missed — _fpsTimer (500 ms) and _batchTimer (50 ms,
        // lazily created on first window discovery) could still tick during
        // shutdown unwind and call into a half-disposed manager. Stop is
        // idempotent so a no-op when the timer isn't running or was never
        // created (?. handles the lazy _batchTimer null case).
        _fpsTimer?.Stop();
        _batchTimer?.Stop();
        _raiseOverlaysDebounce?.Stop();   // #100 debounce — same shutdown-unwind risk
        if (_displayChanged != null)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= _displayChanged;   // IDEAS #10
            _displayChanged = null;
        }
        _monitorSettleTimer?.Stop();
        _monitorFollower = null;
        if (_winEvents != null)
        {
            _winEvents.ForegroundChanged -= OnForegroundOrMinimizeEvent;
            _winEvents.WindowMinimizeStart -= OnForegroundOrMinimizeEvent;
            _winEvents.WindowMinimizeEnd -= OnForegroundOrMinimizeEvent;
            _winEvents = null;
        }
        _frozenFrames?.Dispose();
        _frozenFrames = null;
        _discovery.WindowFound -= OnWindowFound;
        _discovery.WindowLost -= OnWindowLost;
        _discovery.WindowTitleChanged -= OnWindowTitleChanged;

        foreach (var (_, timer) in _destroyTimers)
            timer.Stop();
        _destroyTimers.Clear();

        // Use BeginInvoke to avoid deadlock if Dispose is called during app shutdown
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                HideIndicatorLegend();   // IDEAS #37

                // Primary thumbnails
                foreach (var (_, thumb) in _thumbnails)
                {
                    UnwireEvents(thumb);
                    thumb.Cleanup();
                    thumb.Close();
                }
                _thumbnails.Clear();

                // Secondary PiP thumbnails
                foreach (var (_, pip) in _secondaryThumbnails)
                {
                    UnwireMenu(pip);
                    pip.Cleanup();
                    pip.Close();
                }
                _secondaryThumbnails.Clear();

                // Stat overlay windows
                SaveStatWindowPositions();
                foreach (var (_, sw) in _statWindows)
                    sw.Close();
                _statWindows.Clear();
            });
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            Debug.WriteLine($"[Thumbnail:Dispose] ⚠ Dispatcher shutdown during cleanup: {ex.GetType().Name}");
        }

        Debug.WriteLine("[Thumbnail:Dispose] 🛑 ThumbnailManager disposed (primary + PiP + stat windows)");
    }
}

/// <summary>Tracks per-character alert flash state with severity-based timing. Both times are UTC (<see cref="ThumbnailManager.FlashClock"/>).</summary>
public class AlertFlashInfo
{
    public DateTime StartTime { get; set; }
    public string Severity { get; set; } = "critical";
    public string EventType { get; set; } = "attack";
    public bool ShowFlash { get; set; } = true;
    public DateTime LastToggleTime { get; set; }
}

/// <summary>Per-character unread-alert badge state — count of alerts fired
/// since the user last activated this character, plus the strongest severity
/// seen so the rendered badge can pick its colour. Cleared on activation. A value is never changed once it is
/// made: a new one takes its place in the table (<see cref="CountOne"/>).</summary>
public class AlertBadgeInfo
{
    public int Count { get; init; }
    public string TopSeverity { get; init; } = "info";

    /// <summary>One more alert for a character's badge: the value the table holds for it afterwards, with the count
    /// the badge shows now. Alerts are dispatched from two threads (the log reader, and the window's thread for
    /// "Left behind"). Counting up inside the stored value could lose one of two alerts that arrive in the same
    /// instant; here each update puts a new value in the place of the one it started from, and the table makes an
    /// update again whose starting value another update has replaced meanwhile. The severity stays the one the count
    /// began with, as it always did.</summary>
    public static AlertBadgeInfo CountOne(ConcurrentDictionary<string, AlertBadgeInfo> badges, string character, string severity) =>
        badges.AddOrUpdate(character,
            _ => new AlertBadgeInfo { Count = 1, TopSeverity = severity },
            (_, existing) => new AlertBadgeInfo { Count = existing.Count + 1, TopSeverity = existing.TopSeverity });

    /// <summary>One more alert for a character's badge, asked of the unseen list (IDEAS P75). Null when the list holds
    /// nothing for the character: nothing is counted. Otherwise the count the badge holds afterwards, which is never more
    /// than the list holds: an alert the list let go at once (over <see cref="AlertAttention.MaxPerCharacter"/>, a lesser one
    /// makes room) is no alert for a badge. The list is asked a second time after the count went up, because the player may
    /// have looked at that client meanwhile (the focus sweep clears the badge and takes the alerts off on the window's thread,
    /// while this runs on the log reader's): then the count just made is taken off again and 0 is returned, or it would sit
    /// on a thumbnail with nothing behind it. <paramref name="afterTheFirstLook"/> runs between the two looks (tests).</summary>
    public static int? CountAlert(AlertAttention attention, ConcurrentDictionary<string, AlertBadgeInfo> badges, string character,
        string severity, Action? afterTheFirstLook = null)
    {
        if (!attention.Has(character)) return null;
        afterTheFirstLook?.Invoke();
        CountOne(badges, character, severity);
        if (attention.For(character) is not { } held)
        {
            badges.TryRemove(character, out _);
            return 0;
        }
        CapAt(badges, character, held.Count);
        return CountOf(badges, character);
    }

    /// <summary>The count the table holds for a character now, which is what its badge shows: 0 when it holds none
    /// (nothing came since the character was looked at).</summary>
    public static int CountOf(ConcurrentDictionary<string, AlertBadgeInfo> badges, string character) =>
        badges.TryGetValue(character, out var held) ? held.Count : 0;

    /// <summary>A character's badge shows no more than <paramref name="count"/>: a larger count is replaced by a new
    /// value (the severity stays), a smaller one or none is left as it is. True when the count was lowered.</summary>
    public static bool CapAt(ConcurrentDictionary<string, AlertBadgeInfo> badges, string character, int count)
    {
        while (badges.TryGetValue(character, out var held) && held.Count > count)
            if (badges.TryUpdate(character, new AlertBadgeInfo { Count = count, TopSeverity = held.TopSeverity }, held)) return true;
        return false;
    }
}
