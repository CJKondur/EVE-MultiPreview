using System;
using System.Collections.Generic;
using System.Windows.Controls;
using EveMultiPreview.Services;
using CheckBox = System.Windows.Controls.CheckBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Home › Quick actions (spec §5): hide all, lock positions, pause hotkeys, click-through.
/// Each switch shows the current state and the key that does the same; greyed out without the
/// service. The toggles are the existing ThumbnailManager / HotkeyService methods.</summary>
public sealed class QuickActionsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly StackPanel _rows = new();

    public QuickActionsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Content = _rows;
        Refresh();
    }

    public IReadOnlyList<CheckBox> Switches { get; private set; } = Array.Empty<CheckBox>();

    public void Refresh()
    {
        var tm = _ctx.Services.Thumbnails;
        var hotkeys = _ctx.Services.Hotkeys;
        var switches = new List<CheckBox>();
        _rows.Children.Clear();
        Add("home.quick.hideAll", _ctx.S.HideShowThumbnailsHotkey, tm?.AllThumbnailsHidden, () => tm?.ToggleAllVisibility());
        Add("home.quick.lock", _ctx.S.LockPositionsHotkey, tm == null ? null : _ctx.S.LockPositions, () => tm?.ToggleLockPositions());
        Add("home.quick.pause", _ctx.S.SuspendHotkey, hotkeys?.IsSuspended, () => hotkeys?.ToggleSuspend());
        Add("home.quick.clickThrough", _ctx.S.ClickThroughHotkey, tm?.ClickThroughOn, () => tm?.ToggleClickThrough());
        Switches = switches;

        void Add(string id, string key, bool? state, Action toggle)
        {
            var text = HotkeyText.ToDisplay(key);
            var keyText = EditorCard.Value(text.Length > 0 ? text : SettingsContext.Str("L.S.Hotkey.None"), secondary: true);
            keyText.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.MonoFont");
            var sw = EditorCard.Toggle(state ?? false, _ => { toggle(); Refresh(); });
            sw.IsEnabled = state != null;
            switches.Add(sw);
            _rows.Children.Add(EditorCard.Build(id, EditorCard.Row(keyText, sw)));
        }
    }
}
