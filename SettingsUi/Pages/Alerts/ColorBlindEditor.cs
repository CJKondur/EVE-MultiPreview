using System.Collections.Generic;
using System.Windows.Automation;
using EveMultiPreview.Services;
using CheckBox = System.Windows.Controls.CheckBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Look &amp; severity › Colour-blind mode: one switch. Declining the question puts it back.</summary>
public sealed class ColorBlindEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly CheckBox _toggle;
    private bool _loading;

    public ColorBlindEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _toggle = EditorCard.Toggle(ctx.S.ColorBlindMode, OnToggle);
        Loc.SetNameKey(_toggle, "L.S.alerts.look.colorBlind");
        Content = EditorCard.Build("alerts.look.colorBlind", _toggle);
    }

    public bool IsOn => _toggle.IsChecked == true;

    /// <summary>What a click on the switch does (tests).</summary>
    public void SetOn(bool on) => _toggle.IsChecked = on;

    private void OnToggle(bool on)
    {
        if (_loading) return;
        if (!ColorBlindSwitch.Set(_ctx, on)) Refresh();   // declined: the switch goes back
    }

    public void Refresh()
    {
        _loading = true;
        _toggle.IsChecked = _ctx.S.ColorBlindMode;
        _loading = false;
    }
}
