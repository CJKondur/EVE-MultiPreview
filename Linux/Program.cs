using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Themes.Simple;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        // A command line that is a command ("--focus Alpha") goes to the running MultiPreview and this start ends with its outcome.
        CommandSocket.SendFromCommandLine(args)
        ?? AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var previews = new Previews();
        previews.Start();
        previews.TrayMenu = new Tray(this, previews);
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Keeps one thumbnail per EVE client: looks for clients every 2 s, redraws every 100 ms, and draws each
/// thumbnail as the settings say (the same settings file and rules as the Windows app). Prints the average time a
/// redraw of all thumbnails takes every 10 s.</summary>
public sealed class Previews : ITrayActions, ISettingsHost
{
    private readonly EveClients _clients = new();
    private readonly SettingsService _settings;
    private readonly Dictionary<uint, ThumbnailWindow> _thumbs = new();
    private readonly Dictionary<string, IBrush> _brushes = new();
    private readonly GlobalHotkeys _hotkeys = new();
    private int _ticks;
    private double _drawMs;
    private uint _lastActive, _previousActive;
    private bool _allHidden;
    private Alerts? _alerts;
    private SharedSettingsWindow? _settingsWindow;
    private AlertHistoryWindow? _historyWindow;
    /// <summary>The alerts the player has not looked at yet, and the walk "Go to the last alert" makes through them.</summary>
    private readonly AlertAttention _attention = new();
    private readonly AlertWalk _alertWalk = new();

    /// <summary>The tray icon's menu, rebuilt when a setting it shows changes.</summary>
    public Tray? TrayMenu { get; set; }

    /// <summary>A thumbnail's alert border: its colour, flash rate, when it ends (null: when the player switches to
    /// that client) and whether it is steady.</summary>
    private sealed record AlertBorder(IBrush Brush, int RateMs, DateTime? Until, bool Steady, string AlertType);
    private readonly Dictionary<string, AlertBorder> _alertBorders = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ThumbnailManager.FlashExpiry: how long an alert shows when its event has no time of its own.</summary>
    private static readonly Dictionary<string, int> DefaultSeconds = new() { ["critical"] = 8, ["warning"] = 6, ["info"] = 4 };

    public Previews()
    {
        // ~/.config/EVE MultiPreview. Asked without the existence check: on a new account ~/.config may not exist yet,
        // and the check would then give "" (the Windows app's AppPaths.AppDataDir keeps the check).
        var configHome = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        var home = SettingsHome.Resolve(AppPaths.ExeDir, Path.Combine(configHome, AppPaths.AppDataFolderName));
        AppPaths.Use(home.ConfigDir);
        foreach (var line in home.Notes.Concat(home.Errors)) Console.WriteLine(line);
        _settings = new SettingsService(AppPaths.SettingsPath);
        _settings.Load();
        LocalizationService.SetLanguage(_settings.Settings.Language);
    }

    private AppSettings S => _settings.Settings;

    /// <summary>IDEAS #55: the marker that tells a crash from a clean exit (App's RunMarker); the Diagnostics page says when
    /// the last session did not end cleanly.</summary>
    private RunMarker? _runMarker;

