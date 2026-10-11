using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Views;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Sounds › Do not disturb: a line that says so while do-not-disturb is on right now, whether the
/// hotkey, the tray or the quiet hours switched it on (IDEAS #32). A player who hears nothing finds out why where the
/// sounds are set. It shows the app's state, not a setting: nothing here is saved. Looked at again every second while
/// shown. With do-not-disturb off, and in a window opened without the app, there is no line.</summary>
public sealed class DoNotDisturbStateEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.sounds.dndState";

    /// <summary>The line's two texts: every alert sound is muted, or every one but a critical alert's ("Critical
    /// alerts still play").</summary>
    public const string OnKey = "L.S.DndState.On", OnCriticalKey = "L.S.DndState.OnCritical";

    private readonly SettingsContext _ctx;
    private readonly Func<bool>? _on;
    private readonly TextBlock _line = new();

    public DoNotDisturbStateEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _on = ctx.Services.DoNotDisturbOn;      // null in a window opened without the app: nobody to ask
        _line.SetResourceReference(StyleProperty, "Fx.Label");
        AutomationProperties.SetLiveSetting(_line, AutomationLiveSetting.Polite);   // a screen reader says it when it appears

        // The look of an info line (the Fx.Info template): a coloured bar, the note glyph, the text.
        var glyph = new TextBlock { Text = ((char)0xE946).ToString(), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0) };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.IconFont");
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Fx.InfoBrush");
        Loc.SetNameKey(glyph, "L.S.Info.Note");
        DockPanel.SetDock(glyph, Dock.Left);
        var row = new DockPanel();
        row.Children.Add(glyph);
        row.Children.Add(_line);
        var bar = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8), Child = row };
        bar.SetResourceReference(Border.BorderBrushProperty, "Fx.InfoBrush");
        bar.SetResourceReference(Border.BackgroundProperty, "Fx.CardBrush");
        bar.SetResourceReference(Border.CornerRadiusProperty, "Fx.Radius");
        Content = bar;

        LiveRefresh.Attach(this, Refresh, seconds: 1);
        Refresh();
    }

    /// <summary>The string key of the line as shown, or null while there is no line.</summary>
    public string? ShownKey { get; private set; }

    public void Refresh()
    {
        var key = _on?.Invoke() == true ? (_ctx.S.DoNotDisturbCriticalPlays ? OnCriticalKey : OnKey) : null;
        bool changed = key != ShownKey;
        ShownKey = key;
        Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        if (!changed || key == null) return;
        Loc.SetKey(_line, key);         // the text follows a language change by itself
        Announce.Changed(_line);        // a live region announces nothing by itself: said when the line appears or changes
    }
}
