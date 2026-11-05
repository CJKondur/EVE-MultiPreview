using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>What the tray menu can ask of the app.</summary>
public interface ITrayActions
{
    bool HotkeysPaused { get; set; }
    bool PositionsLocked { get; set; }
    bool ThumbnailsHidden { get; set; }
    bool SecondThumbnailsHidden { get; set; }
    bool DoNotDisturb { get; set; }
    bool StreamerMode { get; set; }
    bool FocusModeOn { get; set; }
    IReadOnlyList<string> Profiles { get; }
    string Profile { get; set; }
    void OpenSettings();
    void OpenAlertHistory();
    void SaveClientPositions();
    void RestoreClientPositions();
    /// <summary>Fixed slots are on (ClientPositionMode 3): Snap to slots works and Restore positions does not, as on Windows.</summary>
    bool SlotsOn { get; }
    void ApplyAllClientSlots();
    void Exit();
}

/// <summary>The tray icon (freedesktop StatusNotifierItem: KDE shows it as is, GNOME with the AppIndicator extension)
/// and its menu, in the player's language. The menu is rebuilt each time a choice changes, so ticks and the profile
/// list are always current.</summary>
public sealed class Tray
{
    private readonly ITrayActions _app;
    private readonly TrayIcon _icon;

    public Tray(Application application, ITrayActions app)
    {
        _app = app;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Icon.ico");
        _icon = new TrayIcon { Icon = stream != null ? new WindowIcon(stream) : null, ToolTipText = "EVE MultiPreview" };
        _icon.Clicked += (_, _) => _app.OpenSettings();
        TrayIcon.SetIcons(application, new TrayIcons { _icon });
        Rebuild();
    }

    private static string T(string key, string english) => LocalizationService.Str(key, english);

    public void Rebuild()
    {
        var menu = new NativeMenu();
        menu.Add(Item(T("L.Tray.Settings", "Settings"), _app.OpenSettings));
        menu.Add(Item(T("L.Tray.AlertHistory", "Alert history…"), _app.OpenAlertHistory));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Check(T("L.Tray.Suspend", "Pause hotkeys"), _app.HotkeysPaused, on => _app.HotkeysPaused = on));
        menu.Add(Check(T("L.Tray.Lock", "Lock positions"), _app.PositionsLocked, on => _app.PositionsLocked = on));
        menu.Add(Check(T("L.Tray.Toggle", "Hide thumbnails"), _app.ThumbnailsHidden, on => _app.ThumbnailsHidden = on));
        menu.Add(Check(T("L.Tray.HideSecondary", "Hide second thumbnails"), _app.SecondThumbnailsHidden, on => _app.SecondThumbnailsHidden = on));
        menu.Add(Check(T("L.Tray.Dnd", "Do not disturb"), _app.DoNotDisturb, on => _app.DoNotDisturb = on));
        menu.Add(Check(T("L.Tray.FocusMode", "Focus mode"), _app.FocusModeOn, on => _app.FocusModeOn = on));
        menu.Add(Check(T("L.Tray.StreamerMode", "Streamer mode"), _app.StreamerMode, on => _app.StreamerMode = on));

        var profiles = new NativeMenu();
        foreach (var name in _app.Profiles)
            profiles.Add(Check(name, name == _app.Profile, _ => _app.Profile = name));
        menu.Add(new NativeMenuItem(string.Format(T("L.Tray.ProfileFmt", "Profile: {0}"), _app.Profile)) { Menu = profiles });

        // The Windows tray's "Client windows": the places "Remember client positions" restores, and the fixed slots.
        var windows = new NativeMenu();
        windows.Add(Item(T("L.Tray.SavePositions", "Save positions"), _app.SaveClientPositions));
        var restore = Item(T("L.Tray.RestorePositions", "Restore positions"), _app.RestoreClientPositions);
        restore.IsEnabled = !_app.SlotsOn;
        windows.Add(restore);
        var snap = Item(T("L.Tray.SnapToSlots", "Snap to slots"), _app.ApplyAllClientSlots);
        snap.IsEnabled = _app.SlotsOn;
        windows.Add(snap);
        menu.Add(new NativeMenuItem(T("L.Tray.ClientPositions", "Client windows")) { Menu = windows });

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(T("L.Tray.Exit", "Exit MultiPreview"), _app.Exit));
        _icon.Menu = menu;
    }

    private static NativeMenuItem Item(string header, Action run)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => run();
        return item;
    }

    private NativeMenuItem Check(string header, bool on, Action<bool> set)
    {
        var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
        item.Click += (_, _) => { set(!on); Rebuild(); };
        return item;
    }
}
