using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>The tray menu's command names (ToolStripItem.Name). The parity test checks the old menu's commands are all
/// still here (Plan 4).</summary>
public static class TrayCommands
{
    public const string Settings = "tray.settings";
    public const string Profiles = "tray.profiles";
    /// <summary>One per profile, inside Profiles.</summary>
    public const string Profile = "tray.profile";
    public const string ManageProfiles = "tray.manageProfiles";
    /// <summary>Opens the alert history window (IDEAS #1).</summary>
    public const string AlertHistory = "tray.alertHistory";
    /// <summary>Opens the hotkey cheat sheet without its key (IDEAS #16).</summary>
    public const string HotkeySheet = "tray.hotkeySheet";
    public const string Suspend = "tray.suspend";
    /// <summary>Do-not-disturb on or off (IDEAS #32).</summary>
    public const string DoNotDisturb = "tray.doNotDisturb";
    public const string Lock = "tray.lock";
    public const string HideThumbnails = "tray.hideThumbnails";
    public const string ClickThrough = "tray.clickThrough";
    public const string HideSecondary = "tray.hideSecondary";
    public const string Secondary = "tray.secondary";
    /// <summary>One per character with a second thumbnail, inside Secondary.</summary>
    public const string SecondaryCharacter = "tray.secondaryCharacter";
    public const string ClientWindows = "tray.clientWindows";
    public const string SavePositions = "tray.savePositions";
    public const string RestorePositions = "tray.restorePositions";
    public const string SnapToSlots = "tray.snapToSlots";
    public const string CloseAll = "tray.closeAll";
    public const string Exit = "tray.exit";
}

/// <summary>What the tray menu reads and does. App wires each one to the call the old menu made; tests use fakes.</summary>
public sealed class TrayMenuActions
{
    public Func<AppSettings?> Settings { get; init; } = () => null;
    public Func<IReadOnlyList<string>> ProfileNames { get; init; } = () => Array.Empty<string>();
    public Action OpenSettings { get; init; } = () => { };
    /// <summary>Opens settings on a page (the new window; the old one just opens).</summary>
    public Action<string> OpenSettingsPage { get; init; } = _ => { };
    /// <summary>Opens the alert history window (IDEAS #1).</summary>
    public Action OpenAlertHistory { get; init; } = () => { };
    /// <summary>Opens, or closes, the hotkey cheat sheet (IDEAS #16).</summary>
    public Action OpenHotkeySheet { get; init; } = () => { };
    /// <summary>The time now, UTC, for the downtime line (IDEAS #9). A test hands in its own.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    public Action<string> SwitchProfile { get; init; } = _ => { };
    public Func<bool> IsSuspended { get; init; } = () => false;
    public Action ToggleSuspend { get; init; } = () => { };
    /// <summary>Whether do-not-disturb is on right now, by the hotkey, this item or the quiet hours (IDEAS #32).</summary>
    public Func<bool> DoNotDisturbOn { get; init; } = () => false;
    public Action ToggleDoNotDisturb { get; init; } = () => { };
    public Action ToggleLock { get; init; } = () => { };
    public Func<bool> ThumbnailsHidden { get; init; } = () => false;
    public Action ToggleThumbnails { get; init; } = () => { };
    public Func<bool> ClickThroughOn { get; init; } = () => false;
    public Action ToggleClickThrough { get; init; } = () => { };
    public Func<bool> SecondaryHidden { get; init; } = () => false;
    public Action ToggleSecondary { get; init; } = () => { };
    /// <summary>Turns one character's second thumbnail on or off (saved), as the old per-character list did.</summary>
    public Action<string, bool> SetSecondary { get; init; } = (_, _) => { };
    public Action SavePositions { get; init; } = () => { };
    public Action RestorePositions { get; init; } = () => { };
    public Action SnapToSlots { get; init; } = () => { };
    public Func<bool> ConfirmCloseAll { get; init; } = () => false;
    public Action CloseAll { get; init; } = () => { };
    public Action Exit { get; init; } = () => { };
}

/// <summary>The tray icon's right-click menu (Plan 4, the F2 reference): the downtime countdown line (IDEAS #9, shown
/// while the switch is on; a line to read, not a command), Settings and the profile, the four toggles plus
/// second thumbnails with their hotkeys, client windows, Close all EVE clients… and Exit. Every item calls what the old
/// menu called. Each time the menu opens it re-reads everything: labels (a language change), check marks (a hotkey or
/// Home may have changed the state), hotkey hints, the profile list and the second-thumbnail list.</summary>
public sealed class TrayMenu : IDisposable
{
    /// <summary>App › Profiles &amp; backups, where Manage profiles… goes.</summary>
    public const string ProfilesPageId = "app.profiles";

