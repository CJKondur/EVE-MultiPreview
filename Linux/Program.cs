using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Themes.Simple;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
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
    private const int Gap = 16;
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
    private SettingsWindow? _settingsWindow;

    /// <summary>The tray icon's menu, rebuilt when a setting it shows changes.</summary>
    public Tray? TrayMenu { get; set; }

    /// <summary>A thumbnail's alert border: its colour, flash rate, when it ends (null: when the player switches to
    /// that client) and whether it is steady.</summary>
    private sealed record AlertBorder(IBrush Brush, int RateMs, DateTime? Until, bool Steady);
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

    public void Start()
    {
        Console.WriteLine($"EVE MultiPreview for Linux: settings {AppPaths.SettingsPath}; looking for windows titled \"{_clients.TitlePrefix}…\" "
            + $"(Composite: {(_clients.HasComposite ? "yes" : "no, covered clients will not show")}; scaling on the X server: {(_clients.ScalesOnServer ? "yes" : "no")})");
        BindHotkeys();
        _alerts = new Alerts(_settings, RunningCharacters, WindowOf, _clients.Active, Flash);
        var (chat, game) = _alerts.Start();
        Console.WriteLine($"Logs: chat {(chat.Length > 0 ? chat : "not found")}; game {(game.Length > 0 ? game : "not found")}");
        new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick()).Start();
        Tick();
        // EVEMP_OPEN_SETTINGS=<page number from 0>: opens the settings window at startup (the tests use it).
        if (int.TryParse(Environment.GetEnvironmentVariable("EVEMP_OPEN_SETTINGS"), out int page)) OpenSettings(page);
    }

    // ── Tray ──

    private bool _hotkeysPaused;

    public bool HotkeysPaused
    {
        get => _hotkeysPaused;
        set { _hotkeysPaused = value; if (value) _hotkeys.Bind(Array.Empty<(string, Action)>()); else BindHotkeys(); }
    }

    public bool PositionsLocked
    {
        get => S.LockPositions;
        set { S.LockPositions = value; _settings.SaveDelayed(); }
    }

    public bool ThumbnailsHidden { get => _allHidden; set => _allHidden = value; }

    public bool DoNotDisturb
    {
        get => _alerts?.DoNotDisturbOn ?? false;
        set { if (_alerts != null) _alerts.DoNotDisturbOn = value; }
    }

    public IReadOnlyList<string> Profiles => _settings.GetProfileNames();

    /// <summary>Switching profile places every thumbnail where the new profile keeps it, and binds its keys.</summary>
    public string Profile
    {
        get => S.LastUsedProfile;
        set
        {
            if (value == S.LastUsedProfile) return;
            _settings.SwitchProfile(value);
            ResetThumbnails();
            if (!_hotkeysPaused) BindHotkeys();
        }
    }

    /// <summary>Closes every thumbnail and makes them again, each where the current profile keeps it.</summary>
    private void ResetThumbnails()
    {
        foreach (var thumb in _thumbs.Values) thumb.Close();
        _thumbs.Clear();
        Sync();
    }

    public void OpenSettings() => OpenSettings(0);

    /// <summary>Opens the settings window, or brings the open one forward.</summary>
    private void OpenSettings(int page)
    {
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, page);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; CaptureHotkey(false); };
        _settingsWindow.Show();
    }

    // ── Settings window ──

    SettingsService ISettingsHost.Settings => _settings;

    IReadOnlyCollection<string> ISettingsHost.RunningCharacters() => RunningCharacters();

    void ISettingsHost.HotkeysChanged() { if (!_hotkeysPaused) BindHotkeys(); }

    /// <summary>While a hotkey box listens, the keys are not grabbed: a key that is already a hotkey would go to the
    /// grab and never reach the window.</summary>
    public void CaptureHotkey(bool capturing)
    {
        _capturing = capturing;
        if (capturing) _hotkeys.Bind(Array.Empty<(string, Action)>());
        else if (!_hotkeysPaused) BindHotkeys();
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
        if (!_hotkeysPaused) BindHotkeys();
        _alerts?.Reapply();
        TrayMenu?.Rebuild();
    }

    public void Exit()
    {
        _alerts?.Dispose();
        _settings.Dispose();
        foreach (var thumb in _thumbs.Values) thumb.Close();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ── Hotkeys ──

    internal void BindHotkeys()
    {
        if (!_keysInScope) { _hotkeys.Bind(Array.Empty<(string, Action)>()); return; }
        var keys = new List<(string, Action)>();
        foreach (var (character, binding) in _settings.CurrentProfile.Hotkeys)
            keys.Add((binding.Key, () => SwitchTo(character)));
        foreach (var group in S.HotkeyGroups.Values)
        {
            keys.Add((group.ForwardsHotkey, () => Cycle(group.Characters, true)));
            keys.Add((group.BackwardsHotkey, () => Cycle(group.Characters, false)));
        }
        keys.Add((S.GlobalCycleForwardHotkey, () => Cycle(null, true)));
        keys.Add((S.GlobalCycleBackwardHotkey, () => Cycle(null, false)));
        keys.Add((S.PreviousClientHotkey, () => { if (_previousActive != 0) _clients.Activate(_previousActive); }));
        keys.Add((S.HideShowThumbnailsHotkey, () => _allHidden = !_allHidden));
        keys.Add((S.LockPositionsHotkey, () => { S.LockPositions = !S.LockPositions; _settings.SaveDelayed(); }));
        var wanted = keys.Where(k => !string.IsNullOrWhiteSpace(k.Item1)).ToList();
        var refused = _hotkeys.Bind(wanted);
        Console.WriteLine($"Hotkeys: {wanted.Count - refused.Count} bound");
        foreach (var key in refused) Console.WriteLine($"Hotkey not bound (no such key on Linux/X11): {key}");
    }

    private void SwitchTo(string character)
    {
        var thumb = _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, character, StringComparison.OrdinalIgnoreCase));
        if (thumb != null) _clients.Activate(thumb.Client.Window);
    }

    /// <summary>The next or previous running client: of <paramref name="order"/> (a group's switching order) when
    /// given, else of every client in thumbnail order. Counted from the client in front (CycleStep, as on Windows).</summary>
    private void Cycle(IReadOnlyList<string>? order, bool forward)
    {
        var list = order == null
            ? _thumbs.Values.ToList()
            : order.Select(n => _thumbs.Values.FirstOrDefault(t => string.Equals(t.Client.Character, n, StringComparison.OrdinalIgnoreCase)))
                   .OfType<ThumbnailWindow>().ToList();
        if (list.Count == 0) return;
        int current = list.FindIndex(t => t.Client.Window == _lastActive);
        _clients.Activate(list[CycleStep.Next(current, list.Count, forward)].Client.Window);
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
            seconds == 0 ? null : DateTime.UtcNow.AddSeconds(seconds), SettingsUi.AlertFields.Steady(S, def));
        Console.WriteLine($"Alert: {character} {alertType} ({severity})");
    }

    private void Tick()
    {
        if (_ticks++ % 20 == 0) Sync();
        var watch = Stopwatch.StartNew();
        uint active = _clients.Active();
        if (active != _lastActive && _thumbs.ContainsKey(active))
        {
            if (_thumbs.ContainsKey(_lastActive)) _previousActive = _lastActive;
            _lastActive = active;
            _alerts?.Looked(_thumbs[active].Client.Character);
        }
        bool inScope = S.GlobalHotkeys || _thumbs.ContainsKey(active) || _clients.IsOurs(active);
        if (inScope != _keysInScope)
        {
            _keysInScope = inScope;
            if (!_hotkeysPaused && !_capturing) BindHotkeys();
        }
        // An alert ends when its time is up, or when the player switches to that client.
        var now = DateTime.UtcNow;
        foreach (var (who, border) in _alertBorders.ToList())
            if (border.Until < now || WindowOf(who) == active && active != 0) _alertBorders.Remove(who);
        foreach (var thumb in _thumbs.Values)
        {
            bool isActive = thumb.Client.Window == active;
            bool shown = !_allHidden && !Hidden(thumb.Client.Character) && !(isActive && S.HideActiveThumbnail);
            if (shown != thumb.IsVisible) { if (shown) thumb.Show(); else thumb.Hide(); }
            if (!shown) continue;
            thumb.CanDrag = !S.LockPositions;
            thumb.Apply(Look(thumb.Client.Character, isActive));
            thumb.Update(_clients);
        }
        _drawMs += watch.Elapsed.TotalMilliseconds;
        if (_ticks % 100 == 0)
        {
            Console.WriteLine($"{_thumbs.Count} thumbnail(s), {_drawMs / 100:F1} ms per redraw");
            _drawMs = 0;
        }
    }

    private bool Hidden(string character) => S.ThumbnailVisibility.TryGetValue(character, out var v) && v != 0;

    private ThumbnailLook Look(string character, bool isActive)
    {
        IBrush border = Brush(BorderColorRule.Resolve(S, character, isActive, RunningCharacters));
        double thickness = isActive ? S.ClientHighlightBorderThickness
            : BorderColorRule.ShowsInactiveBorder(S, character) ? S.InactiveClientBorderThickness : 0;
        if (_alertBorders.TryGetValue(character, out var alert)
            && (alert.Steady || Environment.TickCount64 / alert.RateMs % 2 == 0))
            (border, thickness) = (alert.Brush, Math.Max(1, S.AlertBorderThickness));
        var label = StreamerMask.Name(StreamerKind.Character,
            S.ThumbnailAnnotations.TryGetValue(character, out var note) && !string.IsNullOrWhiteSpace(note) ? note : character);
        var style = S.ThumbnailLabelStyles.GetValueOrDefault(character);
        var textColour = style is { Color.Length: > 0 } ? style.Color : S.ThumbnailTextColor;
        double size = style is { Size: > 0 } ? style.Size : double.TryParse(S.ThumbnailTextSize, out var fs) ? fs : 12;
        return new ThumbnailLook(border, thickness, label, S.ShowThumbnailTextOverlay, Brush(textColour), size,
            S.ThumbnailTextFont, Math.Clamp(S.ThumbnailOpacity, 0, 255) / 255.0, S.ShowThumbnailsAlwaysOnTop);
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

    private void Sync()
    {
        var found = _clients.Find();
        foreach (var gone in _thumbs.Keys.Except(found.Select(f => f.Window)).ToList())
        {
            _thumbs[gone].Close();
            _thumbs.Remove(gone);
            _clients.Forget(gone);
            Console.WriteLine($"Client closed: window {gone}");
        }
        foreach (var client in found.Where(f => !_thumbs.ContainsKey(f.Window)))
        {
            var thumb = new ThumbnailWindow(client, Place(client.Character));
            thumb.Clicked += c => _clients.Activate(c.Window);
            thumb.Moved += t =>
            {
                _settings.SaveThumbnailPosition(t.Client.Character, t.Position.X, t.Position.Y, (int)t.Width, (int)t.Height);
                if (Environment.GetEnvironmentVariable("EVEMP_DEBUG_KEYS") == "1") Console.WriteLine($"[move] {t.Client.Character} -> {t.Position}");
            };
            _thumbs[client.Window] = thumb;
            Console.WriteLine($"Client found: {client.Character} (window {client.Window})");
        }
    }

    /// <summary>The character's saved place, else the next free spot in a row from the start location.</summary>
    private PixelRect Place(string character)
    {
        if (_settings.GetThumbnailPosition(character) is { Width: > 0, Height: > 0 } saved)
            return new PixelRect((int)saved.X, (int)saved.Y, (int)saved.Width, (int)saved.Height);
        var start = S.ThumbnailStartLocation;
        int w = Math.Max(1, (int)start.Width), h = Math.Max(1, (int)start.Height);
        return new PixelRect((int)start.X + _thumbs.Count * (w + Gap), (int)start.Y, w, h);
    }
}
