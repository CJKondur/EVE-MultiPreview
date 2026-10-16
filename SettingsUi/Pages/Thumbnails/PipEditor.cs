using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Slider = System.Windows.Controls.Slider;

namespace EveMultiPreview.SettingsUi;

/// <summary>Second-thumbnail (PiP) rules shared with the character page: one entry per character, found
/// ignoring case (the runtime matches clients that way); Off keeps the entry with its place, size and
/// opacity (CharacterFields.SetPip).</summary>
public static class SecondThumbnails
{
    public static string? KeyOf(AppSettings s, string name) =>
        s.SecondaryThumbnails.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Names(AppSettings s) =>
        s.SecondaryThumbnails.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Old OnSecThumbAdd: a new entry, or the existing one switched back on. Returns the key used,
    /// or null for an empty name or a second thumbnail that is already on.</summary>
    public static string? Add(AppSettings s, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        var key = KeyOf(s, name) ?? name;
        if (CharacterFields.PipOn(s, key)) return null;
        CharacterFields.SetPip(s, key, true);
        return key;
    }

    /// <summary>Old OnSecThumbRemove: the entry goes, with its place and size.</summary>
    public static bool Remove(AppSettings s, string key) => s.SecondaryThumbnails.Remove(key);
}

/// <summary>Thumbnails › Visibility › Second thumbnails (spec §5): one row per character with On, opacity
/// (%), › to the character page and Remove; Add… below. On/off create or close the second thumbnail at
/// once; ApplyOpacityToAll (ApplyScope.Opacity) applies its opacity, as the old slider did.</summary>
public sealed class PipEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private const double MinPercent = 8, MaxPercent = 100;   // the old slider's 20–255 bytes

    private readonly SettingsContext _ctx;
    private readonly EditorTable _table = new();

    public PipEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        var stack = new StackPanel();
        stack.Children.Add(_table);
        var add = EditorCard.Button("L.S.Pip.Add", Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(add);
        Content = EditorCard.Build("thumbs.visibility.pipTable", null, stack);
        Refresh();
    }

    /// <summary>The characters listed, in order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    private static string Str(string key) => SettingsContext.Str(key);

    public void Refresh()
    {
        Names = SecondThumbnails.Names(_ctx.S);
        _table.Reset(new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto);
        int header = AddRow(header: true);
        Place(EditorTable.Head("L.S.Characters.ColCharacter"), header, 0);
        Place(EditorTable.Head("L.S.Pip.ColOn"), header, 1);
        Place(EditorTable.Head("L.S.Pip.ColOpacity"), header, 2);
        if (Names.Count == 0)
        {
            Place(EditorCard.Note("L.S.Pip.Empty"), AddRow(), 0, span: 6);
            return;
        }
        foreach (var name in Names) AddCharacter(name);
    }

    private void AddCharacter(string name)
    {
        int r = AddRow();
        Place(EditorCard.Value(name, strong: true), r, 0);
        var on = EditorCard.Toggle(CharacterFields.PipOn(_ctx.S, name), v => SetOn(name, v));
        AutomationProperties.SetName(on, string.Format(Str("L.S.Pip.OnFor"), name));
        Place(on, r, 1);
        var readout = EditorCard.Value("", secondary: true);
        readout.MinWidth = 44;
        // Value before the handler: building a row never writes (a stored 10 shows as 8 %).
        var slider = new Slider
        {
            Minimum = MinPercent, Maximum = MaxPercent, Width = 180, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 10, Value = Math.Clamp(CharacterFields.PipOpacity(_ctx.S, name), MinPercent, MaxPercent),
        };
        AutomationProperties.SetName(slider, string.Format(Str("L.S.Pip.OpacityFor"), name));
        void Show(double v) => readout.Text = ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture) + Str("L.S.Unit.Percent");
        Show(slider.Value);
        slider.ValueChanged += (_, e) => { Show(e.NewValue); SetOpacity(name, e.NewValue); };
        Place(slider, r, 2);
        Place(readout, r, 3);
        Place(EditorCard.Open(string.Format(Str("L.S.Characters.Open"), name), () => _ctx.Host.Navigate(CharacterPage.IdFor(name))), r, 4);
        var remove = EditorCard.Button("L.S.Pip.Remove", () => Remove(name), destructive: true);
        AutomationProperties.SetName(remove, string.Format(Str("L.S.Pip.RemoveFor"), name));
        Place(remove, r, 5);
    }

    private int AddRow(bool header = false) => _table.AddRow(header);

    private void Place(FrameworkElement element, int row, int column, int span = 1)
    {
        element.Margin = new Thickness(0, 4, 12, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        _table.Place(element, row, column, span);
    }

    /// <summary>As the character page: on creates or re-enables the entry and opens the second thumbnail
    /// now (if the client runs); off keeps the entry and closes it.</summary>
    public void SetOn(string name, bool on)
    {
        CharacterFields.SetPip(_ctx.S, name, on);
        if (on) _ctx.Services.Thumbnails?.CreateSecondaryForCharacter(name);
        else _ctx.Services.Thumbnails?.DestroySecondaryForCharacter(name);
        _ctx.Applier.Request(ApplyScope.Thumbnails);
    }

    public void SetOpacity(string name, double percent)
    {
        if (CharacterFields.PipOpacity(_ctx.S, name) == (int)Math.Round(percent)) return;
        CharacterFields.SetPipOpacity(_ctx.S, name, percent);
        _ctx.Applier.Request(ApplyScope.Opacity);   // ApplyOpacityToAll gives each second thumbnail its own opacity
    }

    public void Add()
    {
        var name = _ctx.Host.PickCharacter(Str("L.S.Pip.AddTitle"), CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
        if (name == null) return;
        var key = SecondThumbnails.Add(_ctx.S, name);
        if (key == null) { _ctx.Host.Toast(string.Format(Str("L.S.Pip.AddExists"), name.Trim())); return; }
        _ctx.Services.Thumbnails?.CreateSecondaryForCharacter(key);   // appears now if the client runs, else at its login
        _ctx.Applier.Request(ApplyScope.Thumbnails);
        Refresh();
    }

    /// <summary>Spec §6: asks first. Off keeps the place and size; Remove forgets them.</summary>
    public void Remove(string name)
    {
        if (!_ctx.Host.Confirm(Str("L.S.Pip.RemoveTitle"), string.Format(Str("L.S.Pip.RemoveConfirm"), name))) return;
        SecondThumbnails.Remove(_ctx.S, name);
        _ctx.Services.Thumbnails?.DestroySecondaryForCharacter(name);
        _ctx.Applier.Request(ApplyScope.Thumbnails);
        Refresh();
    }
}