    private readonly TrayMenuActions _a;
    private readonly List<(ToolStripItem Item, string Key, string English)> _labels = new();
    private readonly ToolStripMenuItem _downtime = new() { Enabled = false };
    private readonly ToolStripSeparator _downtimeSeparator = new();
    private readonly ToolStripMenuItem _profiles, _suspend, _doNotDisturb, _hotkeySheet, _lock, _hide, _clickThrough, _hideSecondary, _secondary, _restore, _snap;

    public TrayMenu(TrayMenuActions actions)
    {
        _a = actions;
        Strip = new ContextMenuStrip();
        FluentMenu.Style(Strip, showIcons: true);

        // IDEAS #9: first in the menu, no command name (it is not one), hidden while the switch is off.
        Strip.Items.Add(_downtime);
        Strip.Items.Add(_downtimeSeparator);
        Strip.Items.Add(Item(TrayCommands.Settings, "L.Tray.Settings", "Settings", FluentMenu.Glyphs.Settings, _a.OpenSettings));
        _profiles = Item(TrayCommands.Profiles, null, "", FluentMenu.Glyphs.Profile, null);
        FluentMenu.Style(_profiles.DropDown, showIcons: false);
        Strip.Items.Add(_profiles);
        Strip.Items.Add(Item(TrayCommands.AlertHistory, "L.Tray.AlertHistory", "Alert history…", FluentMenu.Glyphs.History, _a.OpenAlertHistory));
        // IDEAS #16: no keyboard glyph in FluentMenu.Glyphs, and none is added.
        _hotkeySheet = Item(TrayCommands.HotkeySheet, "L.Tray.HotkeySheet", "Hotkey cheat sheet", null, _a.OpenHotkeySheet);
        Strip.Items.Add(_hotkeySheet);
        Strip.Items.Add(new ToolStripSeparator());

        // The alert hub follows the pause by itself (AlertHub.FollowPauseOf, IDEAS P37), whichever way it was paused.
        _suspend = Item(TrayCommands.Suspend, "L.Tray.Suspend", "Pause hotkeys", FluentMenu.Glyphs.Pause, _a.ToggleSuspend);
        // IDEAS #32: alert sounds off, everything else as it is. The mark follows the live state, quiet hours included.
        _doNotDisturb = Item(TrayCommands.DoNotDisturb, "L.Tray.Dnd", "Do not disturb", null, _a.ToggleDoNotDisturb);
        _lock = Item(TrayCommands.Lock, "L.Tray.Lock", "Lock positions", FluentMenu.Glyphs.Lock, _a.ToggleLock);
        _hide = Item(TrayCommands.HideThumbnails, "L.Tray.Toggle", "Hide thumbnails", FluentMenu.Glyphs.Eye, _a.ToggleThumbnails);
        _clickThrough = Item(TrayCommands.ClickThrough, "L.Tray.ClickThrough", "Click-through", FluentMenu.Glyphs.Pointer, _a.ToggleClickThrough);
        _hideSecondary = Item(TrayCommands.HideSecondary, "L.Tray.HideSecondary", "Hide second thumbnails", FluentMenu.Glyphs.SecondThumbnails, _a.ToggleSecondary);
        _secondary = Item(TrayCommands.Secondary, "L.Tray.PiP", "Second thumbnails", FluentMenu.Glyphs.Window, null);
        FluentMenu.Style(_secondary.DropDown, showIcons: false);
        Strip.Items.AddRange(new ToolStripItem[] { _suspend, _doNotDisturb, _lock, _hide, _clickThrough, _hideSecondary, _secondary, new ToolStripSeparator() });

        var windows = Item(TrayCommands.ClientWindows, "L.Tray.ClientPositions", "Client windows", FluentMenu.Glyphs.Monitor, null);
        FluentMenu.Style(windows.DropDown, showIcons: false);
        windows.DropDownItems.Add(Item(TrayCommands.SavePositions, "L.Tray.SavePositions", "Save positions", null, _a.SavePositions));
        _restore = Item(TrayCommands.RestorePositions, "L.Tray.RestorePositions", "Restore positions", null, _a.RestorePositions);
        _snap = Item(TrayCommands.SnapToSlots, "L.Tray.SnapToSlots", "Snap to slots", null, _a.SnapToSlots);
        windows.DropDownItems.Add(_restore);
        windows.DropDownItems.Add(_snap);
        Strip.Items.Add(windows);

        var closeAll = Item(TrayCommands.CloseAll, "L.Tray.CloseAll", "Close all EVE clients…", FluentMenu.Glyphs.Close, () =>
        {
            if (_a.ConfirmCloseAll()) _a.CloseAll();
        });
        FluentMenu.MarkDestructive(closeAll);
        Strip.Items.Add(closeAll);
        Strip.Items.Add(new ToolStripSeparator());
        Strip.Items.Add(Item(TrayCommands.Exit, "L.Tray.Exit", "Exit MultiPreview", null, _a.Exit));

        Strip.Opening += (_, _) => Refresh();
        Refresh();
    }

