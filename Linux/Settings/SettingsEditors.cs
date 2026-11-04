using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>The Linux editors the settings window (SharedSettingsWindow) puts in its cards where a shared page names an
/// editor (LinuxEditors maps the ids). Each is built from the settings as they are when its page shows, and every control
/// writes its change at once and asks for a delayed save. One partial file per area under Linux/Settings.</summary>
internal sealed partial class SettingsEditors
{
    private readonly ISettingsHost _host;
    private readonly Window _window;
    private readonly Action _refresh;
    private readonly Action _relocalize;
    /// <summary>The clash lines of the page that shows: each recomputes itself from the settings (a key was set, cleared or moved).</summary>
    private readonly List<Action> _clashLines = new();

    /// <param name="refresh">Builds the page that is showing again (a group was added or removed).</param>
    /// <param name="relocalize">Builds the whole window again (the language changed).</param>
    public SettingsEditors(ISettingsHost host, Window window, Action refresh, Action relocalize)
    {
        _host = host;
        _window = window;
        _refresh = refresh;
        _relocalize = relocalize;
    }

    /// <summary>The shared window builds a page anew: the clash lines of the editors it showed before are dropped.</summary>
    internal void ForgetClashLines() => _clashLines.Clear();

    private AppSettings S => _host.Settings.Settings;
    private static string T(string key, string english) => Ui.T(key, english);
    private void Save() => _host.Settings.SaveDelayed();


    /// <summary>A hotkey box. A change is stored by <paramref name="store"/>, saved and bound; then every clash line of the
    /// page is worked out again, because a key set here can make another box's key a clash. The key is still kept (the
    /// key bound last is the one that works).</summary>
    private Control HotkeyField(string id, string value, Action<string> store)
    {
        var row = Ui.Hotkey(value, key => { store(key); Save(); _host.HotkeysChanged(); foreach (var line in _clashLines.ToList()) line(); }, _host);
        var warning = LinuxLooks.Paint(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, IsVisible = false }, TextBlock.ForegroundProperty, "Fx.WarningBrush");
        void Show()
        {
            var others = HotkeyClashes.With(AllHotkeys(), id);
            warning.IsVisible = others.Count > 0;
            warning.Text = others.Count == 0 ? "" : string.Format(T("L.Linux.HotkeyClash", "Same key as: {0}. Only one of them gets it."), string.Join(", ", others));
        }
        _clashLines.Add(Show);
        Show();
        // Under the box: beside it the line would run out of the window (and out of the Characters table's column).
        var field = new StackPanel { Spacing = 2 };
        field.Children.Add(row);
        field.Children.Add(warning);
        return field;
    }

    /// <summary>Every key MultiPreview binds, with the action that owns it: the characters' keys, the groups' two keys, the five global ones.</summary>
    private List<(string Id, string Label, string Key)> AllHotkeys()
    {
        var all = new List<(string, string, string)>();
        foreach (var (character, binding) in CharacterDirectory.Current(S).Hotkeys) all.Add(("char:" + character, character, binding.Key));
        foreach (var (name, group) in S.HotkeyGroups)
        {
            all.Add(("group:" + name + ":next", name + " – " + T("L.Linux.GroupNext", "Next key"), group.ForwardsHotkey));
            all.Add(("group:" + name + ":previous", name + " – " + T("L.Linux.GroupPrevious", "Previous key"), group.BackwardsHotkey));
        }
        all.Add(("global:" + T("L.S.clients.hotkeys.cycleNext", "Next client"), T("L.S.clients.hotkeys.cycleNext", "Next client"), S.GlobalCycleForwardHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.cyclePrevious", "Previous client"), T("L.S.clients.hotkeys.cyclePrevious", "Previous client"), S.GlobalCycleBackwardHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.backToPrevious", "Back to the client before this one"), T("L.S.clients.hotkeys.backToPrevious", "Back to the client before this one"), S.PreviousClientHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.hideAll", "Hide or show all thumbnails"), T("L.S.clients.hotkeys.hideAll", "Hide or show all thumbnails"), S.HideShowThumbnailsHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.hidePip", "Hide or show second thumbnails"), T("L.S.clients.hotkeys.hidePip", "Hide or show second thumbnails"), S.HideSecondaryHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.commandPalette", "Open the command palette"), T("L.S.clients.hotkeys.commandPalette", "Open the command palette"), S.CommandPaletteHotkey));
        all.Add(("global:focus", T("L.S.clients.groups.focusKey", "Focus mode on or off"), S.FocusModeHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.hideCrops", "Hide or show crop windows"), T("L.S.clients.hotkeys.hideCrops", "Hide or show crop windows"), S.HideShowCropsHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.lockPositions", "Lock or unlock thumbnail positions"), T("L.S.clients.hotkeys.lockPositions", "Lock or unlock thumbnail positions"), S.LockPositionsHotkey));
        all.Add(("global:" + T("L.S.clients.hotkeys.lastAlert", "Go to the last alert"), T("L.S.clients.hotkeys.lastAlert", "Go to the last alert"), S.GoToLastAlertHotkey));
        all.Add(("global:" + T("L.S.alerts.sounds.dndHotkey", "Do not disturb on or off"), T("L.S.alerts.sounds.dndHotkey", "Do not disturb on or off"), S.DoNotDisturbHotkey));
        return all;
    }

    /// <summary>A number box with its unit written after it.</summary>
    private static Control WithUnit(Control box, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = unit, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
