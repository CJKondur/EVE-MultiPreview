using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.SettingsUi;

/// <summary>Character page › Stats overlay › Choose numbers (spec §5): every number the overlay can show, each
/// Default, Always show or Never show for this character. Replaces CharacterStatEditorWindow. Each choice writes
/// only its own bit of PerCharacterStats; an entry with nothing forced is removed.</summary>
public sealed class StatPickerEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly string _name;
    private readonly TextBlock _toggleText = new();
    private readonly StackPanel _list = new() { Visibility = Visibility.Collapsed };
    private string _signature = "";

    public StatPickerEditor(SettingsContext ctx, string name)
    {
        _ctx = ctx;
        _name = name;
        var toggle = new Button { Content = _toggleText, Padding = new Thickness(12, 4, 12, 4) };
        toggle.Click += (_, _) => IsOpen = !IsOpen;
        Loc.SetKey(_toggleText, "L.S.StatsPicker.Open");
        Content = EditorCard.Build("character.statsPicker", toggle, _list);
        // The group switches above and Tools › Stats overlay change the same numbers: follow them while shown.
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Rebuild(); }, 1);
        Rebuild();
    }

    /// <summary>Whether the list of numbers is shown ("Choose numbers…" / "Hide the list").</summary>
    public bool IsOpen
    {
        get => _list.Visibility == Visibility.Visible;
        set
        {
            _list.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Loc.SetKey(_toggleText, value ? "L.S.StatsPicker.Close" : "L.S.StatsPicker.Open");
        }
    }

    private string Signature() =>
        $"{(uint)_ctx.S.GlobalStatMetrics}|"
        + (_ctx.S.PerCharacterStats.TryGetValue(_name, out var c) ? $"{(uint)c.ForcedOn}/{(uint)c.ForcedOff}" : "-");

    public void Refresh()
    {
        if (Signature() != _signature) Rebuild();
    }

    /// <summary>0 = default, 1 = always show, 2 = never show.</summary>
    public int StateOf(StatMetrics bit) => StatsFields.Override(_ctx.S, _name, bit) switch { true => 1, false => 2, null => 0 };

    private void Rebuild()
    {
        _signature = Signature();
        _list.Children.Clear();
        foreach (var (group, labelKey, _) in StatsFields.Groups)
        {
            var title = EditorCard.Note(labelKey, strong: true);
            title.Margin = new Thickness(0, 8, 0, 4);
            _list.Children.Add(title);
            foreach (var m in StatsFields.Metrics.Where(x => x.Group == group)) _list.Children.Add(Row(m));
        }
        var reset = EditorCard.Button("L.S.StatsPicker.Reset", Reset);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 12, 0, 0);
        _list.Children.Add(reset);
    }

    /// <summary>The selection is made before the handler: building a row never writes.</summary>
    private UIElement Row(StatMetricInfo m)
    {
        var bit = m.Bit;
        var defaultKey = StatsFields.IsOn(_ctx.S.GlobalStatMetrics, bit) ? "L.S.StatsPicker.DefaultShown" : "L.S.StatsPicker.DefaultHidden";
        var combo = new ComboBox { MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in new[] { defaultKey, "L.S.StatsPicker.Always", "L.S.StatsPicker.Never" })
        {
            var item = new ComboBoxItem();
            Loc.SetKey(item, key);
            combo.Items.Add(item);
        }
        combo.SelectedIndex = StateOf(bit);
        combo.SelectionChanged += (_, _) => Set(bit, combo.SelectedIndex);
        Loc.SetNameKey(combo, m.LabelKey);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        label.SetResourceReference(StyleProperty, "Fx.Label");
        Loc.SetKey(label, m.LabelKey);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(combo, Dock.Right);
        row.Children.Add(combo);
        row.Children.Add(label);
        return row;
    }

    /// <summary>One number for this character: 0 = default, 1 = always show, 2 = never show.</summary>
    public void Set(StatMetrics bit, int choice)
    {
        bool? state = choice switch { 1 => true, 2 => false, _ => null };
        if (StatsFields.Override(_ctx.S, _name, bit) == state) return;
        StatsFields.SetOverride(_ctx.S, _name, bit, state);
        Changed();
    }

    /// <summary>The old editor's Reset all: every number back to the default (the entry goes).</summary>
    public void Reset()
    {
        if (!_ctx.S.PerCharacterStats.ContainsKey(_name)) return;
        if (!_ctx.Host.Confirm(SettingsContext.Str("L.S.StatsPicker.Reset"), SettingsContext.Str("L.S.StatsPicker.ResetConfirm"))) return;
        StatsFields.ClearOverrides(_ctx.S, _name);
        Changed();
        Rebuild();
    }

    private void Changed()
    {
        // A re-apply opens or closes this character's stats window when its numbers change, as the group switches do.
        _ctx.Applier.Request(ApplyScope.Thumbnails);
        _signature = Signature();   // our own write: no redraw under the player's mouse
        _ctx.Host.Reload();          // the group switches above show the new state
    }
}