    public ContextMenuStrip Strip { get; }

    /// <summary>The countdown line at the top of the menu (IDEAS #9), for tests.</summary>
    public ToolStripMenuItem DowntimeLine => _downtime;

    /// <summary>Windows' limit for a tray tooltip. NotifyIcon.Text throws on anything longer.</summary>
    public const int TooltipMax = 127;

    /// <summary>A crossed bell: do-not-disturb, where the tip has no room for the words (IDEAS #32).</summary>
    public static readonly string DoNotDisturbMark = char.ConvertFromUtf32(0x1F515);

    /// <summary>The tray icon's tooltip: the name and the pause state, as before, then (once MultiPreview has handled an
    /// error) how many and where to look (IDEAS #55). Cut to Windows' 127 characters.</summary>
    public static string TooltipText(bool suspended, int errors, bool doNotDisturb = false)
    {
        var text = UncutTooltipText(suspended, errors, doNotDisturb);
        return text.Length <= TooltipMax ? text : text[..TooltipMax];
    }

    /// <summary>The tooltip before it is cut to Windows' limit; a test checks the translations fit without the cut.</summary>
    public static string UncutTooltipText(bool suspended, int errors, bool doNotDisturb = false)
    {
        var text = suspended ? "⏸ EVE MultiPreview (" + LocalizationService.Str("L.Tray.TipSuspended", "SUSPENDED") + ")" : "EVE MultiPreview";
        var errorLine = errors > 0
            ? "\n" + string.Format(LocalizationService.Str("L.Tray.Errors", "Errors handled: {0}. See App settings › Diagnostics."), errors)
            : "";
        if (doNotDisturb)
        {
            // IDEAS #32: in words, on a line of its own. Paused, with a long error line, in a long language, the words
            // would push the tip past Windows' limit: then a crossed bell behind the name says it instead.
            var words = "\n" + LocalizationService.Str("L.Tray.DndOn", "Do not disturb: on");
            text += text.Length + words.Length + errorLine.Length <= TooltipMax ? words : " " + DoNotDisturbMark;
        }
        return text + errorLine;
    }

    /// <summary>Re-reads labels, check marks, hotkey hints and the two lists. Runs every time the menu opens.</summary>
    public void Refresh()
    {
        // Runs on Strip.Opening, inside the NotifyIcon's WndProc: a throw here would reach Application.OnThreadException
        // and WinForms' own modal Quit dialog.
        try { RefreshCore(); }
        catch (Exception ex) { App.LogError(ex); }
    }

