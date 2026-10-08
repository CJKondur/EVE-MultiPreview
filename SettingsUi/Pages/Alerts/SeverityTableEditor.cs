using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Slider = System.Windows.Controls.Slider;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Look &amp; severity › Severity settings (spec §5): colour, flash speed and "toast in the alert
/// hub" for critical, warning and info. No cooldown column: SeverityCooldowns never takes effect (IDEAS P28).
/// Everything here is read at the next alert or flash tick, so a change only saves.</summary>
public sealed class SeverityTableEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private static readonly string[] Columns = { "L.S.Severity.ColSeverity", "L.S.Severity.ColColour", "L.S.Severity.ColFlash", "L.S.Severity.ColHub" };

    private readonly SettingsContext _ctx;
    private readonly Grid _table = new();

    public SeverityTableEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        var note = EditorCard.Note("L.S.Severity.Note");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 8, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(_table);
        stack.Children.Add(note);
        Content = EditorCard.Build("alerts.look.severityTable", null, stack);
        Refresh();
    }

    private static string Str(string key) => SettingsContext.Str(key);

    public void Refresh()
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        foreach (var _ in Columns) _table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int header = AddRow();
        for (int c = 0; c < Columns.Length; c++) Place(EditorCard.Note(Columns[c], strong: true), header, c);
        foreach (var severity in AlertEvents.Severities)
        {
            int r = AddRow();
            var name = Str(AlertEvents.SeverityKey(severity));
            Place(AlertEvents.SeverityText(severity), r, 0);
            var swatch = EditorCard.Swatch(AlertFields.SeverityColour(_ctx.S, severity), () => PickColour(severity));
            AutomationProperties.SetName(swatch, string.Format(Str("L.S.Severity.PickColour"), name));
            Place(swatch, r, 1);
            // Info alerts are painted steady, so their flash speed does nothing (ruling 16).
            Place(severity == AlertEvents.Info ? EditorCard.Note("L.S.Severity.InfoSteady") : FlashSlider(severity, name), r, 2);
            var hub = EditorCard.Toggle(AlertFields.HubToast(_ctx.S, severity), on => SetHubToast(severity, on));
            AutomationProperties.SetName(hub, string.Format(Str("L.S.Severity.HubFor"), name));
            Place(hub, r, 3);
        }
    }

    /// <summary>Value before the handler: building a row never writes (a stored 130 ms shows as 150).</summary>
    private FrameworkElement FlashSlider(string severity, string name)
    {
        var readout = EditorCard.Value("", secondary: true);
        readout.MinWidth = 40;
        readout.TextAlignment = TextAlignment.Right;
        var slider = new Slider
        {
            Minimum = AlertFields.FlashMin, Maximum = AlertFields.FlashMax, TickFrequency = AlertFields.FlashStep,
            IsSnapToTickEnabled = true, SmallChange = AlertFields.FlashStep, LargeChange = 250, Width = 180,
            VerticalAlignment = VerticalAlignment.Center,
            Value = AlertFields.FlashSlider(AlertFields.FlashRate(_ctx.S, severity)),
        };
        AutomationProperties.SetName(slider, string.Format(Str("L.S.Severity.FlashFor"), name));
        void Show(double v) => readout.Text = ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture);
        Show(slider.Value);
        slider.ValueChanged += (_, e) => { Show(e.NewValue); SetFlash(severity, e.NewValue); };
        return EditorCard.Row(slider, readout, EditorCard.Note("L.S.Unit.Ms"));   // the unit via Loc, so it follows the language
    }

    public void PickColour(string severity)
    {
        var picked = _ctx.Host.PickColor(AlertFields.SeverityColour(_ctx.S, severity));
        if (picked == null) return;
        AlertFields.SetSeverityColour(_ctx.S, severity, picked);
        _ctx.Applier.Request(ApplyScope.Save);
        Refresh();
    }

    public void SetFlash(string severity, double ms)
    {
        var rate = (int)AlertFields.FlashSlider((int)Math.Round(ms));
        if (AlertFields.FlashRate(_ctx.S, severity) == rate) return;
        AlertFields.SetFlashRate(_ctx.S, severity, rate);
        _ctx.Applier.Request(ApplyScope.Save);
    }

    public void SetHubToast(string severity, bool on)
    {
        if (AlertFields.HubToast(_ctx.S, severity) == on) return;
        AlertFields.SetHubToast(_ctx.S, severity, on);
        _ctx.Applier.Request(ApplyScope.Save);
    }

    private int AddRow()
    {
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _table.RowDefinitions.Count - 1;
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 4, 20, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _table.Children.Add(element);
    }
}