    public void Start()
    {
        CatchStrays();
        _runMarker = new RunMarker(AppPaths.ConfigDir);
        RunMarker.PreviousUncleanStart = _runMarker.Begin(DateTime.Now);
        DiagnosticsService.Initialize();   // the Logs folder the debug logs are written in (App › Diagnostics)
        DiagnosticsService.GlobalSettings = S;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _runMarker?.End();   // a SIGTERM (logging out) is a clean end too
        Console.WriteLine($"EVE MultiPreview for Linux: settings {AppPaths.SettingsPath}; looking for windows titled \"{_clients.TitlePrefix}…\" "
            + $"(Composite: {(_clients.HasComposite ? "yes" : "no, covered clients will not show")}; scaling on the X server: {(_clients.ScalesOnServer ? "yes" : "no")})");
        BindHotkeys();
        _settings.ProfileSwitched += OnProfileSwitched;
        _settings.ProfileSwitched += _ => { if (!_switchingProfileAutomatically) _autoProfile.ManualSwitch(DateTime.UtcNow); };
        _alerts = new Alerts(_settings, RunningCharacters, WindowOf, _clients.Active, Flash, _attention, SwitchToCharacter, SnoozeEvent);
        var (chat, game) = _alerts.Start();
        Console.WriteLine($"Logs: chat {(chat.Length > 0 ? chat : "not found")}; game {(game.Length > 0 ? game : "not found")}");
        new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick()).Start();
        Tick();
        // Commands from other programs (IDEAS #39): taken only while AcceptCommands is on, run on the UI thread.
        CommandSocket.Serve(command =>
        {
            if (!S.AcceptCommands) return RemoteOutcome.Refused;
            try
            {
                var task = Dispatcher.UIThread.InvokeAsync(() => RunCommand(command)).GetTask();
                return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result : RemoteOutcome.NotReachable;   // the UI thread is stuck: "never ran"
            }
            catch (Exception) { return RemoteOutcome.Refused; }
        });
        // The update check at start, a little after it, so it never slows the start (CheckForUpdatesOnStartup, as on Windows).
        if (S.CheckForUpdatesOnStartup) DispatcherTimer.RunOnce(() => _ = CheckForUpdates(quiet: true), TimeSpan.FromSeconds(15));
        _probeTimer = new System.Threading.Timer(_ => ProbeClients(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        // EVEMP_OPEN_SETTINGS=<page id>, or a page number of the old window (from 0): opens the settings window at startup (the tests use it).
        if (Environment.GetEnvironmentVariable("EVEMP_OPEN_SETTINGS") is { Length: > 0 } page) OpenSettings(OldPageNumbers.ElementAtOrDefault(int.TryParse(page, out int n) ? n : -1) ?? page);
        // EVEMP_OPEN_HISTORY=1: opens the alert history window at startup (the tests use it).
        if (Environment.GetEnvironmentVariable("EVEMP_OPEN_HISTORY") == "1") OpenAlertHistory();
        // "When MultiPreview starts" (StartupSettings): the settings window opens by itself, minimized when asked, half a
        // second in (App's startup timer). Linux has no setup wizard, so SetupCompleted is not asked.
        if (S.StartupSettings != StartupSettingsMode.Off && _settingsWindow == null)
            DispatcherTimer.RunOnce(() =>
            {
                OpenSettings();
                if (S.StartupSettings == StartupSettingsMode.OpenMinimized && _settingsWindow != null) _settingsWindow.WindowState = WindowState.Minimized;
            }, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>The last resort: an exception no handler caught (a settings card's side effect, a menu, a timer) is logged
    /// and said in the settings window instead of ending the app and every thumbnail with it.</summary>
    private void CatchStrays()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Console.Error.WriteLine($"[App] unhandled {e.Exception.GetType().Name}: {e.Exception.Message}");
            try
            {
                ErrorLog.Shared.Record(e.Exception, "App", DateTime.Now);
                _settingsWindow?.Toast(e.Exception is PlatformNotSupportedException
                    ? string.Format(LocalizationService.Str("L.Linux.NotYet", "Not available on Linux yet: {0}"), e.Exception.Message)
                    : e.Exception.GetType().Name);
            }
            catch (Exception) { }   // the last resort never throws itself
        };
        // Off the UI thread nothing can be caught: the reason is written down before the runtime ends the process.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Console.Error.WriteLine($"[App] fatal {ex?.GetType().Name}: {ex?.Message}");
            try { if (ex != null) ErrorLog.Shared.Record(ex, "App", DateTime.Now); } catch (Exception) { }
        };
    }

    // ── Tray ──

    private bool _hotkeysPaused;

    public bool HotkeysPaused
    {
        get => _hotkeysPaused;
        set { _hotkeysPaused = value; BindHotkeys(); }   // paused: only the pause key stays (HotkeyService.ToggleSuspend)
    }

    /// <summary>The pause key (ThumbnailManager.ToggleSuspend's feedback): pauses or resumes every other key, and says so.</summary>
    private void TogglePause()
    {
        HotkeysPaused = !HotkeysPaused;
        Alerts.Say(_hotkeysPaused ? LocalizationService.Str("L.Msg.HotkeysSuspended", "Hotkeys: SUSPENDED") : LocalizationService.Str("L.Msg.HotkeysActive", "Hotkeys: ACTIVE"));
        TrayMenu?.Rebuild();
    }

    /// <summary>"Hide primary thumbnails" (HidePrimaryHotkey, ThumbnailManager.TogglePrimaryVisibility): runtime only. The second
    /// thumbnails and the crop windows stay.</summary>
    private bool _primaryHidden;

    private bool _clickThrough;

    /// <summary>Click-through (ClickThroughHotkey, Home › Quick actions; ThumbnailManager.ToggleClickThrough): every thumbnail and
    /// second thumbnail lets the mouse through to what is under it, and can't be dragged, until it is switched off. Runtime only.</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (value == _clickThrough) return;
            _clickThrough = value;
            Alerts.Say(value ? LocalizationService.Str("L.Msg.ClickThroughOn", "Thumbnails: Click-Through ON") : LocalizationService.Str("L.Msg.ClickThroughOff", "Thumbnails: Click-Through OFF"));
        }
    }

    /// <summary>Gives a thumbnail or second thumbnail the click-through state; asked again every 2 s while on (a window manager
    /// that frames a window after its shape was set keeps a frame that catches the mouse: see the crop windows).</summary>
    private void FollowClickThrough(ThumbnailWindow window)
    {
        if ((window.PassesClicks != _clickThrough || _clickThrough && _ticks % 20 == 0) && window.TryGetPlatformHandle() is { } handle
            && _clients.PassClicks((uint)(long)handle.Handle, _clickThrough))
            window.PassesClicks = _clickThrough;
    }

    /// <summary>ThumbnailManager.CloseAllClients (CloseAllClientsHotkey): asks first (Cancel is the default), then ends every
    /// running EVE client's process, as the Windows app ends them. Only an EVE process (exefile) is ever ended.</summary>
    private void CloseAllClients()
    {
        var clients = _thumbs.Values.Select(t => t.Client).ToList();
        if (clients.Count == 0) return;
        var question = new ChoiceDialog(string.Format(LocalizationService.Str("L.Msg.CloseAllConfirm", "End all {0} EVE clients now?"), clients.Count),
            Ui.NoAccess(LocalizationService.Str("L.S.Shell.Ok", "OK")), Ui.NoAccess(LocalizationService.Str("L.S.Shell.Cancel", "Cancel")));
        question.Answered += yes =>
        {
            if (!yes) return;
            foreach (var client in clients)
            {
                uint pid = _clients.Pid(client.Window);
                if (pid == 0 || !IsEveProcess(pid)) continue;
                try { using var p = Process.GetProcessById((int)pid); p.Kill(); }
                catch (Exception) { }   // already gone, or not ours to end
            }
        };
        question.Show();
    }

    public bool PositionsLocked
    {
        get => S.LockPositions;
        set { S.LockPositions = value; _settings.SaveDelayed(); }
    }

    public bool ThumbnailsHidden { get => _allHidden; set => _allHidden = value; }

    /// <summary>"Hide second thumbnails" (the tray's item and HideSecondaryHotkey): runtime only, as on Windows.</summary>
    public bool SecondThumbnailsHidden { get => _pipsHidden; set => _pipsHidden = value; }

    private bool _pipsHidden;

    // ── Focus mode (IDEAS #35: App.ToggleFocusMode / FollowFocusMode) ──

    private bool _focusOn;
    private readonly FocusArrivals _focusArrivals = new();
    /// <summary>The characters whose thumbnails, second thumbnails and crop windows focus mode holds hidden now.</summary>
    private IReadOnlySet<string> _focusHeld = FocusMode.Nobody;

    /// <summary>Focus mode on or off (its hotkey and the tray's item). While off a press turns it on only when there is a
    /// group to show; it says what came of it.</summary>
    public bool FocusModeOn
    {
        get => _focusOn;
        set { if (value != _focusOn) ToggleFocusMode(); }
    }

    private void ToggleFocusMode()
    {
        var (chosen, _) = FocusMode.Options(S);
        var group = FocusMode.GroupFor(chosen, ActiveCharacter(), FocusMode.Groups(S));
        var press = FocusMode.Press(_focusOn, chosen, group);
        _focusOn = press == FocusPress.On;
        if (press == FocusPress.Off) _focusHeld = FocusMode.Nobody;
        else if (press == FocusPress.On) _focusArrivals.Begin(RunningCharacters());
        Alerts.Say(FocusMode.Say(press, press == FocusPress.On ? group!.Value.Name : chosen));
        TrayMenu?.Rebuild();
        FollowFocusMode();
    }

    /// <summary>Twice a second while focus mode is on: the group to show (the chosen one, or the one of the client in
    /// front) and so who is held; a client with an unseen alert, or one that just logged in, shows through.</summary>
    private void FollowFocusMode()
    {
        if (!_focusOn) return;
        var (chosen, alertsShow) = FocusMode.Options(S);
        var group = FocusMode.GroupFor(chosen, ActiveCharacter(), FocusMode.Groups(S));
        var running = RunningCharacters();
        var arrived = _focusArrivals.Look(DateTime.UtcNow, running);
        _focusHeld = FocusMode.Held(group, running, alertsShow, _attention.Has, arrived.Contains);
    }

    // ── Keep the PC awake (IDEAS #46) and the downtime warnings (IDEAS #9): App's half-second followers ──

    private readonly LinuxKeepAwake _keepAwake = new();
    private readonly DowntimeWarnings _downtimeWarnings = new();

    private void FollowKeepAwake()
    {
        try { _keepAwake.Follow(KeepAwake.Wanted(S, RunningCharacters())); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorLog.Shared.Record(ex, "App", DateTime.Now); }
    }

    /// <summary>App.FollowDowntime: nothing while the switch is off or nobody is logged in (so a warning is not used up
    /// unseen); then, when one is due (15 and 5 minutes before 11:00 UTC), a notification naming every logged-in character.</summary>
    private void FollowDowntime()
    {
        if (!S.DowntimeWarnings) return;
        var names = RunningCharacters().ToList();
        if (names.Count == 0) return;
        if (_downtimeWarnings.Due(DateTime.UtcNow) is int minutes) Alerts.Say(Downtime.WarningText(minutes, names), 10000);
    }

    // ── Switch profiles by who is logged in (IDEAS #11: App.FollowAutoProfile) ──

    private readonly AutoProfile _autoProfile = new();
    /// <summary>True only around a switch AutoProfile asked for, so ProfileSwitched does not count it as the player's own.</summary>
    private bool _switchingProfileAutomatically;
    private ChoiceDialog? _autoProfileQuestion;

    /// <summary>Twice a second: while "Switch profiles by who is logged in" is on, has the set of logged-in characters
    /// settled on another profile? Switch to it, or ask when the player switched himself a short while ago. While the switch
    /// is off nothing is weighed and an open question is closed without a switch.</summary>
    private void FollowAutoProfile()
    {
        try
        {
            if (!AutoProfile.IsOn(S)) { _autoProfile.Forget(); CloseAutoProfileQuestion(); return; }
            var choice = _autoProfile.Look(DateTime.UtcNow, RunningCharacters(), AutoProfile.Candidates(S), S.LastUsedProfile);
            if (choice == null) return;
            if (choice.Ask) AskAutoProfile(choice.Profile);
            else SwitchProfileAutomatically(choice.Profile);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorLog.Shared.Record(ex, "App", DateTime.Now); }
    }

    /// <summary>Switches as the tray does (OnProfileSwitched places the thumbnails), not counted as the player's own switch,
    /// and says so. A profile deleted meanwhile is not switched to.</summary>
    private void SwitchProfileAutomatically(string profile)
    {
        if (!S.Profiles.ContainsKey(profile)) return;
        _switchingProfileAutomatically = true;
        try { _settings.SwitchProfile(profile); }
        finally { _switchingProfileAutomatically = false; }
        TrayMenu?.Rebuild();
        Alerts.Say(string.Format(LocalizationService.Str("L.AutoProfile.Switched", "Profile: {0}, picked by who is logged in"), StreamerMask.Name(StreamerKind.Profile, profile)));
    }

    private void CloseAutoProfileQuestion()
    {
        var open = _autoProfileQuestion;
        if (open == null) return;
        _autoProfileQuestion = null;   // cleared first: its Answered finds no current question and switches nothing
        open.Close();
    }

    /// <summary>The player switched profiles himself a short while ago, so the switch is a question that does not take the
    /// focus from the game. A newer question replaces an older one; Switch is not counted as the player's own switch.</summary>
    private void AskAutoProfile(string profile)
    {
        var current = S.LastUsedProfile;
        var shownProfile = StreamerMask.Name(StreamerKind.Profile, profile);
        var shownCurrent = StreamerMask.Name(StreamerKind.Profile, current);
        CloseAutoProfileQuestion();
        var question = new ChoiceDialog(
            string.Format(LocalizationService.Str("L.AutoProfile.Body", "The characters logged in now belong to the profile {0}. You switched profiles yourself a few minutes ago, so MultiPreview asks before it leaves {1}."), shownProfile, shownCurrent),
            string.Format(LocalizationService.Str("L.AutoProfile.Switch", "Switch to {0}"), shownProfile),
            string.Format(LocalizationService.Str("L.AutoProfile.Stay", "Stay on {0}"), shownCurrent))
        {
            Title = LocalizationService.Str("L.AutoProfile.Title", "Switch profile?"),
            ShowActivated = false,
        };
        question.Answered += yes =>
        {
            bool latest = ReferenceEquals(_autoProfileQuestion, question);
            if (latest) _autoProfileQuestion = null;
            // Clicked long after it came up: the switch must still be on and the profile still there.
            if (yes && latest && AutoProfile.IsOn(S) && S.Profiles.ContainsKey(profile)) SwitchProfileAutomatically(profile);
        };
        _autoProfileQuestion = question;
        question.Show();
    }

    // ── Updates (App's update check, Settings > General) ──

    private bool _checkingUpdate;
    private UpdateDialog? _updateDialog;

    /// <summary>Asks GitHub for a newer release (the only network call, at start when CheckForUpdatesOnStartup is on, or on
    /// the button) and offers the update dialog when there is one. The text to show beside the button, or null.</summary>
    public async Task<string?> CheckForUpdates(bool quiet)
    {
        if (_checkingUpdate || _updateDialog != null) return null;
        _checkingUpdate = true;
        try
        {
            var service = new UpdateService();
            if (await service.CheckForUpdateAsync(S.ReceivePreReleaseUpdates))
            {
                var dialog = new UpdateDialog(service, Exit);
                dialog.Closed += (_, _) => _updateDialog = null;
                _updateDialog = dialog;
                dialog.Show();
                return null;
            }
            return quiet ? null : string.Format(LocalizationService.Str("L.S.About.UpToDate", "You're up to date ({0})."), service.CurrentVersion);
        }
        catch (Exception e)
        {
            return quiet ? null : string.Format(LocalizationService.Str("L.S.About.CheckFailed", "Couldn't check for updates: {0}"), e.Message);
        }
        finally { _checkingUpdate = false; }
    }

    // ── Commands (App.RunCommand) and the command palette (IDEAS #14) ──

    private CommandPaletteWindow? _palette;

    /// <summary>App.ToggleCommandPalette: opens the palette over the screen under the pointer, or closes the open one. Its
    /// rows are the running characters, their labels, the groups and the profiles; the chosen row runs through RunCommand.</summary>
    private void ToggleCommandPalette()
    {
        if (_palette != null) { var open = _palette; _palette = null; open.Dismiss(); return; }
        var entries = CommandPalette.Entries(RunningCharacters(), S.ThumbnailAnnotations,
            FocusMode.Groups(S).Select(g => g.Name), _settings.GetProfileNames());
        var palette = new CommandPaletteWindow(entries, command => RunCommand(command));
        palette.Closed += (_, _) => { if (ReferenceEquals(_palette, palette)) _palette = null; };
        _palette = palette;
        palette.ShowAt(_clients.Pointer() is { } p ? new PixelPoint(p.X, p.Y) : null);
        // A window manager that holds back focus from a window shown while the game has it (KWin's focus-stealing guard) is
        // asked once more, as a pager asks; a palette still not in front closes: a box that can't take the keyboard would
        // leave the typing going to the game (the Windows palette's 400 ms rule).
        DispatcherTimer.RunOnce(() =>
        {
            if (_palette != palette || palette.IsActive) return;
            if (palette.TryGetPlatformHandle() is { } handle) _clients.Activate((uint)(long)handle.Handle);
            DispatcherTimer.RunOnce(() => { if (_palette == palette && !palette.IsActive) ToggleCommandPalette(); }, TimeSpan.FromMilliseconds(400));
        }, TimeSpan.FromMilliseconds(400));
    }

    /// <summary>App.RunCommand: one command (the palette's row) through the shared RemoteCommandRunner, with what Linux has.
    /// Minimising and restoring every client are not offered on Linux.</summary>
    private RemoteOutcome RunCommand(RemoteCommand command)
    {
        try
        {
            return new RemoteCommandRunner
            {
                Running = RunningCharacters,
                Profiles = () => _settings.GetProfileNames(),
                Focus = SwitchTo,
                SwitchProfile = name => Profile = name,
                ToggleThumbnails = () => _allHidden = !_allHidden,
                ToggleFocusMode = ToggleFocusMode,
                Groups = () => FocusMode.Groups(S).Select(g => g.Name),
                CycleGroup = (name, forward) =>
                {
                    var group = FocusMode.Groups(S).FirstOrDefault(g => g.Name == name);
                    if (group.Members != null) Cycle(group.Members, forward);
                },
                CycleAll = forward => Cycle(null, forward),
                PreviousClient = () => { if (_previousActive != 0) ActivateClient(_previousActive); },
                LastAlert = GoToLastAlert,
                CycleProfile = CycleProfile,
                DoNotDisturb = ToggleDoNotDisturb,
                IsDoNotDisturbOn = () => DoNotDisturb,
                IsFocusModeOn = () => _focusOn,
                SetAlertMute = (name, mute) => Mute(name, mute ? ThumbnailMenu.LongestTimedMuteMinutes : 0),
                OpenSettings = OpenSettings,
                OpenAlertHistory = OpenAlertHistory,
                IsNameBarOn = () => S.NameBarMode,
                SetNameBar = on =>
                {
                    if (S.NameBarMode == on) return;
                    S.NameBarMode = on;
                    _settings.SaveDelayed();
                    FollowNameBar();
                    Alerts.Say(on ? LocalizationService.Str("L.NameBar.On", "Name bar: on") : LocalizationService.Str("L.NameBar.Off", "Name bar: off"));
                },
                MarkAlertsSeen = () => _attention.SeeAll(),
                MuteFor = Mute,
            }.Run(command);
        }
        catch (Exception e)
        {
            ErrorLog.Shared.Record(e, "App", DateTime.Now);
            return RemoteOutcome.Refused;
        }
    }

    /// <summary>App.CycleProfile: the next or previous profile, round the ends.</summary>
    private void CycleProfile(bool forward)
    {
        var names = _settings.GetProfileNames();
        if (names.Length <= 1) return;
        int at = Math.Max(0, Array.IndexOf(names, S.LastUsedProfile));
        Profile = names[((at + (forward ? 1 : -1)) % names.Length + names.Length) % names.Length];
        TrayMenu?.Rebuild();
    }

    // ── The name bar (IDEAS #38: App.FollowNameBar) ──

    private NameBarWindow? _nameBar;

    /// <summary>Twice a second: with NameBarMode on, the bar is made or asked for its buttons again (the main thumbnails are
    /// held hidden meanwhile, in Tick); off, it is closed.</summary>
    private void FollowNameBar()
    {
        if (!S.NameBarMode)
        {
            var open = _nameBar;
            _nameBar = null;
            open?.Close();
            return;
        }
        if (_nameBar != null) { _nameBar.Refresh(); return; }
        var bar = new NameBarWindow(NameBarEntries, SwitchTo,
            name => _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, name, StringComparison.OrdinalIgnoreCase))?.Client);
        bar.Opened += (_, _) => { if (bar.TryGetPlatformHandle() is { } handle) _clients.NoFocus((uint)(long)handle.Handle); };
        bar.Closed += (_, _) => { if (ReferenceEquals(_nameBar, bar)) _nameBar = null; };
        _nameBar = bar;
        bar.Show();
    }

    /// <summary>App.NameBarEntries: the running characters in the quick-switch order, each with its first colour group's
    /// colour, its worst unseen alert and whether its client is in front.</summary>
    private IReadOnlyList<NameBarEntry> NameBarEntries()
    {
        uint front = _clients.Active();
        string active = _thumbs.TryGetValue(front, out var t) ? t.Client.Character : "";
        return NameBar.Entries(RunningCharacters().ToList(),
            S.ThumbnailGroups.Select(g => (g.Name, (string?)g.Color, (IReadOnlyList<string>)g.Members)).ToList(),
            name => _attention.For(name)?.Severity, active, S.QuickSwitchCardOrder,
            name => _attention.Top(name)?.EventKey);
    }

    /// <summary>ThumbnailManager.ActiveCharacter: the character of the client last in front (still running), or "".</summary>
    private string ActiveCharacter() => _thumbs.TryGetValue(_lastActive, out var t) ? t.Client.Character : "";
    /// <summary>The second thumbnails (PiP), by character: one for each running client whose character has one switched on.</summary>
    private readonly Dictionary<string, ThumbnailWindow> _pips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Crop windows hidden by HideShowCropsHotkey (runtime only, as on Windows). Opening the settings or switching
    /// profile shows them again (CropManager.ShowCrops, issue #81: a stray press must not leave them hidden for good).</summary>
    private bool _cropsHidden;

    /// <summary>A crop window on screen: its window, the place and size from its definition it was last put on (a change
    /// on the settings page moves it), and whether clicks pass through it now (null: not set yet).</summary>
    private sealed class CropShown(ThumbnailWindow window, string character)
    {
        public ThumbnailWindow Window { get; } = window;
        public string Character { get; } = character;
        public (int X, int Y, int W, int H) Placed;
        public bool? Through;
    }

    /// <summary>The crop windows (CropManager), by client window and crop id: one for each crop of each running
    /// client's character while crop windows are on (CropEnabled).</summary>
    private readonly Dictionary<(uint Window, string Id), CropShown> _crops = new();

    public bool DoNotDisturb
    {
        get => _alerts?.DoNotDisturbOn ?? false;
        set { if (_alerts != null) _alerts.DoNotDisturbOn = value; }
    }

    /// <summary>Streamer mode (the Windows tray's item and the Thumbnails page's switch). Everything that draws a name asks
    /// StreamerMask each time, so a change shows at the next redraw (the thumbnails redraw every 100 ms).</summary>
    public bool StreamerMode
    {
        get => S.StreamerMode;
        set { S.StreamerMode = value; _settings.SaveDelayed(); }
    }

    public IReadOnlyList<string> Profiles => _settings.GetProfileNames();

    /// <summary>Switching profile places every thumbnail where the new profile keeps it, and binds its keys.</summary>
    public string Profile
    {
        get => S.LastUsedProfile;
        set
        {
            if (value == S.LastUsedProfile) return;
            _settings.SwitchProfile(value);   // OnProfileSwitched places the thumbnails and binds the keys
        }
    }

    /// <summary>A profile was switched, from the tray or from the settings window (App › Profiles): every thumbnail goes
    /// where the new profile keeps it, and its keys are bound.</summary>
    private void OnProfileSwitched(string _)
    {
        ResetThumbnails();
        BindHotkeys();
    }

    /// <summary>Closes every thumbnail and makes them again, each where the current profile keeps it.</summary>
    private void ResetThumbnails()
    {
        foreach (var thumb in _thumbs.Values.Concat(_pips.Values).Concat(_crops.Values.Select(c => c.Window))) thumb.Close();
        _thumbs.Clear();
        _pips.Clear();
        _crops.Clear();
        _cropsHidden = false;   // CropManager.ShowCrops: switching profile brings hidden crops back (issue #81)
        Sync();
    }

    public void OpenSettings() => OpenSettings(null);

    /// <summary>The pages of the old hand-made window by its numbers, for the test scripts that still pass a number.</summary>
    private static readonly string[] OldPageNumbers =
        { "app.general", ThumbnailsPages.AppearanceId, CharacterPage.CropsPageId, ClientsPages.CharactersId, ClientsPages.GroupsId, ClientsPages.HotkeysId, AlertsPages.EventsId };

    /// <summary>Opens the settings window (drawn from the shared page definitions), or brings the open one forward. With a
    /// page id, that page shows; without one, an open window keeps its page and a new one starts at Home.</summary>
    private void OpenSettings(string? pageId)
    {
        _cropsHidden = false;   // CropManager.ShowCrops: opening the settings brings hidden crops back (issue #81)
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            if (pageId != null) _settingsWindow.ShowPage(pageId);
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SharedSettingsWindow(this, pageId);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; CaptureHotkey(false); };
        _settingsWindow.Show();
    }

    /// <summary>Opens the alert history window (the tray's "Alert history…" and the Alerts page's button), or brings the open one forward.</summary>
    public void OpenAlertHistory()
    {
        if (_alerts == null) return;
        if (_historyWindow != null)
        {
            if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new AlertHistoryWindow(_alerts.History, new AlertHistoryActions(
            SwitchToCharacter, () => S.AlertHistoryEnabled, () => _settings.LoadedSuccessfully, () => S));
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    /// <summary>Brings the character's client to the front (the history window's "Switch to client"). False when it isn't running.</summary>
    private bool SwitchToCharacter(string character)
    {
        uint window = WindowOf(character);
        if (window == 0) return false;
        ActivateClient(window);
        return true;
    }

    // ── Settings window ──

    SettingsService ISettingsHost.Settings => _settings;

    IReadOnlyCollection<string> ISettingsHost.RunningCharacters() => RunningCharacters();

    // Not while a hotkey box listens: binding again would grab the key the player is pressing (CaptureHotkey(false) binds).
    void ISettingsHost.HotkeysChanged() { if (!_capturing) BindHotkeys(); }

    /// <summary>While a hotkey box listens, the keys are not grabbed: a key that is already a hotkey would go to the
    /// grab and never reach the window.</summary>
    public void CaptureHotkey(bool capturing)
    {
        _capturing = capturing;
        if (capturing) _hotkeys.Bind(Array.Empty<(string, Action)>());
        else BindHotkeys();
    }

    private bool _capturing;
    /// <summary>Whether the keys are wanted right now: always with "Everywhere" (GlobalHotkeys), otherwise only while an
    /// EVE client or a MultiPreview window is in front, as on Windows. Out of that scope the keys are let go, so a
    /// bound "1" types normally in every other program.</summary>
    private bool _keysInScope = true;

    void ISettingsHost.AlertsChanged() => _alerts?.Reapply();

    LinuxSpeaker? ISettingsHost.Speaker => _alerts?.Speaker;

    void ISettingsHost.VoiceAnswered() => _alerts?.VoiceAnswered();

    Task<PushResult> ISettingsHost.TestPush() => _alerts?.TestPush() ?? Task.FromResult(new PushResult(PushOutcome.Refused));

    PushStatus? ISettingsHost.PushStatus => _alerts?.PushStatus;

    void ISettingsHost.TrayChanged() => TrayMenu?.Rebuild();

    void ISettingsHost.Imported()
    {
        ResetThumbnails();
        BindHotkeys();
        _alerts?.Reapply();
        TrayMenu?.Rebuild();
    }

    public void Exit()
    {
        _probeTimer?.Dispose();
        _nameBar?.Close();
        _keepAwake.Dispose();
        _runMarker?.End();
        _alerts?.Dispose();
        _settings.Dispose();
        foreach (var thumb in _thumbs.Values.Concat(_pips.Values).Concat(_crops.Values.Select(c => c.Window))) thumb.Close();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ── Hotkeys ──

    internal void BindHotkeys()
    {
        // The pause key (SuspendHotkey) is bound on its own, as HotkeyService registers it apart from the others: it works
        // while the keys are paused and out of their scope, so a press resumes them.
        var pause = string.IsNullOrWhiteSpace(S.SuspendHotkey) ? new List<(string, Action)>() : new List<(string, Action)> { (S.SuspendHotkey, TogglePause) };
        if (_hotkeysPaused || !_keysInScope) { _hotkeys.Bind(pause); return; }
        var keys = new List<(string, Action)>(pause);
        foreach (var (character, binding) in _settings.CurrentProfile.Hotkeys)
            keys.Add((binding.Key, () => SwitchTo(character)));
        foreach (var group in S.HotkeyGroups.Values)
        {
            keys.Add((group.ForwardsHotkey, () => Cycle(group.Characters, true)));
            keys.Add((group.BackwardsHotkey, () => Cycle(group.Characters, false)));
        }
        keys.Add((S.GlobalCycleForwardHotkey, () => Cycle(null, true)));
        keys.Add((S.GlobalCycleBackwardHotkey, () => Cycle(null, false)));
        keys.Add((S.PreviousClientHotkey, () => { if (_previousActive != 0) ActivateClient(_previousActive); }));
        keys.Add((S.HideShowThumbnailsHotkey, () => _allHidden = !_allHidden));
        keys.Add((S.HideSecondaryHotkey, () =>
        {
            _pipsHidden = !_pipsHidden;
            Alerts.Say(_pipsHidden ? LocalizationService.Str("L.Msg.PipHidden", "PiP: Hidden") : LocalizationService.Str("L.Msg.PipVisible", "PiP: Visible"));
            TrayMenu?.Rebuild();
        }));
        keys.Add((S.FocusModeHotkey, ToggleFocusMode));
        keys.Add((S.CommandPaletteHotkey, ToggleCommandPalette));
        keys.Add((S.HideShowCropsHotkey, () =>
        {
            _cropsHidden = !_cropsHidden;
            Alerts.Say(_cropsHidden ? LocalizationService.Str("L.Msg.CropsHidden", "Crops: Hidden") : LocalizationService.Str("L.Msg.CropsVisible", "Crops: Visible"));
        }));
        keys.Add((S.LockPositionsHotkey, () => { S.LockPositions = !S.LockPositions; _settings.SaveDelayed(); }));
        keys.Add((S.GoToLastAlertHotkey, GoToLastAlert));
        keys.Add((S.DoNotDisturbHotkey, ToggleDoNotDisturb));
        // S3b: the app keys of Clients › Hotkeys that Linux has the feature for (HotkeyService.RegisterHotkeys).
        keys.Add((S.ClickThroughHotkey, () => ClickThrough = !ClickThrough));
        keys.Add((S.HidePrimaryHotkey, () =>
        {
            _primaryHidden = !_primaryHidden;
            Alerts.Say(_primaryHidden ? LocalizationService.Str("L.Msg.PrimaryHidden", "Primary: Hidden") : LocalizationService.Str("L.Msg.PrimaryVisible", "Primary: Visible"));
        }));
        keys.Add((S.ProfileCycleForwardHotkey, () => CycleProfile(true)));
        keys.Add((S.ProfileCycleBackwardHotkey, () => CycleProfile(false)));
        keys.Add((S.CloseAllClientsHotkey, CloseAllClients));
        keys.Add((S.OpenSettingsHotkey, OpenSettings));
        var wanted = keys.Where(k => !string.IsNullOrWhiteSpace(k.Item1)).ToList();
        var refused = _hotkeys.Bind(wanted);
        Console.WriteLine($"Hotkeys: {wanted.Count - refused.Count} bound");
        foreach (var key in refused) Console.WriteLine($"Hotkey not bound (no such key on Linux/X11): {key}");
    }

    private void SwitchTo(string character)
    {
        var thumb = _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, character, StringComparison.OrdinalIgnoreCase));
        if (thumb != null) ActivateClient(thumb.Client.Window);
    }

    /// <summary>Characters left out of cycling this session (ThumbnailManager._excludedFromCycle: Shift+click by default, or a
    /// character page's switch). Not saved, as on Windows.</summary>
    private readonly HashSet<string> _excludedFromCycle = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The cycle filters as the last press found them, and what the keys remember between presses (IDEAS #40).</summary>
    private CycleFilterOptions _cycleSwitches;
    private readonly CycleFilter.Walk<uint> _groupCycleWalk = new(), _allCycleWalk = new();

    /// <summary>The next or previous running client: of <paramref name="order"/> (a group's switching order) when
    /// given, else of every client in thumbnail order. Counted from the client in front (CycleStep, as on Windows).
    /// Characters left out of cycling are skipped, the cycle filters are applied (CycleFilter.Press, as ThumbnailManager
    /// does), and a wrap from the last client to the first plays the wrap-around sound.</summary>
    private void Cycle(IReadOnlyList<string>? order, bool forward)
    {
        var list = (order == null
            ? _thumbs.Values.ToList()
            : order.Select(n => _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, n, StringComparison.OrdinalIgnoreCase)))
                   .OfType<ThumbnailWindow>().ToList())
            .Where(t => !_excludedFromCycle.Contains(t.Client.Character)).ToList();
        if (list.Count == 0) return;
        var options = CycleFilterOptions.From(S);
        if (CycleFilter.Changed(ref _cycleSwitches, options)) { _groupCycleWalk.Forget(); _allCycleWalk.Forget(); }
        if (options.Any)
        {
            var windows = list.Select(t => t.Client.Window).ToList();
            string NameOf(uint w) => _thumbs.TryGetValue(w, out var t) ? t.Client.Character : "";
            if (CycleFilter.Press(windows, options, w => w == _lastActive, NameOf(_lastActive) is { Length: > 0 } a ? a : null, NameOf,
                    name => _alerts?.SystemOf(name), _attention.Has, name => _alerts?.Mutes.Until(name) != null,
                    order == null ? _allCycleWalk : _groupCycleWalk))
                Alerts.Say(LocalizationService.Str("L.Switch.CycleFilterEmpty", "No other client matches the cycle filter: switching through all of them"));
            list = windows.Select(w => _thumbs[w]).ToList();
        }
        int current = list.FindIndex(t => t.Client.Window == _lastActive);
        int next = CycleStep.Next(current, list.Count, forward);
        bool wrapped = forward
            ? current > 0 && next == 0 && list.Count > 1
            : current >= 0 && current < list.Count - 1 && next == list.Count - 1 && list.Count > 1;
        if (wrapped) PlayWrapSound();
        DiagnosticsService.LogCycling($"[Cycle] {(order == null ? "all" : "group")} {(forward ? "FWD" : "BWD")}: {current} -> {next} of {list.Count}");
        ActivateClient(list[next].Client.Window);
    }

    /// <summary>ThumbnailManager.ActivateEveWindow: brings a client to the front, and with "Minimize the other clients" on
    /// (MinimizeInactiveClients) minimizes the others MinimizeDelay ms later: not the one in front, not a character on the
    /// profile's never-minimize list (ThumbnailManager.MinimizableClient's rule, copied: keep in sync).</summary>
    private void ActivateClient(uint window)
    {
        _clients.Activate(window);
        if (!S.MinimizeInactiveClients) return;
        DispatcherTimer.RunOnce(() =>
        {
            var dontMinimize = _settings.CurrentProfile.DontMinimizeClients;
            uint front = _clients.Active();
            foreach (var thumb in _thumbs.Values)
            {
                var name = thumb.Client.Character;
                if (thumb.Client.Window == window || thumb.Client.Window == front) continue;
                if (string.IsNullOrEmpty(name) || name == "EVE" || dontMinimize.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                _clients.Minimize(thumb.Client.Window);
            }
        }, TimeSpan.FromMilliseconds(Math.Clamp(S.MinimizeDelay, 0, 5000)));
    }

    // ── Auto-solo audio (ThumbnailManager.ApplyAudioSolo / MuteAllClientAudio / UnmuteAllClientAudio) ──

    /// <summary>The client whose sound auto-solo left on (0: none, every client muted or solo off), and whether auto-solo was
    /// on at the last tick (switched off: every client is unmuted, as ReapplySettings did).</summary>
    private uint _soloWindow;
    private bool _soloOn;

    /// <summary>At every tick: with "Only the client in front plays sound" on, a client that comes to the front is the one
    /// unmuted and every other client is muted; with "Mute every client when none is in front" too, a program other than EVE
    /// in front mutes them all. Off again: every client is unmuted.</summary>
    private void FollowAudioSolo(uint active)
    {
        if (!S.AutoSoloClientAudio)
        {
            if (_soloOn) foreach (var thumb in _thumbs.Values) ClientVolume.SetMute(_clients.Pid(thumb.Client.Window), false);
            _soloOn = false;
            _soloWindow = 0;
            return;
        }
        bool wasOn = _soloOn;
        _soloOn = true;
        if (_thumbs.ContainsKey(active))
        {
            if (active == _soloWindow && wasOn) return;
            _soloWindow = active;
            ApplySolo();
        }
        else if (S.MuteAllClientAudioWhenNoneActive && !_clients.IsOurs(active) && (_soloWindow != 0 || !wasOn))
        {
            _soloWindow = 0;
            foreach (var thumb in _thumbs.Values) ClientVolume.SetMute(_clients.Pid(thumb.Client.Window), true);
        }
    }

    /// <summary>The solo client unmuted, every other one muted (asked again every 10 s: a client's sound stream starts late).</summary>
    private void ApplySolo()
    {
        foreach (var thumb in _thumbs.Values)
            ClientVolume.SetMute(_clients.Pid(thumb.Client.Window), thumb.Client.Window != _soloWindow);
    }

    /// <summary>App's CycleWrapped: the wrap-around sound, held by do-not-disturb (critical alerts or not), at the alert volume.</summary>
    private void PlayWrapSound()
    {
        if (!S.CycleWrapSoundEnabled || DoNotDisturb) return;
        var wrap = AlertSoundChoice.WrapSound(S);
        if (wrap.Length > 0) Alerts.PlayFile(wrap, S.AlertSoundVolume);
    }

    /// <summary>A left click on a thumbnail (ThumbnailWindow's click on Windows): Ctrl or Shift held runs the player's action
    /// for it (CtrlClickAction / ShiftClickAction: 0 switch, 1 minimize the client, 2 leave it out of cycling); else switch.</summary>
    private void ClickThumbnail(ThumbnailWindow thumb, EveClient client)
    {
        var held = thumb.ClickModifiers;
        int action = (held & Avalonia.Input.KeyModifiers.Control) != 0 ? S.CtrlClickAction : (held & Avalonia.Input.KeyModifiers.Shift) != 0 ? S.ShiftClickAction : 0;
        if (action == 1) _clients.Minimize(client.Window);
        else if (action == 2) ToggleCycleExclusion(client.Character);
        else ActivateClient(client.Window);
    }

    /// <summary>ThumbnailManager.ToggleCycleExclusion: the character is left out of cycling, or taken back, and it is said.</summary>
    private void ToggleCycleExclusion(string character)
    {
        if (string.IsNullOrEmpty(character)) return;
        bool now = !_excludedFromCycle.Remove(character);
        if (now) _excludedFromCycle.Add(character);
        var who = StreamerMask.Name(StreamerKind.Character, character);
        Alerts.Say(now ? string.Format(LocalizationService.Str("L.Msg.CycleExcluded", "'{0}' excluded from cycle"), who)
            : string.Format(LocalizationService.Str("L.Msg.CycleIncluded", "'{0}' re-included in cycle"), who));
    }

    bool ISettingsHost.IsExcludedFromCycle(string character) => _excludedFromCycle.Contains(character);

    void ISettingsHost.SetInCycle(string character, bool included)
    {
        if (_excludedFromCycle.Contains(character) == included) ToggleCycleExclusion(character);
    }

    /// <summary>The "Go to the last alert" hotkey (ThumbnailManager.GoToLastAlert): the client of the most severe alert the
    /// player has not looked at; pressed again, the next; with none left, back through this session's alerts (AlertWalk).
    /// With nothing to go to it says so and switches nothing; when the last alert is on the client in front, moments ago, it
    /// says that and stays.</summary>
    private void GoToLastAlert()
    {
        uint active = _clients.Active();
        string? inFront = _thumbs.TryGetValue(active, out var front) ? front.Client.Character : null;
        // An alert of the client in front is seen: the player is on it (it may have arrived while a switch was in flight).
        if (inFront != null) _attention.Seen(inFront);
        var step = _alertWalk.Next(_attention.Unseen(), () => _alerts?.Recent() ?? Array.Empty<AlertHistoryEntry>(),
            name => WindowOf(name) != 0, inFront, DateTime.UtcNow);
        if (!step.Goes)
        {
            Alerts.Say(step.Kind switch
            {
                AlertWalk.Kind.Here => LocalizationService.Str("L.Switch.LastAlertHere", "The last alert is on this client"),
                AlertWalk.Kind.NoEarlier => LocalizationService.Str("L.Switch.NoEarlierAlert", "No earlier alert"),
                _ => LocalizationService.Str("L.Switch.NoAlert", "No alert to go to"),
            });
            return;
        }
        uint window = WindowOf(step.Character);
        // The client in front already is looked at now; any other client's alert stays unseen until it really is in
        // front (Tick), so a switch the window manager refuses leaves it as it was.
        if (window == active) _attention.Seen(step.Character);
        else ActivateClient(window);
    }

    /// <summary>The do-not-disturb hotkey: flips it as the tray item does, says what it is now, and shows it in the tray menu.</summary>
    private void ToggleDoNotDisturb()
    {
        if (_alerts == null) return;
        var (key, english) = _alerts.PressDoNotDisturb();
        Alerts.Say(LocalizationService.Str(key, english));
        TrayMenu?.Rebuild();
    }

    // ── Alerts ──

    private uint WindowOf(string character) =>
        _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, character, StringComparison.OrdinalIgnoreCase))?.Client.Window ?? 0;

    private void Flash(string character, string severity, string alertType)
    {
        var def = SettingsUi.AlertEvents.Listed.FirstOrDefault(e => e.Key == alertType) ?? new SettingsUi.AlertEventDef(alertType, severity, "");
        int? lasts = SettingsUi.AlertFields.Lasts(S, alertType);
        int seconds = lasts ?? DefaultSeconds.GetValueOrDefault(severity, 6);
        _alertBorders[character] = new AlertBorder(Brush(SettingsUi.AlertFields.Colour(S, def)), Math.Max(100, SettingsUi.AlertFields.FlashRate(S, severity)),
            seconds == 0 ? null : DateTime.UtcNow.AddSeconds(seconds), SettingsUi.AlertFields.Steady(S, def), alertType);
        Console.WriteLine($"Alert: {character} {alertType} ({severity})");
    }

    private void Tick()
    {
        if (_ticks++ % 20 == 0) Sync();
        if (_ticks % 5 == 0)   // App's half-second timer
        {
            _alerts?.LookDerived(DateTime.UtcNow, RunningCharacters());
            FollowFocusMode();
            FollowNameBar();
            FollowAutoProfile();
            // The debug logs (App › Diagnostics) ask DiagnosticsService for the settings; an import replaces the object.
            DiagnosticsService.GlobalSettings = S;
            FollowKeepAwake();
            FollowDowntime();
        }
        if (_ticks % 50 == 0) LookAtMemory();
        var watch = Stopwatch.StartNew();
        uint active = _clients.Active();
        if (active != _lastActive && _thumbs.ContainsKey(active))
        {
            if (_thumbs.ContainsKey(_lastActive)) _previousActive = _lastActive;
            _lastActive = active;
            _alerts?.Looked(_thumbs[active].Client.Character);
            _alertBorders.Remove(_thumbs[active].Client.Character);   // switching to a client ends its alert
        }
        // The client in front is looked at: its unseen alerts go. Alerts nobody looked at for hours expire.
        _attention.DropExpired();
        if (_thumbs.TryGetValue(active, out var inFront)) _attention.Seen(inFront.Client.Character);
        bool inScope = S.GlobalHotkeys || _thumbs.ContainsKey(active) || _clients.IsOurs(active);
        // "Hide when another program is in front" (HideThumbnailsOnLostFocus): the thumbnails show only while an EVE client or
        // a MultiPreview window is in front, or the settings window is open (ThumbnailManager's focus sweep).
        bool lostFocus = S.HideThumbnailsOnLostFocus && !(_thumbs.ContainsKey(active) || _clients.IsOurs(active) || _settingsWindow != null);
        if (inScope != _keysInScope)
        {
            _keysInScope = inScope;
            if (!_capturing) BindHotkeys();
        }
        // An alert ends when its time is up, or (above) when the player switches to that client. One that arrives on the
        // client already in front shows its time out, as on Windows: being in front is not a switch.
        var now = DateTime.UtcNow;
        foreach (var (who, border) in _alertBorders.ToList())
            if (border.Until < now) _alertBorders.Remove(who);
        TellEnlarged(active);
        FollowAudioSolo(active);
        // The unseen-alert badge (ThumbnailManager.IncrementAlertBadge): per character, the unseen alerts of events whose badge is on.
        var badges = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (S.ShowAlertBadgeOnThumbnails)
            foreach (var unseen in _attention.Unseen())
                if (SettingsUi.AlertFields.Badge(S, unseen.EventKey)) badges[unseen.Character] = badges.GetValueOrDefault(unseen.Character) + 1;
        foreach (var thumb in _thumbs.Values)
        {
            bool isActive = thumb.Client.Window == active;
            bool shown = !_allHidden && !_primaryHidden && !Hidden(thumb.Client.Character) && !(isActive && S.HideActiveThumbnail) && !lostFocus
                && !FocusMode.Holds(_focusHeld, thumb.Client.Character) && !S.NameBarMode;   // the name bar holds the main thumbnails
            if (shown != thumb.IsVisible) { if (shown) thumb.Show(); else thumb.Hide(); }
            if (!shown) continue;
            FollowClickThrough(thumb);
            thumb.CanDrag = !S.LockPositions && !_clickThrough;
            thumb.ResizeFloor = ThumbnailWindow.FloorOf(S);
            thumb.LockResizeAspect = S.LockResizeAspect;
            thumb.HoverScale = S.ResizeThumbnailsOnHover ? S.HoverScale : 1.0;   // ThumbnailManager.HoverScaleFor
            thumb.Apply(Look(thumb.Client.Character, isActive, thumb.Hovered));
            thumb.MenuOpen = _menu != null;
            thumb.Follow();   // a drag that ended or a pointer that left lets a waiting enlargement happen
            thumb.SetMark(MarkOf(thumb.Client.Window));
            thumb.SetCycleBadge(_excludedFromCycle.Contains(thumb.Client.Character), ThumbnailFields.Anchor(S.CycleExclusionBadgePosition));
            Extras(thumb, badges.GetValueOrDefault(thumb.Client.Character));
            thumb.Update(_clients);
        }
        // Second thumbnails: shown while the client runs, also while it is in front, unless all thumbnails or the second ones are hidden.
        foreach (var (character, pip) in _pips)
        {
            bool shown = !_allHidden && !_pipsHidden && !FocusMode.Holds(_focusHeld, character) && !lostFocus;
            if (shown != pip.IsVisible) { if (shown) pip.Show(); else pip.Hide(); }
            if (!shown) continue;
            FollowClickThrough(pip);
            pip.CanDrag = !S.LockPositions && !_clickThrough;
            pip.ResizeFloor = ThumbnailWindow.FloorOf(S);
            pip.LockResizeAspect = S.LockResizeAspect;
            pip.Apply(SecondLook(character, pip.Hovered));
            pip.MenuOpen = _menu != null;
            pip.Follow();
            pip.SetMark(MarkOf(pip.Client.Window));
            pip.Update(_clients);
        }
        SyncCrops();
        foreach (var ((_, id), crop) in _crops)
        {
            var def = SettingsUi.CropEdits.Of(S, crop.Character).FirstOrDefault(d => d.Id == id);
            if (def == null) continue;   // deleted on the settings page: the next SyncCrops closes it
            bool shown = !_allHidden && !_cropsHidden && !FocusMode.Holds(_focusHeld, crop.Character);
            if (shown != crop.Window.IsVisible) { if (shown) crop.Window.Show(); else crop.Window.Hide(); }
            if (!shown) continue;
            var placed = PopupOf(def);
            if (placed != crop.Placed && !crop.Window.Busy)
            {
                crop.Window.PlaceAt(placed.X, placed.Y, placed.W, placed.H);
                crop.Placed = placed;
            }
            // Asked again every 2 s while it is on: a window manager that frames the window after the shape was set (a crop
            // made while MultiPreview runs, under openbox) keeps a frame that still catches the mouse until the shape changes.
            if ((crop.Through != def.ClickThrough || def.ClickThrough && _ticks % 20 == 0) && crop.Window.TryGetPlatformHandle() is { } handle
                && _clients.PassClicks((uint)(long)handle.Handle, def.ClickThrough))
                crop.Through = def.ClickThrough;
            crop.Window.CanDrag = !def.ClickThrough;
            crop.Window.Area = new EveClients.Area(def.SourceX, def.SourceY, Math.Max(1, def.SourceWidth), Math.Max(1, def.SourceHeight));
            crop.Window.Apply(CropLook(def));
            crop.Window.SetMark(MarkOf(crop.Window.Client.Window));   // IDEAS P114: the crop windows say it too
            crop.Window.Update(_clients);
        }
        if (_nameBar?.Preview is { } preview)   // the name bar's live picture of the client under the pointer
        {
            preview.Apply(new ThumbnailLook(Brushes.Transparent, 0, StreamerMask.Name(StreamerKind.Character, preview.Client.Character), true,
                Brush(S.ThumbnailTextColor), 12, S.ThumbnailTextFont, 1.0, true));
            preview.Update(_clients);
        }
        _drawMs += watch.Elapsed.TotalMilliseconds;
        if (_ticks % 100 == 0)
        {
            Console.WriteLine($"{_thumbs.Count} thumbnail(s), {_drawMs / 100:F1} ms per redraw");
            _drawMs = 0;
        }
    }

    /// <summary>ThumbnailManager.TellEnlarged: asks AlertEnlarge what is due and tells each thumbnail its scale (1.0: back to
    /// its own size). A thumbnail that is enlarged, or was asked to be, while AlertEnlarge holds nothing for its character is
    /// put back (the watchdog: its character left the window, or a "go back" failed). Only the thumbnails are told: the
    /// client in front is the real thing and never enlarged.</summary>
    private void TellEnlarged(uint active)
    {
        if (_alerts?.Enlarge is not { } enlarge) return;
        foreach (var thumb in _thumbs.Values)
            if ((thumb.IsAlertEnlarged || thumb.IsAlertEnlargementAsked) && !enlarge.Holds(thumb.Client.Character))
                thumb.SetAlertScale(1.0);
        if (!enlarge.Any) return;
        foreach (var change in enlarge.Tick(DateTime.UtcNow, S,
                     name => WindowOf(name) is var w && w != 0 && w == active, name => WindowOf(name) != 0))
        {
            var thumb = _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, change.Character, StringComparison.OrdinalIgnoreCase));
            thumb?.SetAlertScale(change.Scale);
        }
    }

    /// <summary>The Windows text overlay's other parts on a thumbnail: the text margins, the solar system (ShowSystemName, from
    /// the logs) and the session timer (ShowSessionTimer) under the name, the rounded corners, the alert's name while its border
    /// shows (ShowAlertTextOnThumbnails, at AlertTextPosition) and the unseen-alert count.</summary>
    private void Extras(ThumbnailWindow thumb, int unseen)
    {
        var name = thumb.Client.Character;
        var margins = S.ThumbnailTextMargins;
        string? system = S.ShowSystemName && _alerts?.SystemOf(name) is { Length: > 0 } sys ? StreamerMask.Name(StreamerKind.System, sys) : null;
        string? timer = S.ShowSessionTimer ? (DateTime.UtcNow - thumb.SessionStartUtc).ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : null;
        thumb.SetExtras((int)Math.Round(margins?.X ?? 5), (int)Math.Round(margins?.Y ?? 5), system, timer, Math.Clamp(S.ThumbnailCornerRadius, 0, Models.Profile.MaxCornerRadius));
        bool alerting = _alertBorders.TryGetValue(name, out var alert) && S.ShowAlertTextOnThumbnails;
        thumb.SetAlertText(alerting ? EveMultiPreview.Views.AlertHub.EventDisplayName(alert!.AlertType) : null, alert?.Brush ?? Brushes.White,
            SettingsUi.AlertFields.TextPosition(S.AlertTextPosition));
        thumb.SetAlertCount(unseen);
    }

    private bool Hidden(string character) => S.ThumbnailVisibility.TryGetValue(character, out var v) && v != 0;

    private ThumbnailLook Look(string character, bool isActive, bool hovered = false)
    {
        IBrush border = Brush(BorderColorRule.Resolve(S, character, isActive, RunningCharacters));
        // ThumbnailManager.ApplyActiveBorders: with "Show a border on the active client" off, the active one has no border at all.
        double thickness = isActive ? (S.ShowClientHighlightBorder ? S.ClientHighlightBorderThickness : 0)
            : BorderColorRule.ShowsInactiveBorder(S, character) ? S.InactiveClientBorderThickness : 0;
        if (_alertBorders.TryGetValue(character, out var alert)
            && (alert.Steady || Environment.TickCount64 / alert.RateMs % 2 == 0))
            (border, thickness) = (alert.Brush, Math.Max(1, S.AlertBorderThickness));
        // Streamer mode: the name becomes "Client N", a label the player typed is left out (StreamerKind.Label).
        var label = S.ThumbnailAnnotations.TryGetValue(character, out var note) && !string.IsNullOrWhiteSpace(note)
            ? StreamerMask.Name(StreamerKind.Label, note)
            : StreamerMask.Name(StreamerKind.Character, character);
        var style = S.ThumbnailLabelStyles.GetValueOrDefault(character);
        var textColour = style is { Color.Length: > 0 } ? style.Color : S.ThumbnailTextColor;
        double size = style is { Size: > 0 } ? style.Size : double.TryParse(S.ThumbnailTextSize, out var fs) ? fs : 12;
        // The window's opacity fades its border too, so while an alert shows the thumbnail is at least as strong as the
        // alert's own strength (AlertOpacityPercent, 100 % by default): a faint thumbnail must not hide its alert.
        double opacity = Math.Clamp(S.ThumbnailOpacity, 0, 255) / 255.0;
        if (hovered && S.OpacityOnHover) opacity = 1.0;   // "Solid under the mouse" (issue #37)
        if (_alertBorders.ContainsKey(character))
            opacity = Math.Max(opacity, SettingsUi.AlertFields.PulsePercent(S.AlertOpacityPercent) / 100.0);
        return new ThumbnailLook(border, thickness, label, S.ShowThumbnailTextOverlay, Brush(textColour), size,
            S.ThumbnailTextFont, opacity, S.ShowThumbnailsAlwaysOnTop);
    }

    /// <summary>A second thumbnail's look (ThumbnailManager.CreateSecondaryThumbnail): no border, the character's name, its own
    /// opacity (PipOpacityFor: its entry's, else the thumbnails'), solid under the mouse when that is on.</summary>
    private ThumbnailLook SecondLook(string character, bool hovered)
    {
        int alpha = Math.Clamp(S.SecondaryThumbnails.TryGetValue(character, out var ss) ? ss.Opacity : S.ThumbnailOpacity, 0, 255);
        double opacity = hovered && S.OpacityOnHover ? 1.0 : alpha / 255.0;
        var style = S.ThumbnailLabelStyles.GetValueOrDefault(character);
        var textColour = style is { Color.Length: > 0 } ? style.Color : S.ThumbnailTextColor;
        double size = style is { Size: > 0 } ? style.Size : double.TryParse(S.ThumbnailTextSize, out var fs) ? fs : 12;
        return new ThumbnailLook(Brushes.Transparent, 0, StreamerMask.Name(StreamerKind.Character, character), true, Brush(textColour), size,
            S.ThumbnailTextFont, opacity, S.ShowThumbnailsAlwaysOnTop);
    }

    /// <summary>ThumbnailManager's PiP sync: a second thumbnail for each running client whose character has one switched on
    /// (CharacterFields.PipOn); one whose client is gone or that was switched off is closed.</summary>
    private void SyncPips()
    {
        // Gone: no client, switched off, or the client restarted (a new window; the next lines make it again for that one).
        foreach (var gone in _pips.Where(kv => WindowOf(kv.Key) != kv.Value.Client.Window || !SettingsUi.CharacterFields.PipOn(S, kv.Key))
                     .Select(kv => kv.Key).ToList())
        {
            _pips[gone].Close();
            _pips.Remove(gone);
        }
        foreach (var thumb in _thumbs.Values)
        {
            var character = thumb.Client.Character;
            if (_pips.ContainsKey(character) || !SettingsUi.CharacterFields.PipOn(S, character)) continue;
            var ss = S.SecondaryThumbnails[character];
            var pip = new ThumbnailWindow(thumb.Client, new PixelRect(ss.X, ss.Y, ss.Width > 0 ? ss.Width : 160, ss.Height > 0 ? ss.Height : 120), second: true);
            pip.Clicked += c => ActivateClient(c.Window);
            void Save(ThumbnailWindow t)
            {
                if (!S.SecondaryThumbnails.TryGetValue(character, out var entry)) return;
                (entry.X, entry.Y, entry.Width, entry.Height) = (t.OwnPosition.X, t.OwnPosition.Y, t.OwnWidth, t.OwnHeight);
                _settings.SaveDelayed();
            }
            pip.Moved += Save;
            pip.ResizeEnded += Save;
            pip.MenuRequested += ShowMenu;
            pip.PointerSource = _clients.Pointer;
            pip.RaiseWhileEnlarged = t =>
            {
                if (S.ShowThumbnailsAlwaysOnTop && t.TryGetPlatformHandle() is { } handle) _clients.Raise((uint)(long)handle.Handle);
            };
            _pips[character] = pip;
        }
    }

    string? ISettingsHost.PickCropArea(string character, CropDefinition def, Action picked)
    {
        uint window = WindowOf(character);
        if (window == 0) return "L.S.Crops.NeedsClient";
        if (_clients.Rectangle(window) is not { } r) return "L.S.Crops.NoClientArea";
        var pixels = _clients.Snapshot(window, out int width, out int height);
        if (pixels == null || width != r.Width || height != r.Height) return "L.S.Crops.NoClientArea";
        var settings = _settingsWindow;
        CropPicker? picker = null;
        try
        {
            picker = new CropPicker(new PixelPoint(r.X, r.Y), width, height, pixels);
            picker.Done += got =>
            {
                try
                {
                    if (got is { } p)
                    {
                        SettingsUi.CropEdits.ApplyPick(def, p, 1.0);   // X11 pixels are the window's pixels: no scale to undo
                        _settings.SaveDelayed();
                        picked();
                    }
                }
                finally
                {
                    if (settings != null && settings == _settingsWindow) { settings.Show(); settings.Activate(); }
                }
            };
            settings?.Hide();   // so it can't come back over the client while the player picks
            _clients.Activate(window);
            picker.Show();
            return null;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Crop area picker failed: {e.Message}");
            try { picker?.Close(); } catch { }
            settings?.Show();
            return "L.S.Crops.PickFailed";
        }
    }

    /// <summary>ThumbnailManager.ReapplySettings' place and size part: each running thumbnail with a saved place goes there, at
    /// its saved size. One the player is dragging, and one already there, are left alone (asked after every applied change).</summary>
    void ISettingsHost.ReapplyThumbnails()
    {
        foreach (var thumb in _thumbs.Values)
        {
            if (thumb.Busy || _settings.GetThumbnailPosition(thumb.Client.Character) is not { Width: > 0, Height: > 0 } saved) continue;
            var (x, y, w, h) = ((int)saved.X, (int)saved.Y, (int)saved.Width, (int)saved.Height);
            if (thumb.OwnPosition == new PixelPoint(x, y) && thumb.OwnWidth == w && thumb.OwnHeight == h) continue;
            thumb.PlaceAt(x, y, w, h);
        }
    }

    void ISettingsHost.ResizeThumbnails(int width, int height)
    {
        S.ThumbnailStartLocation.Width = width;
        S.ThumbnailStartLocation.Height = height;
        foreach (var thumb in _thumbs.Values)
        {
            thumb.SetSize(width, height);
            _settings.SaveThumbnailPosition(thumb.Client.Character, thumb.OwnPosition.X, thumb.OwnPosition.Y, width, height);
        }
        _settings.SaveDelayed();
    }

    bool ISettingsHost.ResizeThumbnail(string character, int width, int height)
    {
        var thumb = _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, character, StringComparison.OrdinalIgnoreCase));
        if (thumb == null) return false;
        thumb.SetSize(width, height);
        _settings.SaveThumbnailPosition(thumb.Client.Character, thumb.OwnPosition.X, thumb.OwnPosition.Y, width, height);
        return true;
    }

    void ISettingsHost.SetClientVolume(string character, int percent)
    {
        var thumb = _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, character, StringComparison.OrdinalIgnoreCase));
        if (thumb != null) { SetVolume(thumb.Client, Math.Clamp(percent, 0, 100)); return; }   // stores, saves, sets the client
        if (percent >= 100) S.PerClientAudioVolume.Remove(character);
        else S.PerClientAudioVolume[character] = Math.Clamp(percent, 0, 100);
        _settings.SaveDelayed();
    }

    /// <summary>A crop window's place and size from its definition, at least CropManager's 40 × 30.</summary>
    private static (int X, int Y, int W, int H) PopupOf(CropDefinition def) =>
        (def.PopupX, def.PopupY, Math.Max(SettingsUi.CropEdits.PopupMin.W, def.PopupWidth), Math.Max(SettingsUi.CropEdits.PopupMin.H, def.PopupHeight));

    /// <summary>A crop window's look (CropWindow + CropManager.ApplyVisualSettings): no border, its name when "Show the
    /// name" is on, in the thumbnails' text style, at its own opacity (10-100 %).</summary>
    private ThumbnailLook CropLook(CropDefinition def)
    {
        double size = double.TryParse(S.ThumbnailTextSize, out var fs) && fs > 0 ? fs : 11;
        bool label = def.ShowLabel && !string.IsNullOrWhiteSpace(def.Name);
        return new ThumbnailLook(Brushes.Transparent, 0, def.Name, label, Brush(S.ThumbnailTextColor), size,
            S.ThumbnailTextFont, Math.Clamp(def.Opacity, SettingsUi.CropEdits.MinOpacity, SettingsUi.CropEdits.MaxOpacity) / 100.0,
            S.ShowThumbnailsAlwaysOnTop);
    }

    /// <summary>CropManager.ReconcileCharacter: a crop window for each crop of each running client's character while crop
    /// windows are on; one whose crop was deleted, whose client is gone or that is switched off is closed. The thumbnails'
    /// clients are the running ones (a client that restarted is a new window, so its crops are made again).</summary>
    private void SyncCrops()
    {
        var wanted = new Dictionary<(uint, string), (EveClient Client, CropDefinition Def)>();
        if (S.CropEnabled)
            foreach (var thumb in _thumbs.Values)
                foreach (var def in SettingsUi.CropEdits.Of(S, thumb.Client.Character))
                    wanted.TryAdd((thumb.Client.Window, def.Id), (thumb.Client, def));
        foreach (var gone in _crops.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _crops[gone].Window.Close();
            _crops.Remove(gone);
        }
        foreach (var (key, (client, def)) in wanted)
        {
            if (_crops.ContainsKey(key)) continue;
            var placed = PopupOf(def);
            var win = new ThumbnailWindow(client, new PixelRect(placed.X, placed.Y, placed.W, placed.H), second: true, cropId: def.Id)
            {
                PointerSource = _clients.Pointer,
                ResizeFloor = (SettingsUi.CropEdits.PopupMin.W, SettingsUi.CropEdits.PopupMin.H),
            };
            var crop = new CropShown(win, client.Character) { Placed = placed };
            // Left click does nothing and there is no menu: a right drag moves it, the left button too resizes it (CropWindow).
            void Save(ThumbnailWindow t)
            {
                var d = SettingsUi.CropEdits.Of(S, crop.Character).FirstOrDefault(x => x.Id == key.Item2);
                if (d == null) return;
                SettingsUi.CropEdits.SetPopup(d, (t.OwnPosition.X, t.OwnPosition.Y, t.OwnWidth, t.OwnHeight));
                crop.Placed = PopupOf(d);
                _settings.SaveDelayed();
            }
            win.Moved += Save;
            win.ResizeEnded += Save;
            win.Opened += (_, _) => { if (win.TryGetPlatformHandle() is { } handle) _clients.NoFocus((uint)(long)handle.Handle); };
            _crops[key] = crop;
        }
    }

    private IReadOnlyCollection<string> RunningCharacters() => _thumbs.Values.Select(t => t.Client.Character).ToList();

    /// <summary>A brush for a settings colour ("#RRGGBB", "0xRRGGBB", "RRGGBB", "rgb(r,g,b)"), parsed as the Windows
    /// app parses it; white when it can't be read.</summary>
    private IBrush Brush(string colour)
    {
        if (_brushes.TryGetValue(colour, out var brush)) return brush;
        var c = AppSettings.ParseColor(colour, System.Windows.Media.Color.FromRgb(255, 255, 255));
        return _brushes[colour] = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    private int _syncs;

    // ── What the clients are like (IDEAS #22, #47: App's ProbeClients / LookAtClients) ──

    /// <summary>The clients at the login screen at the last look ("Client ready").</summary>
    private HashSet<uint> _atLogin = new();
    /// <summary>The client windows the probe looks at: written by Sync, read by the probe's thread.</summary>
    private volatile (uint Window, string Character)[] _probeWindows = Array.Empty<(uint, string)>();
    private volatile int _probeWants;   // ClientProbeWants, as LookAtClients last worked it out
    private int _probing;
    private X11ClientProbe? _probe;
    private System.Threading.Timer? _probeTimer;
    private readonly ClientWatch _clientWatch = new();
    /// <summary>What ClientWatch says of each client window now (only the windows with a mark).</summary>
    private readonly Dictionary<uint, ClientMark> _marks = new();

    /// <summary>Every 3 s on a pool thread: asks the X server and /proc what LookAtClients last said is wanted and hands it
    /// to the UI thread. It reads no setting.</summary>
    private void ProbeClients()
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;
        try
        {
            var wants = (ClientProbeWants)_probeWants;
            IReadOnlyList<ClientSample> samples = Array.Empty<ClientSample>();
            if (wants != ClientProbeWants.None || _probe != null)
            {
                _probe ??= new X11ClientProbe();
                samples = _probe.Take(_probeWindows, wants, DateTime.UtcNow);
            }
            Dispatcher.UIThread.Post(() => LookAtClients(samples), DispatcherPriority.Background);
        }
        catch (Exception e) { ErrorLog.Shared.Record(e, "App", DateTime.Now); }
        finally { Volatile.Write(ref _probing, 0); }
    }

    /// <summary>On the UI thread after each probe: what is wanted next time, the marks, and "Client not responding". Linux
    /// can't read the exit code of a client it did not start, so "Client closed unexpectedly" is not looked for.</summary>
    private void LookAtClients(IReadOnlyList<ClientSample> samples)
    {
        if (_alerts == null) return;
        var options = ClientWatchOptions.Of(S, _alerts.EventOn);
        _probeWants = (int)options.Wants(closedOn: false);
        var now = DateTime.UtcNow;
        var look = _clientWatch.Look(now, samples, options);
        foreach (var change in look.Changes)
        {
            uint window = (uint)(long)change.Window;
            if (change.Mark == ClientMark.None) _marks.Remove(window);
            else _marks[window] = change.Mark;
        }
        foreach (var alert in look.Alerts) _alerts.Raise(now, alert.Character, alert.EventKey, alert.Severity, alert.Text);
    }

    /// <summary>The words a client's thumbnails, second thumbnail and crop windows show over it, or null.</summary>
    private string? MarkOf(uint window) => _marks.TryGetValue(window, out var mark) ? ClientMarks.Text(mark) : null;

    /// <summary>"Client memory high" (IDEAS #45), every 5 s: the resident memory of each client's process, from /proc
    /// (VmRSS: the Windows working set's counterpart).</summary>
    private void LookAtMemory()
    {
        if (_alerts == null || !_alerts.EventOn(MemoryWatch.EventKey)) { _alerts?.LookAtMemory(DateTime.UtcNow, Array.Empty<(string, int, long)>()); return; }
        var clients = new List<(string, int, long)>();
        foreach (var thumb in _thumbs.Values)
        {
            int pid = (int)_clients.Pid(thumb.Client.Window);
            if (pid > 0 && ResidentMb(pid) is long mb) clients.Add((thumb.Client.Character, pid, mb));
        }
        _alerts.LookAtMemory(DateTime.UtcNow, clients);
    }

    private static long? ResidentMb(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal)
                    && long.TryParse(line[6..].Trim().Split(' ')[0], out long kb)) return kb / 1024;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private void Sync()
    {
        var atLogin = new List<uint>();
        var found = _clients.Find(atLogin);
        // "Client ready" (ClientReady.ShouldRaise): a client seen at the login screen at the last look has a character now.
        foreach (var client in found.Where(f => _atLogin.Contains(f.Window)))
            if (ClientReady.ShouldRaise("", client.Character, _syncs > 0)) _alerts?.Raise(DateTime.UtcNow, client.Character, ClientReady.EventKey, ClientReady.Severity);
        _atLogin = atLogin.ToHashSet();
        _probeWindows = found.Select(f => (f.Window, f.Character)).ToArray();
        // A client's sound stream starts after its window (at login, after a loading screen): its saved volume is given again every 10 s.
        if (++_syncs % 5 == 0)
        {
            foreach (var thumb in _thumbs.Values)
                if (!_soloOn || thumb.Client.Window == _soloWindow) ApplyVolume(thumb.Client);
            if (_soloOn) foreach (var thumb in _thumbs.Values) ClientVolume.SetMute(_clients.Pid(thumb.Client.Window), thumb.Client.Window != _soloWindow);
        }
        foreach (var gone in _thumbs.Keys.Except(found.Select(f => f.Window)).ToList())
        {
            _attention.Gone(_thumbs[gone].Client.Character);   // nothing there needs the player any more
            _thumbs[gone].Close();
            _thumbs.Remove(gone);
            _clients.Forget(gone);
            Console.WriteLine($"Client closed: window {gone}");
        }
        foreach (var client in found.Where(f => !_thumbs.ContainsKey(f.Window)))
        {
            var thumb = new ThumbnailWindow(client, Place(client.Character));
            thumb.Clicked += c => ClickThumbnail(thumb, c);
            thumb.Moved += t =>
            {
                _settings.SaveThumbnailPosition(t.Client.Character, t.OwnPosition.X, t.OwnPosition.Y, t.OwnWidth, t.OwnHeight);
                if (Environment.GetEnvironmentVariable("EVEMP_DEBUG_KEYS") == "1") Console.WriteLine($"[move] {t.Client.Character} -> {t.OwnPosition}");
            };
            thumb.ResizeEnded += t =>
            {
                _settings.SaveThumbnailPosition(t.Client.Character, t.OwnPosition.X, t.OwnPosition.Y, t.OwnWidth, t.OwnHeight);
                if (Environment.GetEnvironmentVariable("EVEMP_DEBUG_KEYS") == "1") Console.WriteLine($"[resize] {t.Client.Character} -> {t.OwnWidth}x{t.OwnHeight}");
            };
            thumb.ResizeAll += ResizeOthers;
            // An enlarged thumbnail is stacked above its neighbours, as Windows raises it (only while the thumbnails are "always on top").
            thumb.RaiseWhileEnlarged = t =>
            {
                if (S.ShowThumbnailsAlwaysOnTop && t.TryGetPlatformHandle() is { } handle) _clients.Raise((uint)(long)handle.Handle);
            };
            thumb.PointerSource = _clients.Pointer;
            thumb.MenuRequested += ShowMenu;
            _thumbs[client.Window] = thumb;
            ApplyVolume(client);
            Console.WriteLine($"Client found: {client.Character} (window {client.Window})");
        }
        SyncPips();
    }

    // ── The thumbnail's menu ──

    private ContextMenu? _menu;

    /// <summary>Opens a thumbnail's right-click menu at the pointer, with the state of this moment. Only one is open.</summary>
    private void ShowMenu(ThumbnailWindow thumb)
    {
        if (_alerts == null) return;
        _menu?.Close();
        var character = thumb.Client.Character;
        var state = new ThumbnailMenuState(character, _alerts.SystemOf(character), _alerts.Mutes.Until(character),
            _alerts.Snoozes.Of(character), Volume(character), LookupLinks.CharacterId(S.EveManager?.CharNameCache, character),
            Frozen: _clientWatch.IsHung((IntPtr)thumb.Client.Window));
        var menu = ThumbnailMenu.Build(state, new ThumbnailMenuActions(
            EditLabel: () => EditLabel(character),
            Mute: minutes => Mute(character, minutes),
            EndSnooze: key => { if (key == null) _alerts.Snoozes.EndAll(character); else _alerts.Snoozes.End(character, key); },
            Audio: code => SetVolume(thumb.Client, code),
            OpenSettings: () => OpenSettings(CharacterPage.IdFor(character)),
            CopyText: text => _ = thumb.Clipboard?.SetTextAsync(text),
            OpenLink: OpenLink,
            CloseClient: () => CloseClient(thumb.Client),
            EndFrozen: () => EndFrozenClient(thumb.Client)), DateTime.UtcNow);
        menu.Closed += (_, _) => { if (_menu == menu) _menu = null; };
        _menu = menu;   // while it is open no thumbnail is enlarged for an alert, this one included (MenuOpen, at the next tick)
        menu.Open(thumb);
    }

    private void EditLabel(string character)
    {
        var dialog = new LabelDialog(character, S, () => _settings.SaveDelayed());
        dialog.Show();
    }

    /// <summary>ThumbnailManager.SetCharacterAlertMute: mutes or unmutes one character's alerts and says so. A mute ends
    /// that character's alert border and puts back its thumbnail, enlarged for an alert, now.</summary>
    private void Mute(string character, int minutes)
    {
        if (_alerts == null) return;
        var until = _alerts.Mutes.Set(character, minutes);
        string who = StreamerMask.Name(StreamerKind.Character, character);
        if (until != null)
        {
            _alertBorders.Remove(character);
            PutBack(character);
        }
        Alerts.Say(until == null ? string.Format(LocalizationService.Str("L.Msg.AlertsUnmuted", "{0}: 🔔 Alerts unmuted"), who)
            : until == DateTime.MaxValue ? string.Format(LocalizationService.Str("L.Msg.AlertsMuted", "{0}: 🔇 Alerts muted"), who)
            : string.Format(LocalizationService.Str("L.Msg.AlertsMutedMins", "{0}: 🔇 Alerts muted {1}m"), who, minutes));
    }

    /// <summary>App.SnoozeEvent: snoozes one event for one character (a notification's button, the settings' list), and its
    /// thumbnail, enlarged for an alert, goes back now (ThumbnailManager.PutBackForASnooze).</summary>
    private void SnoozeEvent(string character, string alertType, TimeSpan? length)
    {
        if (_alerts == null) return;
        _alerts.Snoozes.Snooze(character, alertType, length);
        PutBack(character);
    }

    void ISettingsHost.Snooze(string character, string alertType, TimeSpan? length) => SnoozeEvent(character, alertType, length);

    EventSnoozes? ISettingsHost.Snoozes => _alerts?.Snoozes;

    CustomAlertStore? ISettingsHost.CustomAlerts => _alerts?.CustomAlerts;

    IReadOnlyDictionary<string, string> ISettingsHost.CustomRulesSwitchedOff => _alerts?.CustomRulesSwitchedOff ?? CustomAlertEngine.NoneSwitchedOff;

    private void PutBack(string character)
    {
        _alerts?.Enlarge.Forget(character);
        _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, character, StringComparison.OrdinalIgnoreCase))?.SetAlertScale(1.0);
    }

    /// <summary>The client's saved volume (0–100; 100 when none is saved).</summary>
    private int Volume(string character) => S.PerClientAudioVolume.TryGetValue(character, out var v) ? Math.Clamp(v, 0, 100) : 100;

    /// <summary>ThumbnailManager.OnAudioRequested / SetClientVolume: -1 mutes the client's sound (not saved); 0–100 sets and
    /// saves its volume (100 is saved as none).</summary>
    private void SetVolume(EveClient client, int code)
    {
        uint pid = _clients.Pid(client.Window);
        if (code < 0)
        {
            ClientVolume.Apply(pid, -1);
            Alerts.Say(string.Format(LocalizationService.Str("L.Msg.AudioMuted", "{0}: audio muted"), StreamerMask.Name(StreamerKind.Character, client.Character)));
            return;
        }
        int percent = Math.Clamp(code, 0, 100);
        if (percent >= 100) S.PerClientAudioVolume.Remove(client.Character);
        else S.PerClientAudioVolume[client.Character] = percent;
        _settings.SaveDelayed();
        ClientVolume.Apply(pid, percent);
    }

    /// <summary>Gives a client its saved volume (when it starts, and again now and then: its sound starts after its window).</summary>
    private void ApplyVolume(EveClient client)
    {
        if (S.PerClientAudioVolume.TryGetValue(client.Character, out var v) && v < 100) ClientVolume.Apply(_clients.Pid(client.Window), v);
    }

    /// <summary>A lookup link, in the browser (xdg-open), only for the three hosts LookupLinks allows, in its canonical form.</summary>
    private static void OpenLink(string url)
    {
        if (LookupLinks.BrowserTarget(url) is not { } target) return;
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(target);
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }   // no xdg-open
    }

    /// <summary>Close client: the window manager is asked to close it, as its own close button would. If its window is still
    /// there 5 s later, its process is ended, as the Windows app ends it, but only an EVE process (exefile).</summary>
    private void CloseClient(EveClient client)
    {
        uint window = client.Window, pid = _clients.Pid(window);
        _clients.Close(window);
        DispatcherTimer.RunOnce(() =>
        {
            if (pid == 0 || _clients.Pid(window) != pid || !IsEveProcess(pid)) return;
            try { using var p = Process.GetProcessById((int)pid); p.Kill(); }
            catch (Exception) { }   // already gone, or not ours to end
        }, TimeSpan.FromSeconds(5));
    }

    /// <summary>App.EndFrozenClient (IDEAS #22): asks first (Keep by default), then ends only the process the freeze was seen
    /// on, only while its window is still that process's, still frozen, and an EVE process. Says what came of it.</summary>
    private void EndFrozenClient(EveClient client)
    {
        uint window = client.Window;
        if (_clientWatch.PidOf((IntPtr)window) is not int pid) return;
        var who = ThumbnailMenu.HasCharacter(client.Character) ? StreamerMask.Name(StreamerKind.Character, client.Character)
            : LocalizationService.Str("L.Ctx.LoginScreen", "Character select");
        var dialog = new ChoiceDialog(
            string.Format(LocalizationService.Str("L.Msg.EndFrozenConfirm", "{0}'s client is not responding. End it now? Anything it has not saved is lost."), who),
            LocalizationService.Str("L.Msg.EndFrozenYes", "End the client"), LocalizationService.Str("L.Msg.EndFrozenNo", "Keep it running"));
        dialog.Answered += end =>
        {
            if (!end) return;
            EndResult result;
            if (_clients.Pid(window) != pid || !Directory.Exists($"/proc/{pid}")) result = EndResult.Gone;
            else if (!_clientWatch.IsHung((IntPtr)window)) result = EndResult.NoLongerFrozen;
            else if (!IsEveProcess((uint)pid)) result = EndResult.Refused;
            else
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(); result = EndResult.Ended; }
                catch (ArgumentException) { result = EndResult.Gone; }
                catch (Exception) { result = EndResult.Refused; }
            }
            // The Windows "refused" sentence names Windows: Linux has its own.
            var (key, english) = result == EndResult.Refused ? ("L.Linux.EndFrozenRefused", "MultiPreview could not end {0}'s client.") : ClientExits.Feedback(result);
            Alerts.Say(string.Format(LocalizationService.Str(key, english), who));
        };
        dialog.Show();
    }

    /// <summary>The process is an EVE client: its name (/proc/‹pid›/comm, Wine's "exefile.exe") is exefile.</summary>
    private static bool IsEveProcess(uint pid)
    {
        try
        {
            var name = File.ReadAllText($"/proc/{pid}/comm").Trim();
            return name.Equals("exefile", StringComparison.OrdinalIgnoreCase) || name.Equals("exefile.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>ThumbnailManager.OnResizeAll: while one thumbnail is resized (without Ctrl) every other one gets the same
    /// size, and so does the size new clients start at. Not with "Resize thumbnails individually" (IndividualThumbnailResize).
    /// Each size is saved at once, as on Windows.</summary>
    private void ResizeOthers(ThumbnailWindow source, int width, int height)
    {
        if (S.IndividualThumbnailResize) return;
        S.ThumbnailStartLocation.Width = width;
        S.ThumbnailStartLocation.Height = height;
        foreach (var thumb in _thumbs.Values)
        {
            if (thumb == source) continue;
            thumb.SetSize(width, height);
            _settings.SaveThumbnailPosition(thumb.Client.Character, thumb.OwnPosition.X, thumb.OwnPosition.Y, width, height);
        }
    }

    /// <summary>The character's saved place, else the next free spot of the column MultiPreview lays out for new clients
    /// (ThumbnailManager.FlowSlot): down from the start location, a new column at the bottom of the screen, ThumbnailGutter
    /// apart (0-200, as the card allows).</summary>
    private PixelRect Place(string character)
    {
        if (_settings.GetThumbnailPosition(character) is { Width: > 0, Height: > 0 } saved)
            return new PixelRect((int)saved.X, (int)saved.Y, (int)saved.Width, (int)saved.Height);
        var start = S.ThumbnailStartLocation;
        int w = Math.Max(1, (int)start.Width), h = Math.Max(1, (int)start.Height);
        int x = (int)start.X, y = (int)start.Y;
        // Copy of ThumbnailManager.FlowSlot (a WPF file) — keep in sync.
        int gap = Math.Clamp(S.ThumbnailGutter, 0, 200);
        int bottom = _thumbs.Values.Select(t => t.Screens.Primary?.WorkingArea.Bottom).FirstOrDefault(b => b != null) ?? int.MaxValue / 4;
        int perColumn = Math.Max(1, (bottom - y) / Math.Max(1, h + gap));
        int index = _thumbs.Count;
        return new PixelRect(x + index / perColumn * (w + gap), y + index % perColumn * (h + gap), w, h);
    }
}
