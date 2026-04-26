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
    private SettingsService? _settings;
    private WindowDiscoveryService? _discovery;
    private ThumbnailManager? _thumbnailManager;
    private HotkeyService? _hotkeyService;
    private LogMonitorService? _logMonitor;
    private StatTrackerService? _statTracker;
    private AlertHub? _alertHub;
    private ProcessMonitorService? _processMonitor;
    private CropManager? _cropManager;
    private SettingsWindow? _settingsWindow;

    // Tray balloon click-to-focus (matches AHK _trayAlertChar/_trayAlertHwnd)
    private string _trayAlertChar = "";
    private IntPtr _trayAlertHwnd = IntPtr.Zero;

    // Per-event sound cooldowns
    private readonly Dictionary<string, DateTime> _soundCooldowns = new();

    // Sound player (WPF MediaPlayer supports MP3 unlike System.Media.SoundPlayer)
    private MediaPlayer? _soundPlayer;

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
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener("debug_log.txt"));
        System.Diagnostics.Trace.AutoFlush = true;
        // Single-instance guard
        _singleInstanceMutex = new Mutex(true, "EveMultiPreview_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("EVE MultiPreview is already running.", "EVE MultiPreview",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        // Clear perf log on each launch
        try { System.IO.File.WriteAllText(_perfLogPath, ""); } catch { }

        var startupSw = Stopwatch.StartNew();
        PerfLog("🚀 OnStartup entered");

        // ── Global Error Handler ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // ── JSON Migration ──
        CheckJsonMigration();
        PerfLog($"JSON migration check: {startupSw.ElapsedMilliseconds}ms");

        // Reset Settings diagnostic log each run
        try { System.IO.File.WriteAllText(SettingsDiagLogPath, $"--- Session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}"); } catch { }

        // 1. Load settings
        _settings = new SettingsService();
        _settings.Load();
        
        DiagnosticsService.Initialize();
        DiagnosticsService.GlobalSettings = _settings.Settings;

        PerfLog($"Settings loaded: {startupSw.ElapsedMilliseconds}ms");

        // ── Setup Wizard gate ──
        if (!_settings.Settings.SetupCompleted)
        {
            var wizard = new SetupWizard(_settings);
            wizard.ShowDialog();
            PerfLog($"Setup wizard: {startupSw.ElapsedMilliseconds}ms");
        }

        // 2. Start window discovery
        _discovery = new WindowDiscoveryService();

        // 3. Create thumbnail manager
        _thumbnailManager = new ThumbnailManager(_discovery, _settings);

        // 4. Start stat tracker (needed before thumbnails fire)
        _statTracker = new StatTrackerService();
        _thumbnailManager.SetStatTracker(_statTracker);

        // 4b. Process monitor (CPU/RAM per EVE client)
        _processMonitor = new ProcessMonitorService();
        _thumbnailManager.SetProcessMonitor(_processMonitor);
        _processMonitor.Start();

        // 4c. Crop manager (spawns per-character cropped DWM popups)
        _cropManager = new CropManager(_discovery, _settings);
        _cropManager.AttachThumbnailManager(_thumbnailManager);
        PerfLog($"Core services created: {startupSw.ElapsedMilliseconds}ms");

        // ── START DISCOVERY IMMEDIATELY — thumbnails appear ASAP ──
        _discovery.Start();
        _thumbnailManager.StartFocusTracking();
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
            PerfLog($"[Deferred] AlertHub created: {deferSw.ElapsedMilliseconds}ms");

            // 7. Start log monitor and wire events
            _logMonitor = new LogMonitorService();
            _logMonitor.PveMode = _settings.Settings.PveMode;
            _logMonitor.SetCooldown(_settings.Settings.AlertCooldown);
            _logMonitor.SetSettings(_settings.Settings);

            if (_settings.Settings.EnabledAlertTypes != null)
                _logMonitor.SetEnabledAlertTypes(_settings.Settings.EnabledAlertTypes);
            if (_settings.Settings.SeverityCooldowns != null)
                _logMonitor.SetEventCooldowns(_settings.Settings.SeverityCooldowns);

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
            _logMonitor.AlertTriggered += (charName, alertType, severity) =>
            {
                Debug.WriteLine($"[App:Alert] ⚡ Alert: {alertType} [{severity}] for '{charName}'");
                var activeChars = _thumbnailManager.GetActiveCharacterNames();
                if (!activeChars.Any(n => string.Equals(n, charName, StringComparison.OrdinalIgnoreCase)))
                {
                    Debug.WriteLine($"[App:Alert] ⏭ Skipped — '{charName}' not in active windows");
                    return;
                }
                _thumbnailManager.SetAlertFlash(charName, severity, alertType);
                bool trayEnabled = _settings.Settings.SeverityTrayNotify.TryGetValue(severity, out var tn) && tn;

                // Show HUB toast only if the character's window is NOT the foreground window
                if (_settings.Settings.AlertHubEnabled && trayEnabled)
                {
                    var fgHwnd = Interop.User32.GetForegroundWindow();
                    var charHwnd = _thumbnailManager.GetHwndForCharacter(charName);
                    if (charHwnd == IntPtr.Zero || fgHwnd != charHwnd)
                        _alertHub.ShowToast(charName, alertType, severity);
                }

                PlayAlertSound(alertType, severity);
            };

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
            _logMonitor.Start("", chatLogDir, gameLogDir);
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
                    _trayIcon.Text = _hotkeyService.IsSuspended
                        ? "⏸ EVE MultiPreview (SUSPENDED)"
                        : "EVE MultiPreview";
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
                bool hasWindows = _thumbnailManager?.GetActiveCharacterNames()?.Any() == true;
                if (hasWindows != _lastHotkeyToggleState)
                {
                    PerfLog($"[Hotkey:Toggle] EVE windows present: {hasWindows} → {(hasWindows ? "ACTIVATE" : "DEACTIVATE")}");
                    _lastHotkeyToggleState = hasWindows;
                }
                if (hasWindows)
                    _hotkeyService?.ActivateHotkeys();
                else
                    _hotkeyService?.DeactivateHotkeys();
            };
            hotkeyToggleTimer.Start();

            PerfLog($"[Deferred] ✅ All deferred startup complete: {deferSw.ElapsedMilliseconds}ms total");

            // ── Auto-Update Check (fire-and-forget, non-blocking) ──
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
                            var dialog = new UpdateDialog(updateService);
                            dialog.ShowDialog();
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
        // Silently skip DWM race conditions (matches AHK behavior)
        if (e.Exception is System.Runtime.InteropServices.COMException ||
            e.Exception.Message.Contains("Missing a required parameter") ||
            e.Exception is System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"[App:Error] ⚠ Silently handled: {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
            return;
        }

        // Log all other errors
        LogError(e.Exception);
        e.Handled = true; // Keep running
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogError(ex);
    }

    private static void LogError(Exception ex)
    {
        try
        {
            string logPath = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? ".",
                "error_log.txt");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n" +
                           $"  Stack: {ex.StackTrace}\n";
            File.AppendAllText(logPath, entry);
            Debug.WriteLine($"[App:Error] ❌ Logged error: {ex.GetType().Name}: {ex.Message}");
        }
        catch { }
    }

    // ── JSON Migration (AHK: EVE-X-Preview → EVE MultiPreview) ───────

    private static void CheckJsonMigration()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var oldFile = Path.Combine(exeDir, "EVE-X-Preview.json");
        var newFile = Path.Combine(exeDir, "EVE MultiPreview.json");

        if (File.Exists(oldFile) && !File.Exists(newFile))
        {
            var result = MessageBox.Show(
                "Found settings from EVE-X-Preview. Migrate to EVE MultiPreview?",
                "Settings Migration", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    File.Copy(oldFile, newFile);
                    Debug.WriteLine("[App:Migration] ✅ Migrated EVE-X-Preview.json → EVE MultiPreview.json");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[App:Migration] ❌ Migration failed: {ex.Message}");
                }
            }
        }
    }

    // ── Alert Sound System (per-event sounds, WAV/MP3 via MediaPlayer) ──

    private void PlayAlertSound(string alertType, string severity)
    {
        var s = _settings?.Settings;
        if (s == null || !s.AlertSoundEnabled) return;

        // Per-event sound cooldown check
        string cooldownKey = $"sound_{alertType}";
        int soundCooldown = s.SoundCooldowns?.GetValueOrDefault(alertType, 0) ?? 0;
        if (soundCooldown > 0 && _soundCooldowns.TryGetValue(cooldownKey, out var lastPlay))
        {
            if ((DateTime.Now - lastPlay).TotalSeconds < soundCooldown)
            {
                Debug.WriteLine($"[AlertSound:Cooldown] ⏳ Sound cooldown active: {alertType} ({soundCooldown}s)");
                return;
            }
        }

        // Look up per-event sound file, fall back to global
        string? soundFile = null;
        if (s.AlertSounds?.TryGetValue(alertType, out var eventSoundFile) == true && !string.IsNullOrEmpty(eventSoundFile))
            soundFile = eventSoundFile;
        else if (!string.IsNullOrEmpty(s.AlertSoundFile))
            soundFile = s.AlertSoundFile;

        if (string.IsNullOrEmpty(soundFile) || !System.IO.File.Exists(soundFile))
        {
            Debug.WriteLine($"[AlertSound:Play] ⚠ No sound file for {alertType} (file='{soundFile ?? "null"}')");
            return;
        }

        try
        {
            // Use WPF MediaPlayer for MP3 support
            Dispatcher.Invoke(() =>
            {
                _soundPlayer?.Open(new Uri(soundFile));
                _soundPlayer!.Volume = (s.AlertSoundVolume / 100.0);
                _soundPlayer.Play();
            });

            _soundCooldowns[cooldownKey] = DateTime.Now;
            Debug.WriteLine($"[AlertSound:Play] 🔊 Playing '{System.IO.Path.GetFileName(soundFile)}' for {alertType} (vol={s.AlertSoundVolume}%)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlertSound:Play] ❌ Error playing sound: {ex.Message}");
        }
    }

    // ── Tray Menu (matches AHK TrayMenu.ahk) ────────────────────────

    private void SetupTrayIcon()
    {
        // Load icon from embedded resource (works with single-file publish)
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var iconStream = asm.GetManifestResourceStream("EveMultiPreview.Icon.ico");
        var trayIco = iconStream != null ? new System.Drawing.Icon(iconStream) : SystemIcons.Application;

        _trayIcon = new NotifyIcon
        {
            Text = "EVE MultiPreview",
            Icon = trayIco,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        // M3: Balloon click → focus attacked character (matches AHK NIN_BALLOONUSERCLICK)
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_trayAlertChar))
            {
                _thumbnailManager?.ActivateEveWindow(IntPtr.Zero, _trayAlertChar);
                Debug.WriteLine($"[Tray:BalloonClick] 🔧 Focused '{_trayAlertChar}' via balloon click");
            }
        };

        var menu = _trayIcon.ContextMenuStrip;

        // Header
        var header = menu.Items.Add("EVE MultiPreview");
        header.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());

        // Settings
        // Defer to let the tray menu fully close before Show() — otherwise
        // the NotifyIcon's internal message window steals foreground back
        // and Settings appears but can't receive input.
        menu.Items.Add("⚙ Settings", null, (_, _) =>
        {
            SettingsDiag("Tray menu item clicked");
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => OpenSettings()));
        });

        // Profile submenu (dynamically rebuilt to sync checks and profile list)
        var profileMenu = new ToolStripMenuItem("👤 Profiles");
        profileMenu.DropDownOpening += (_, _) => RebuildProfileMenu(profileMenu);
        RebuildProfileMenu(profileMenu);
        menu.Items.Add(profileMenu);

        menu.Items.Add(new ToolStripSeparator());

        // Suspend Hotkeys + AlertHub (C8)
        var suspendItem = new ToolStripMenuItem("⏸ Suspend Hotkeys") { CheckOnClick = true };
        suspendItem.Click += (_, _) =>
        {
            _hotkeyService?.ToggleSuspend();
            _alertHub?.SetSuspended(suspendItem.Checked);
        };
        menu.Items.Add(suspendItem);

        // Lock Positions toggle
        var lockItem = new ToolStripMenuItem("🔒 Lock Positions")
        {
            CheckOnClick = true,
            Checked = _settings?.Settings.LockPositions ?? false
        };
        lockItem.Click += (_, _) => _thumbnailManager?.ToggleLockPositions();
        menu.Items.Add(lockItem);

        // Hide/Show Thumbnails
        var hideItem = new ToolStripMenuItem("👁 Toggle Thumbnails") { CheckOnClick = true };
        hideItem.Click += (_, _) => _thumbnailManager?.ToggleThumbnailVisibility();
        menu.Items.Add(hideItem);

        // Click-Through
        var ctItem = new ToolStripMenuItem("↗ Click-Through Mode") { CheckOnClick = true };
        ctItem.Click += (_, _) => _thumbnailManager?.ToggleClickThrough();
        menu.Items.Add(ctItem);

        menu.Items.Add(new ToolStripSeparator());

        // Client position management
        var posMenu = new ToolStripMenuItem("📐 Client Positions");
        posMenu.DropDownItems.Add("💾 Save Positions", null, (_, _) =>
        {
            _thumbnailManager?.SaveClientPositions();
            _trayIcon?.ShowBalloonTip(2000, "EVE MultiPreview", "Client positions saved", ToolTipIcon.Info);
        });
        posMenu.DropDownItems.Add("📋 Restore Positions", null, (_, _) =>
        {
            _trayIcon?.ShowBalloonTip(2000, "EVE MultiPreview", "Positions restored on next discovery cycle", ToolTipIcon.Info);
        });
        menu.Items.Add(posMenu);

        // Close all EVE clients
        menu.Items.Add("❌ Close All EVE Clients", null, (_, _) =>
        {
            var result = System.Windows.MessageBox.Show(
                "Close all EVE Online windows?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                _thumbnailManager?.CloseAllEveWindows();
        });

        menu.Items.Add(new ToolStripSeparator());

        // ── PiP Individual Toggle Submenu (AHK: TrayMenu._TrayPiPToggle) ──
        var pipMenu = new ToolStripMenuItem("🖼 PiP Individual");
        try
        {
            var secondarySettings = _settings?.CurrentProfile.SecondaryThumbnails;
            if (secondarySettings != null)
            {
                foreach (var (charName, pipSettings) in secondarySettings)
                {
                    bool isEnabled = pipSettings.IsEnabled;

                    var capturedName = charName;
                    var pipItem = new ToolStripMenuItem(charName)
                    {
                        CheckOnClick = true,
                        Checked = isEnabled
                    };
                    pipItem.Click += (_, _) =>
                    {
                        // Toggle enabled state
                        if (secondarySettings.TryGetValue(capturedName, out var sts))
                        {
                            sts.IsEnabled = pipItem.Checked;
                            if (pipItem.Checked)
                                _thumbnailManager?.CreateSecondaryForCharacter(capturedName);
                            else
                                _thumbnailManager?.DestroySecondaryForCharacter(capturedName);
                            _settings?.Save();
                        }
                    };
                    pipMenu.DropDownItems.Add(pipItem);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Tray:PiP] ❌ Failed to build PiP submenu: {ex.Message}");
        }
        menu.Items.Add(pipMenu);

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        menu.Items.Add("🚪 Exit", null, (_, _) => ExitApplication());

        // Double-click to open settings (deferred — see Settings menu item above)
        _trayIcon.DoubleClick += (_, _) =>
        {
            SettingsDiag("Tray double-click");
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => OpenSettings()));
        };


    }

    private void RebuildProfileMenu(ToolStripMenuItem profileMenu)
    {
        profileMenu.DropDownItems.Clear();
        if (_settings == null) return;

        foreach (var name in _settings.GetProfileNames())
        {
            var item = new ToolStripMenuItem(name)
            {
                Checked = name == _settings.Settings.LastUsedProfile
            };
            item.Click += (_, _) =>
            {
                _settings.SwitchProfile(name);
                
                _thumbnailManager?.ReapplySettings();

                _hotkeyService?.RegisterFromSettings(
                    _settings.Settings, _settings.CurrentProfile,
                    _thumbnailManager!, OpenSettings);
                Debug.WriteLine($"[App:Profile] 🔄 Switched to profile: {name}");
            };
            profileMenu.DropDownItems.Add(item);
        }
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
        _settings.SwitchProfile(newProfile);

        // Apply the new profile's settings to all running thumbnails
        _thumbnailManager?.ReapplySettings();
        _cropManager?.Refresh();

        // Re-register hotkeys for new profile (AHK does Reload() which re-runs __New)
        _hotkeyService?.RegisterFromSettings(
            _settings.Settings, _settings.CurrentProfile,
            _thumbnailManager!, OpenSettings);

        // Tooltip feedback (AHK L705: ToolTip("Profile: " newProfile))
        _thumbnailManager?.ShowTooltipFeedback($"Profile: {newProfile}");

        Debug.WriteLine($"[App:Profile] 🔄 Cycled {(forward ? "forward" : "backward")} to profile: {newProfile}");
    }

    private void OpenSettings() => OpenSettings(startMinimized: false);

    private void OpenSettings(bool startMinimized)
    {
        SettingsDiag($"OpenSettings called, startMinimized={startMinimized}, existing={_settingsWindow != null}");
        if (_settingsWindow != null && _settingsWindow.IsVisible)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;

            _settingsWindow.Activate();
            SettingsDiag("Re-activated existing Settings window");
            return;
        }

        _settingsWindow = new SettingsWindow(_settings!, _thumbnailManager!, _cropManager);
        // Set initial WindowState BEFORE Show() so WPF commits the restore rect
        // with the saved Width/Height already applied. Setting WindowState after
        // Show() can race the HWND map and corrupt the restore rect.
        if (startMinimized)
            _settingsWindow.WindowState = WindowState.Minimized;
        
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
            }

            // Dynamically show/hide the Alert Hub based on settings toggle
            if (_isShuttingDown) return;
            try
            {
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

        _settingsWindow.SettingsApplied += applyLiveSettings;

        _settingsWindow.Closed += (_, _) =>
        {
            SettingsDiag("Closed event fired");
            // Ensure topmost + click-through are restored when settings closes
            _thumbnailManager?.SetSuppressTopmost(false);
            _thumbnailManager?.SetSettingsClickSuppression(false);
            _thumbnailManager?.SetSettingsOpen(false);

            _settingsWindow = null;
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
        var fg = Interop.User32.GetForegroundWindow();
        SettingsDiag($"After Activate: settingsHwnd=0x{settingsHwnd.ToInt64():X}, foreground=0x{fg.ToInt64():X}, match={settingsHwnd == fg}");

        // Tray-menu opens don't always fire Activated. Apply suppressions
        // synchronously so Settings is usable regardless of how it was opened.
        _thumbnailManager?.SetSuppressTopmost(true);
        _thumbnailManager?.SetSettingsClickSuppression(true);
        _thumbnailManager?.SetSettingsOpen(true);
        SettingsDiag("OpenSettings finished");
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
        // Single disposal path — ExitApplication calls Shutdown() which triggers this
        _alertHub?.Dispose();
        _logMonitor?.Dispose();
        _hotkeyService?.Dispose();
        _processMonitor?.Dispose();
        _cropManager?.Dispose();
        _thumbnailManager?.Dispose();
        _discovery?.Dispose();
        _settings?.Dispose();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        base.OnExit(e);
    }


}