    private void RefreshCore()
    {
        foreach (var (item, key, english) in _labels) item.Text = LocalizationService.Str(key, english);
        var s = _a.Settings();
        bool downtime = s?.DowntimeWarnings == true;
        _downtime.Available = downtime;
        _downtimeSeparator.Available = downtime;
        if (downtime) _downtime.Text = Downtime.CountdownText(_a.UtcNow());
        _profiles.Text = string.Format(LocalizationService.Str("L.Tray.ProfileFmt", "Profile: {0}"), s?.LastUsedProfile ?? "");
        RebuildProfiles(s);

        _suspend.Checked = _a.IsSuspended();
        _doNotDisturb.Checked = _a.DoNotDisturbOn();
        _lock.Checked = s?.LockPositions ?? false;
        _hide.Checked = _a.ThumbnailsHidden();
        _clickThrough.Checked = _a.ClickThroughOn();
        _hideSecondary.Checked = _a.SecondaryHidden();

        _suspend.ShortcutKeyDisplayString = Hint(s?.SuspendHotkey);
        _doNotDisturb.ShortcutKeyDisplayString = Hint(s?.DoNotDisturbHotkey);
        _hotkeySheet.ShortcutKeyDisplayString = Hint(s?.HotkeySheetHotkey);
        _lock.ShortcutKeyDisplayString = Hint(s?.LockPositionsHotkey);
        _hide.ShortcutKeyDisplayString = Hint(s?.HideShowThumbnailsHotkey);
        _clickThrough.ShortcutKeyDisplayString = Hint(s?.ClickThroughHotkey);
        _hideSecondary.ShortcutKeyDisplayString = Hint(s?.HideSecondaryHotkey);

        RebuildSecondary(s);

        // Fixed slots own the client rect in mode 3: restoring tracked positions would pull clients out of their slots.
        bool slots = s?.ClientPositionMode == 3;
        _snap.Enabled = slots;
        _restore.Enabled = !slots;
    }

    /// <summary>The item with this command name, searched through every submenu (tests).</summary>
    public ToolStripMenuItem Find(string name) => All(Strip.Items).OfType<ToolStripMenuItem>().First(i => i.Name == name);

    /// <summary>Every command name in the menu and its submenus, as it is now.</summary>
    public IReadOnlySet<string> CommandNames() =>
        All(Strip.Items).Where(i => !string.IsNullOrEmpty(i.Name)).Select(i => i.Name!).ToHashSet();

    public void Dispose() => Strip.Dispose();

    private void RebuildProfiles(AppSettings? s)
    {
        ClearAndDispose(_profiles);
        foreach (var name in _a.ProfileNames())
        {
            var item = new ToolStripMenuItem(name) { Name = TrayCommands.Profile, Checked = name == s?.LastUsedProfile };
            item.Click += (_, _) => _a.SwitchProfile(name);   // ProfileSwitched applies it (#101)
            _profiles.DropDownItems.Add(item);
        }
        _profiles.DropDownItems.Add(new ToolStripSeparator());
        var manage = new ToolStripMenuItem(LocalizationService.Str("L.Tray.ManageProfiles", "Manage profiles…")) { Name = TrayCommands.ManageProfiles };
        manage.Click += (_, _) => _a.OpenSettingsPage(ProfilesPageId);
        _profiles.DropDownItems.Add(manage);
    }

    /// <summary>Rebuilt on every open: the old list was built once at startup, so a second thumbnail added later, or a
    /// profile switch, never showed in it.</summary>
    private void RebuildSecondary(AppSettings? s)
    {
        ClearAndDispose(_secondary);
        foreach (var (name, pip) in s?.SecondaryThumbnails ?? new Dictionary<string, SecondaryThumbnailSettings>())
        {
            var item = new ToolStripMenuItem(name) { Name = TrayCommands.SecondaryCharacter, Checked = pip.IsEnabled };
            bool on = pip.IsEnabled;
            item.Click += (_, _) => _a.SetSecondary(name, !on);
            _secondary.DropDownItems.Add(item);
        }
        _secondary.Enabled = _secondary.DropDownItems.Count > 0;
    }

    /// <summary>Removes and disposes a submenu's old items. Never an item's Image: the glyph images are a shared cache.</summary>
    private static void ClearAndDispose(ToolStripMenuItem menu)
    {
        var old = menu.DropDownItems.Cast<ToolStripItem>().ToList();
        menu.DropDownItems.Clear();
        foreach (var item in old) item.Dispose();
    }

    private ToolStripMenuItem Item(string name, string? key, string english, char? glyph, Action? click)
    {
        var item = new ToolStripMenuItem { Name = name };
        if (key != null)
        {
            item.Text = LocalizationService.Str(key, english);
            _labels.Add((item, key, english));
        }
        if (glyph is char g) FluentMenu.SetGlyph(item, g);
        if (click != null) item.Click += (_, _) => click();
        return item;
    }

    private static string? Hint(string? ahk)
    {
        var text = HotkeyText.ToDisplay(ahk);
        return text.Length > 0 ? text : null;
    }

    private static IEnumerable<ToolStripItem> All(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            yield return item;
            if (item is ToolStripMenuItem { HasDropDownItems: true } menu)
                foreach (var child in All(menu.DropDownItems)) yield return child;
        }
    }
}
