using System.Collections.Generic;
using System.Windows.Automation;
using EveMultiPreview.Services;
using CheckBox = System.Windows.Controls.CheckBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Colour-blind mode (spec D11: moved from the old header). The one card that writes several settings:
/// exactly the old palette (Services/ColorBlindPalette), after asking.</summary>
public static class ColorBlindSwitch
{
    /// <summary>What ColorBlindPalette.Apply writes, plus the mode itself and the colours it keeps to give back.</summary>
    public static readonly IReadOnlyList<string> Binds = new[]
    {
        "AppSettings.ColorBlindMode", "AppSettings.SeverityColors", "AppSettings.AlertFlashColor", "AppSettings.AlertColors",
        "AppSettings.ClientHighlightColor", "AppSettings.ThumbnailTextColor", "AppSettings.NotLoggedInColor", "AppSettings.StatOverlayTextColor",
        "AppSettings.ColorBlindSavedColors",
    };

    /// <summary>Asks first (it replaces colours the player may have chosen; turning it off gives them back, IDEAS P29),
    /// then writes the palette and re-reads every card. Returns false when nothing changed.</summary>
    public static bool Set(SettingsContext ctx, bool on)
    {
        if (ctx.S.ColorBlindMode == on) return false;
        // Off with nothing kept (turned on before colours were kept): the old question, the standard colours.
        var question = on ? "L.S.ColorBlind.ConfirmOn"
            : ColorBlindPalette.Saved(ctx.S) != null ? "L.S.ColorBlind.ConfirmOffRestore" : "L.S.ColorBlind.ConfirmOff";
        if (!ctx.Host.Confirm(SettingsContext.Str("L.S.alerts.look.colorBlind"), SettingsContext.Str(question))) return false;
        ctx.S.ColorBlindMode = on;
        ColorBlindPalette.Apply(ctx.S, on);
        // ReapplySettings paints the highlight, thumbnail text and login mark; crop window names use the text colour.
        ctx.Applier.Request(ApplyScope.Thumbnails | ApplyScope.Crops);
        ctx.Host.Reload();
        return true;
    }
}

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
