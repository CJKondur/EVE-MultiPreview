using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Slider = System.Windows.Controls.Slider;

namespace EveMultiPreview.SettingsUi;

/// <summary>Clients › Game audio › one volume slider per running client (old BuildClientVolumeRows).</summary>
public sealed class VolumeEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private string _signature = "";

    public VolumeEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        LiveRefresh.Attach(this, () => { if (Signature(CharacterDirectory.Running(_ctx)) != _signature) Refresh(); });
        Refresh();
    }

    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    private string Signature(IEnumerable<string> names) =>
        string.Join("\n", names.Select(n => n + "|" + CharacterFields.Volume(_ctx.S, n)));

    public void Refresh()
    {
        Names = CharacterDirectory.Running(_ctx);
        _signature = Signature(Names);
        var rows = new StackPanel();
        if (Names.Count == 0) rows.Children.Add(EditorCard.Note("L.S.clients.audio.volume.Empty"));
        foreach (var name in Names) rows.Children.Add(Row(name));
        Content = EditorCard.Build("clients.audio.volume", null, rows);
    }

    private UIElement Row(string name)
    {
        var readout = EditorCard.Value("", secondary: true);
        readout.MinWidth = 48;
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Width = 220, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 5, Value = CharacterFields.Volume(_ctx.S, name),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
        };
        void Show(double v) => readout.Text = ((int)Math.Round(v)) + SettingsContext.Str("L.S.Unit.Percent");
        Show(slider.Value);
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            Show(v);
            if (_ctx.Services.Thumbnails is { } tm) tm.SetClientVolume(name, v);   // stores, saves, sets the running client
            else { CharacterFields.SetVolume(_ctx.S, name, v); _ctx.Applier.Request(ApplyScope.Save); }
            _signature = Signature(Names);   // our own write must not trigger a redraw mid-drag
        };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = EditorCard.Value(name);
        grid.Children.Add(label);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(readout);
        return grid;
    }
}
