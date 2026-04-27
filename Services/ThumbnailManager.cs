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
    private readonly System.Collections.Generic.HashSet<IntPtr> _iconicHwnds = new();
    private FrozenFrameService? _frozenFrames;
    private WinEventHookService? _winEvents;
    private DispatcherTimer? _focusTimer;
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
    private bool _lastEveFocused = false;   // Track focus transitions for one-time BringToFront

    // Alert flash state — per-severity rates + expiry (matches AHK)
    private readonly ConcurrentDictionary<string, AlertFlashInfo> _alertFlashChars = new();
    private DispatcherTimer? _flashTimer;

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

    // Under-fire tracking: character name → last damage timestamp
    private readonly ConcurrentDictionary<string, DateTime> _underFireChars = new();

    // Drag all state: positions of all thumbnails at start of Ctrl+drag
    private readonly Dictionary<ThumbnailWindow, (double X, double Y)> _dragAllStartPositions = new();
    private double _dragAllBaseX, _dragAllBaseY;

    public ThumbnailManager(WindowDiscoveryService discovery, SettingsService settings)
    {
        _discovery = discovery;
        _settings = settings;

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
        _sessionTimer.Tick += (_, _) => { UpdateSessionTimers(); CheckUnderFireExpiry(); };
        _sessionTimer.Start();

        // Flash timer — 100ms for urgent, responsive alert flashing (10Hz)
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _flashTimer.Tick += (_, _) => FlashAlertTick();
        _flashTimer.Start();

        // Stat overlay update timer — 1s interval for DPS/Logi/Mining/Ratting
        _statTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statTimer.Tick += (_, _) => UpdateStatWindows();
        _statTimer.Start();

        // FPS polling timer — 500ms interval for real-time overlay
        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fpsTimer.Tick += (_, _) => UpdateFpsOverlays();
        _fpsTimer.Start();

        Debug.WriteLine("[AlertFlash:Start] 🔧 Focus (100ms), Session (1s), Flash (200ms), Stat (1s), FPS (500ms) timers started");
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
            // Flow-layout: stack vertically, wrap to new column at screen edge
            int index = _thumbnails.Count;
            int gap = 8;
            var workArea = GetTargetMonitorWorkArea(s.PreferredMonitor);
            int availableHeight = workArea.Bottom - y;
            int maxPerCol = Math.Max(1, availableHeight / (height + gap));
            int col = index / maxPerCol;
            int row = index % maxPerCol;
            x += col * (width + gap);
            y += row * (height + gap);
        }

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
        thumbWindow.LabelEditRequested += OnLabelEditRequested;

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

        // Apply system name *after* window show to guarantee WPF visibility rendering
        if (!string.IsNullOrEmpty(window.CharacterName) &&
            _settings.Settings.ShowSystemName &&
            _charSystems.TryGetValue(window.CharacterName, out var finalSys))
        {
            thumbWindow.UpdateSystemName(finalSys);
        }

        // Restore client position if tracking
        if (s.TrackClientPositions)
            RestoreClientPosition(window.Hwnd, window.CharacterName);

        PerfLog(
            $"✅ Created thumbnail for '{window.CharacterName}' @ ({x},{y}) {width}x{height} [{sw.ElapsedMilliseconds}ms]");
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
        _iconicHwnds.Remove(window.Hwnd);
        _frozenFrames?.Forget(window.Hwnd);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_thumbnails.TryRemove(window.Hwnd, out var thumbWindow))
            {
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
                }

                UnwireEvents(thumbWindow);
                thumbWindow.Cleanup();
                thumbWindow.Close();
            }
        });
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

                // Check if char select (title == "EVE" without character name)
                bool isCharSelect = string.IsNullOrEmpty(window.CharacterName) ||
                    window.Title == "EVE";
                thumbWindow.SetNotLoggedIn(isCharSelect, _settings.Settings.NotLoggedInIndicator);

                // Issue 5: Re-apply system name after character login
                if (!string.IsNullOrEmpty(window.CharacterName) &&
                    _charSystems.TryGetValue(window.CharacterName, out var sys))
                {
                    thumbWindow.UpdateSystemName(sys);
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


                }
            }
        });
    }

    // ── Settings Application ────────────────────────────────────────

    private void ApplySettings(ThumbnailWindow thumb, EveWindow window)
    {
        var s = _settings.Settings;

        // Lock positions
        thumb.IsLocked = s.LockPositions;

        // Opacity
        thumb.SetOpacity((byte)s.ThumbnailOpacity);

        // Always on top
        thumb.SetTopmost(s.ShowThumbnailsAlwaysOnTop);

        // Background color
        thumb.SetBackgroundColor(ParseColor(s.ThumbnailBackgroundColor));

        // Hover zoom
        if (s.ResizeThumbnailsOnHover)
            thumb.SetHoverScale(s.HoverScale);

        // Text overlay
        thumb.SetTextOverlayVisible(s.ShowThumbnailTextOverlay);
        thumb.SetTextStyle(s.ThumbnailTextFont,
            double.TryParse(s.ThumbnailTextSize, out var fs) ? fs : 12,
            ParseColor(s.ThumbnailTextColor));
        thumb.SetTextMargins((int)s.ThumbnailTextMargins.X, (int)s.ThumbnailTextMargins.Y);

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
        thumb.SetNotLoggedIn(isCharSelect, s.NotLoggedInIndicator);
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
            foreach (var (_, thumb) in _thumbnails)
                thumb.SetOpacity(opacity);
            foreach (var (_, pip) in _secondaryThumbnails)
                pip.SetOpacity(opacity);
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
                        int applyW = s.IndividualThumbnailResize ? (int)savedPos.Width : (int)s.ThumbnailStartLocation.Width;
                        int applyH = s.IndividualThumbnailResize ? (int)savedPos.Height : (int)s.ThumbnailStartLocation.Height;

                        thumb.MoveTo((int)savedPos.X, (int)savedPos.Y);
                        thumb.Resize(applyW, applyH);
                    }
                    else
                    {
                        // Apply layout defaults instantly to thumbnails that have never been manually moved
                        thumb.MoveTo((int)s.ThumbnailStartLocation.X, (int)s.ThumbnailStartLocation.Y);
                        thumb.Resize((int)s.ThumbnailStartLocation.Width, (int)s.ThumbnailStartLocation.Height);
                    }
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


            // Re-apply stat overlay settings (font size, opacity, topmost)
            foreach (var (_, statWin) in _statWindows)
            {
                statWin.SetFontSize(s.StatOverlayFontSize);
                statWin.SetOpacity((byte)s.StatOverlayOpacity);
                statWin.SetBackgroundColor(s.StatOverlayBgColor);
                statWin.SetTextColor(s.StatOverlayTextColor);
                statWin.Topmost = s.ShowThumbnailsAlwaysOnTop;
                statWin.LockPositions = s.LockPositions;
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
    }

    private void OnSwitchRequested(ThumbnailWindow thumb)
    {
        ActivateEveWindow(thumb.EveHwnd, thumb.CharacterName);
    }

    private void OnMinimizeRequested(ThumbnailWindow thumb)
    {
        Interop.User32.ShowWindowAsync(thumb.EveHwnd, Interop.User32.SW_FORCEMINIMIZE);
    }



    private void OnDragMoveAll(ThumbnailWindow source, double dx, double dy)
    {
        // On first call of drag-all, store all positions
        if (_dragAllStartPositions.Count == 0)
        {
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

    /// <summary>Called when drag ends to clear stored positions.</summary>
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
        if (!_settings.Settings.IndividualThumbnailResize)
        {
            _settings.Settings.ThumbnailStartLocation.Width = newW;
            _settings.Settings.ThumbnailStartLocation.Height = newH;
        }

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

    public void ActivateEveWindow(IntPtr hwnd, string? title = null, bool rapidSwitch = false, Action? onActivated = null)
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

            if (!string.IsNullOrEmpty(title))
                _alertFlashChars.TryRemove(title, out _);

            if (Interop.User32.IsIconic(hwnd))
            {
                if (_settings.Settings.AlwaysMaximize)
                    Interop.User32.ShowWindowAsync(hwnd, Interop.User32.SW_MAXIMIZE);
                else
                    Interop.User32.ShowWindowAsync(hwnd, Interop.User32.SW_RESTORE);
            }

            EveMultiPreview.Services.DiagnosticsService.LogWindowHook($"[ActivateEveWindow] 🚀 Executing Standard WIN32 Activation for HWND {hwnd} ({title ?? "unknown"})");
            Interop.User32.ActivateWindow(hwnd);
            
            // Execute visual callback instantaneously
            onActivated?.Invoke();

            // Explicitly pulse WM_KEYDOWN messages directly to the EVE client for held action keys
            // This natively restores module firing without polluting global SendInput states
            Interop.User32.FixTargetHeldKeys(hwnd);

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
            }, System.Windows.Threading.DispatcherPriority.Background);

            if (_settings.Settings.AlwaysMaximize && !Interop.User32.IsZoomed(hwnd))
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
    private void OnForegroundOrMinimizeEvent(IntPtr _)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(new Action(UpdateActiveBorders), DispatcherPriority.Background);
    }

    private void UpdateActiveBorders()
    {
        // Skip all heavy Win32/DWM work during drag — prevents contention
        if (_thumbnails.Values.Any(t => t.IsDragging))
            return;

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
            bool wasIconic = _iconicHwnds.Contains(eveHwnd);
            if (isIconic == wasIconic) continue;

            if (isIconic)
            {
                _iconicHwnds.Add(eveHwnd);
                var frame = _frozenFrames?.GetLastFrame(eveHwnd);
                thumb.SetFrozenFrame(frame);
                thumb.HideDwmOnly(); // DWM transparent so OnPaint's frozen bitmap shows.
            }
            else
            {
                _iconicHwnds.Remove(eveHwnd);
                thumb.ClearFrozenFrame();
                if (!_thumbnailsHidden && !_primaryHidden)
                    thumb.ShowDwmOnly(); // Restore live DWM preview.
            }
        }

        // Visibility gate for primary thumbnails under HideThumbnailsOnLostFocus.
        // Hide only when every EVE window is closed (or Settings is also closed)
        // — stat overlays and PiPs are decoupled from this and always stay up
        // while any EVE client exists.
        bool anyEveExists = _thumbnails.Count > 0;
        bool appVisibleContext = anyEveExists || _settingsOpen;

        if (s.HideThumbnailsOnLostFocus)
        {
            foreach (var (eveHwnd, thumb) in _thumbnails)
            {
                // Skip DWM toggling for iconic windows — they're painting a frozen
                // frame and must keep _isDwmHidden=true for that to render.
                if (_iconicHwnds.Contains(eveHwnd)) continue;

                if (!appVisibleContext && !_thumbnailsHidden)
                    thumb.HideDwmOnly();
                else if (appVisibleContext && !_thumbnailsHidden && !_primaryHidden)
                    thumb.ShowDwmOnly();
            }

            // Stat overlays and PiPs normally stay visible whenever any client is
            // tracked. When HideStatsOnLostFocus is opted-in (issue #12), they also
            // collapse whenever EVE/our-app is not the foreground process — matching
            // the pre-2026-04-23 behaviour for users who preferred it.
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

        // Sync text overlay positions (position-only, z-order handled by BringToFront)
        foreach (var (_, thumb) in _thumbnails)
        {
            thumb.SyncOverlayPosition();
        }
        foreach (var (_, pip) in _secondaryThumbnails)
        {
            pip.SyncOverlayPosition();
        }

        // One-time BringToFront when EVE gains focus (false→true transition)
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
        }
        _lastEveFocused = eveFocused;

        // Shield the fast-cycle tracker and visual borders from asynchronous OS focus lag.
        // If we recently cycled, the native background window switch might still be resolving.
        // Letting the slow OS tracker overwrite our state here will illegally rewind the cycle sequence.
        if ((DateTime.UtcNow - _lastCycleTime).TotalMilliseconds < 500)
            return;

        if (fgHwnd == _lastActiveEveHwnd) return;
        _lastActiveEveHwnd = fgHwnd;

        // Re-assert z-order when switching between EVE clients (EVE→EVE transition).
        // The false→true BringToFront above only fires when coming FROM a non-EVE app.
        // EVE's DirectX surface aggressively fights for z-order in the TOPMOST band,
        // pushing ThumbnailWindows behind while TextOverlays survive via owner semantics.
        if (eveFocused && !_suppressTopmost && s.ShowThumbnailsAlwaysOnTop)
        {
            foreach (var (_, thumb) in _thumbnails)
                thumb.BringToFront();
            foreach (var (_, pip) in _secondaryThumbnails)
                pip.BringToFront();
            foreach (var (_, sw) in _statWindows)
                sw.BringToFront();
        }

        // Flash toggle moved to dedicated FlashAlertTick

        foreach (var (eveHwnd, thumb) in _thumbnails)
        {
            bool isActive = eveHwnd == fgHwnd;
            string charName = thumb.CharacterName;

            // Hide active thumbnail DWM preview (text overlay stays via owner + opacity)
            if (s.HideActiveThumbnail && !_thumbnailsHidden)
            {
                if (isActive)
                    thumb.HideDwmOnly(); // Opacity 0 — text overlay stays
                else
                    thumb.ShowDwmOnly(); // Restore inactive thumbnail opacity
            }

            // Border color
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
                // Check alert flash (severity-aware)
                if (_alertFlashChars.TryGetValue(charName, out var flashInfo))
                {
                    // Per-event color cascade: AlertColors[eventType] → SeverityColors[severity] → default red
                    var flashColor = ResolveAlertFlashColor(flashInfo.EventType, flashInfo.Severity);
                    if (flashInfo.ShowFlash)
                    {
                        // Flash ON: show resolved flash color
                        thumb.SetBorder(flashColor, s.ClientHighlightBorderThickness);
                    }
                    else if (flashInfo.Severity == "info")
                    {
                        // Info alerts: stay solid (no dim)
                        thumb.SetBorder(flashColor, s.ClientHighlightBorderThickness);
                    }
                    else
                    {
                        // Flash OFF for critical/warning: dim color (AHK: "330000")
                        thumb.SetBorder(Color.FromRgb(0x33, 0x00, 0x00), s.ClientHighlightBorderThickness);
                    }
                }
                else
                {
                    // Show inactive border only if a color is configured
                    var color = GetBorderColor(charName, false);
                    int thickness = ShouldShowInactiveBorder(charName) ? s.InactiveClientBorderThickness : 0;
                    thumb.SetBorder(color, thickness);
                }
            }
        }

        // ── CPU Affinity & Priority Management ──
        ManageCpuAffinity(fgHwnd, s);
    }

    private void ManageCpuAffinity(IntPtr activeHwnd, AppSettings s)
    {
        if (!s.ManageAffinity) return;

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

    /// <summary>Check whether an inactive border should be shown for this character.
    /// Returns false when InactiveClientBorderColor is empty and no custom/group color applies.</summary>
    private bool ShouldShowInactiveBorder(string charName)
    {
        var s = _settings.Settings;

        // Custom per-character inactive border — always show if configured
        if (s.CustomColorsActive && s.CustomColors.TryGetValue(charName, out var custom)
            && !string.IsNullOrEmpty(custom.InactiveBorder))
            return true;

        // Group color — show if enabled and character is in a group
        if (s.ShowAllColoredBorders)
        {
            foreach (var group in s.ThumbnailGroups)
            {
                if (group.Members.Any(m => m.Equals(charName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        // Default — empty means no border
        return !string.IsNullOrWhiteSpace(s.InactiveClientBorderColor);
    }

    /// <summary>Get the correct border color for a character (custom → group → default).</summary>
    private Color GetBorderColor(string charName, bool isActive)
    {
        var s = _settings.Settings;

        // 1. Custom per-character color
        if (s.CustomColorsActive && s.CustomColors.TryGetValue(charName, out var custom))
        {
            if (isActive && !string.IsNullOrEmpty(custom.Border))
                return ParseColor(custom.Border);
            if (!isActive && !string.IsNullOrEmpty(custom.InactiveBorder))
                return ParseColor(custom.InactiveBorder);
        }

        // 2. Thumbnail group color (only when Show Group Borders is enabled)
        if (s.ShowAllColoredBorders)
        {
            foreach (var group in s.ThumbnailGroups)
            {
                if (group.Members.Any(m => m.Equals(charName, StringComparison.OrdinalIgnoreCase)))
                {
                    return ParseColor(group.Color);
                }
            }
        }

        // 3. Default colors
        return isActive
            ? ParseColor(s.ClientHighlightColor)
            : ParseColor(s.InactiveClientBorderColor);
    }

    /// <summary>Resolve per-event alert flash color: AlertColors[eventType] → SeverityColors[severity] → default red.</summary>
    private Color ResolveAlertFlashColor(string eventType, string severity)
    {
        var s = _settings.Settings;

        // 1. Per-event color override
        if (s.AlertColors.TryGetValue(eventType, out var eventColor) && !string.IsNullOrEmpty(eventColor))
            return ParseColor(eventColor);

        // 2. Severity-level color
        if (s.SeverityColors.TryGetValue(severity, out var sevColor) && !string.IsNullOrEmpty(sevColor))
            return ParseColor(sevColor);

        // 3. Default flash color from settings, then hardcoded red
        if (!string.IsNullOrEmpty(s.AlertFlashColor))
            return ParseColor(s.AlertFlashColor);

        return Color.FromRgb(0xFF, 0x00, 0x00); // Default red
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
        ShowTooltipFeedback(isSuspended ? "Hotkeys: SUSPENDED" : "Hotkeys: ACTIVE");
    }

    // ── Snap ─────────────────────────────────────────────────────────

    // M25: Corner-to-corner snapping (matches AHK Window_Snap algorithm)
    private void SnapThumbnail(ThumbnailWindow target)
    {
        int snapRange = _settings.Settings.ThumbnailSnapDistance;
        double x = target.Left, y = target.Top;
        double w = target.Width, h = target.Height;

        // 4 corners of the dragged window
        var myCorners = new[] { (x, y), (x + w, y), (x, y + h), (x + w, y + h) };
        bool[] isRight = { false, true, false, true };
        bool[] isBottom = { false, false, true, true };

        double bestDist = snapRange + 1;
        double destX = x, destY = y;
        bool shouldMove = false;

        foreach (var (_, other) in _thumbnails)
        {
            if (other == target) continue;
            double oL = other.Left, oT = other.Top, oW = other.Width, oH = other.Height;
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

        if (shouldMove)
        {
            target.Left = destX;
            target.Top = destY;
            target.SyncOverlayPosition();
        }
    }





    // ── Session Timer ────────────────────────────────────────────────

    private void UpdateSessionTimers()
    {
        if (!_settings.Settings.ShowSessionTimer) return;

        foreach (var (_, thumb) in _thumbnails)
        {
            if (!string.IsNullOrEmpty(thumb.CharacterName))
            {
                thumb.UpdateSessionTimer(thumb.SessionDuration);
            }
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

        Application.Current?.Dispatcher.Invoke(() =>
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
        });
    }

    // ── Alert Flash (per-severity rates + expiry) ────────────────────

    public void SetAlertFlash(string characterName, string severity = "critical", string eventType = "attack")
    {
        _alertFlashChars[characterName] = new AlertFlashInfo
        {
            StartTime = DateTime.Now,
            Severity = severity,
            EventType = eventType,
            ShowFlash = true,
            LastToggleTime = DateTime.Now
        };
        Debug.WriteLine($"[AlertFlash:Start] 🚨 Flash started: '{characterName}' severity={severity} event={eventType}");
    }

    public void ClearAlertFlash(string characterName)
    {
        if (_alertFlashChars.TryRemove(characterName, out _))
            Debug.WriteLine($"[AlertFlash:Tick] ✅ Flash cleared: '{characterName}'");
    }

    /// <summary>Dedicated flash timer tick — handles per-severity flash rates, expiry, and border updates.</summary>
    private void FlashAlertTick()
    {
        if (_alertFlashChars.IsEmpty) return;

        var now = DateTime.Now;
        var s = _settings.Settings;
        var toRemove = new List<string>();

        foreach (var (charName, info) in _alertFlashChars)
        {
            // Check expiry
            int expirySec = FlashExpiry.GetValueOrDefault(info.Severity, 6);
            if ((now - info.StartTime).TotalSeconds >= expirySec)
            {
                toRemove.Add(charName);
                Debug.WriteLine($"[AlertFlash:Expire] ⏰ Flash expired: '{charName}' after {expirySec}s ({info.Severity})");
                continue;
            }

            // Check if it's time to toggle based on severity rate
            int rateMs = FlashRates.GetValueOrDefault(info.Severity, 500);
            if ((now - info.LastToggleTime).TotalMilliseconds >= rateMs)
            {
                info.ShowFlash = !info.ShowFlash;
                info.LastToggleTime = now;
            }

            // Apply flash border color directly (fixes: UpdateActiveBorders skips this via early return)
            var thumb = FindThumbnailByCharacter(charName);
            if (thumb != null)
            {
                var flashColor = ResolveAlertFlashColor(info.EventType, info.Severity);
                if (info.ShowFlash)
                {
                    thumb.SetBorder(flashColor, s.ClientHighlightBorderThickness);
                }
                else if (info.Severity == "info")
                {
                    thumb.SetBorder(flashColor, s.ClientHighlightBorderThickness);
                }
                else
                {
                    // Flash OFF for critical/warning: dim color (AHK: "330000")
                    thumb.SetBorder(Color.FromRgb(0x33, 0x00, 0x00), s.ClientHighlightBorderThickness);
                }
            }
        }

        // Remove expired flashes and restore their inactive borders
        foreach (var name in toRemove)
        {
            _alertFlashChars.TryRemove(name, out _);
            var thumb = FindThumbnailByCharacter(name);
            if (thumb != null)
            {
                var color = GetBorderColor(name, false);
                thumb.SetBorder(color, s.InactiveClientBorderThickness);
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

        _underFireChars[characterName] = DateTime.Now;

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

        var now = DateTime.Now;
        int timeoutSec = _settings.Settings.UnderFireTimeoutSeconds;
        var expired = new List<string>();

        foreach (var (charName, lastDamage) in _underFireChars)
        {
            if ((now - lastDamage).TotalSeconds >= timeoutSec)
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

                _quickSwitchWheel.ShowWheel(ordered);
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
                Interop.User32.SetForegroundWindow(solo);
                return;
            }

            if (forward)
                _charSelectIndex = (_charSelectIndex + 1) % charSelectWindows.Count;
            else
                _charSelectIndex = (_charSelectIndex - 1 + charSelectWindows.Count) % charSelectWindows.Count;

            var targetHwnd = charSelectWindows[_charSelectIndex];

            // Restore if minimized
            if (Interop.User32.IsIconic(targetHwnd))
                Interop.User32.ShowWindowAsync(targetHwnd, Interop.User32.SW_RESTORE);

            // Raw window activation
            Interop.User32.SetForegroundWindow(targetHwnd);
            Interop.User32.SetFocus(targetHwnd);
            
            // Explicitly pass held login actions (like Enter) to Chromium message pump
            Interop.User32.FixTargetHeldKeys(targetHwnd);

            // Defer WPF z-order operations to prevent dispatch pumping from eating the OS input state interrupt
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var (_, t) in _thumbnails) t.BringToFront();
                foreach (var (_, p) in _secondaryThumbnails) p.BringToFront();
                foreach (var (_, sw) in _statWindows) sw.BringToFront();
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
            if (pos.IsMaximized == 1)
            {
                Interop.User32.ShowWindowAsync(hwnd, Interop.User32.SW_MAXIMIZE);
            }
            else
            {
                Interop.User32.MoveWindow(hwnd, (int)pos.X, (int)pos.Y, (int)pos.Width, (int)pos.Height, true);
            }
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
        ShowTooltipFeedback(_thumbnailsHidden ? "Thumbnails: Hidden" : "Thumbnails: Visible");
    }

    /// <summary>Alias for ToggleThumbnailVisibility — matches AHK HideShowThumbnailsHotkey.</summary>
    public void ToggleAllVisibility() => ToggleThumbnailVisibility();

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
        ShowTooltipFeedback(_primaryHidden ? "Primary: Hidden" : "Primary: Visible");
    }

    private bool _secondaryHidden = false;

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
        ShowTooltipFeedback(_secondaryHidden ? "PiP: Hidden" : "PiP: Visible");
    }

    private DateTime _lastCycleTime = DateTime.MinValue;

    /// <summary>Fired by CycleGroup / CycleAll whenever a cycle hotkey wraps
    /// from the last active client back to the first (or first to last when
    /// reversing). App.xaml.cs subscribes to play the cycle-wrap sound (#24).</summary>
    public event Action? CycleWrapped;

    /// <summary>Cycle through characters in a hotkey group.</summary>
    public void CycleGroup(string groupName, List<string> members, bool forward)
    {
        if (members == null || members.Count == 0) return;

        // 100ms cycle throttle to prevent skipped clients when hotkey is held
        if ((DateTime.UtcNow - _lastCycleTime).TotalMilliseconds < 100) return;
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
        
        var previousActiveHwnd = _lastActiveEveHwnd;
        int currentIdx = -1;
        string? activeChar = null;

        // O(1) lookup instead of iterating all thumbnails
        if (_thumbnails.TryGetValue(fgHwnd, out var activeThumbnail))
            activeChar = activeThumbnail.CharacterName;

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

        int nextIdx;
        bool wrapped;
        if (forward)
        {
            nextIdx = (currentIdx + 1) % onlineMembers.Count;
            // Wrap: forward cycle landed on index 0 from the end of the list.
            // Skip if currentIdx was -1 (no prior active) or onlineMembers.Count == 1.
            wrapped = currentIdx > 0 && nextIdx == 0 && onlineMembers.Count > 1;
        }
        else
        {
            nextIdx = (currentIdx - 1 + onlineMembers.Count) % onlineMembers.Count;
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

        // Create the UI border update action. This will be securely dispatched 
        // back to the WPF UI thread exactly after the window focus shift succeeds.
        Action onActivated = () =>
        {
            var s = _settings.Settings;
            int activeThickness = s.ClientHighlightBorderThickness;

            // Set new active border
            if (targetHwnd != IntPtr.Zero && _thumbnails.TryGetValue(targetHwnd, out var targetThumb))
            {
                var color = GetBorderColor(charName, true);
                targetThumb.SetBorder(color, activeThickness);
            }

            // Reset previous active to inactive border
            if (previousActiveHwnd != IntPtr.Zero && previousActiveHwnd != targetHwnd
                && _thumbnails.TryGetValue(previousActiveHwnd, out var prevThumb))
            {
                var color = GetBorderColor(prevThumb.CharacterName, false);
                int thickness = ShouldShowInactiveBorder(prevThumb.CharacterName) ? s.InactiveClientBorderThickness : 0;
                prevThumb.SetBorder(color, thickness);
            }
        };

        LogCycle($"[CycleGroup] ➡️ Activating '{charName}' (HWND: {targetHwnd}). Online Members in pool: {string.Join(", ", onlineMembers)}");
        ActivateEveWindow(targetHwnd, charName, rapidSwitch: true, onActivated: onActivated);
    }

    /// <summary>Cycle across every tracked client regardless of profile or group
    /// membership (issue #9). Keyed on HWND so it can include login-screen
    /// windows (empty CharacterName) when IncludeLoginScreensInCycle is on
    /// (issue #8). Shift-excluded characters (#16) are always skipped.</summary>
    public void CycleAll(bool forward)
    {
        if ((DateTime.UtcNow - _lastCycleTime).TotalMilliseconds < 100) return;

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

        int currentIdx = hwnds.IndexOf(fgHwnd);
        int nextIdx = forward
            ? (currentIdx + 1) % hwnds.Count
            : (currentIdx - 1 + hwnds.Count) % hwnds.Count;

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
            ? $"'{characterName}' excluded from cycle"
            : $"'{characterName}' re-included in cycle");
    }

    // ── Click-Through Toggle ────────────────────────────────────────

    public void ToggleClickThrough()
    {
        _clickThroughActive = !_clickThroughActive;
        ApplyClickThroughStateToAll();
        ShowTooltipFeedback(_clickThroughActive ? "Thumbnails: Click-Through ON" : "Thumbnails: Click-Through OFF");
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
        ShowTooltipFeedback(s.LockPositions ? "Positions: Locked" : "Positions: Unlocked");
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
        thumb.LabelEditRequested -= OnLabelEditRequested;
    }

    private void OnCycleExclusionRequested(ThumbnailWindow thumb)
    {
        ToggleCycleExclusion(thumb.CharacterName);
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

            pip.Show();
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
            pip.Cleanup();
            pip.Close();
            Debug.WriteLine($"[PiP:Destroy] 🛑 PiP destroyed for '{charName}'");
        }
    }


    // ── Stat Overlay Window Lifecycle ────────────────────────────────

    private void CreateStatWindowsForCharacter(string charName, IntPtr eveHwnd)
    {
        if (string.IsNullOrEmpty(charName) || _statTracker == null) return;

        // Resolve per-character effective metric set — skip window if no metric is enabled.
        var s = _settings.Settings;
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
                    statWin.UpdateOverlayText("Waiting for data...");
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
        _focusTimer?.Stop();
        _sessionTimer?.Stop();
        _flashTimer?.Stop();
        _statTimer?.Stop();
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

/// <summary>Tracks per-character alert flash state with severity-based timing.</summary>
public class AlertFlashInfo
{
    public DateTime StartTime { get; set; }
    public string Severity { get; set; } = "critical";
    public string EventType { get; set; } = "attack";
    public bool ShowFlash { get; set; } = true;
    public DateTime LastToggleTime { get; set; }
}
