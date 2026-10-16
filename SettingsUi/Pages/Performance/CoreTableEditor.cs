using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.SettingsUi;

/// <summary>Performance › CPU › Core for each character (old LvPerClientCores): every known character, not just the
/// running ones, with the character page's core choices and › to its page. Read by the affinity pass every tick
/// while management is on: a change only saves.</summary>
public sealed class CoreTableEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly EditorTable _table = new();
    private string _signature = "";

    public CoreTableEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Content = EditorCard.Build("perf.cpu.coreTable", null, _table);
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Refresh(); });
        Refresh();
    }

    /// <summary>The characters listed, in order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    private static string Str(string key) => SettingsContext.Str(key);

    private IReadOnlyList<string> Current() => CharacterDirectory.Known(_ctx.S, CharacterDirectory.Running(_ctx));

    private string Signature() => string.Join("\n", Current().Select(n => n + "|" + (CharacterFields.StalePin(_ctx.S, n) ?? CharacterFields.Core(_ctx.S, n))));

    public void Refresh()
    {
        Names = Current();
        _signature = Signature();
        _table.Reset(new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto);
        int header = AddRow(header: true);
        Place(EditorTable.Head("L.S.Characters.ColCharacter"), header, 0);
        Place(EditorTable.Head("L.S.CoreTable.ColCore"), header, 1);
        if (Names.Count == 0)
        {
            Place(EditorCard.Note("L.S.CoreTable.Empty"), AddRow(), 0, span: 3);
            return;
        }
        foreach (var name in Names)
        {
            int r = AddRow();
            Place(EditorCard.Value(name, strong: true), r, 0);
            Place(CoreBox(name), r, 1);
            Place(EditorCard.Open(string.Format(Str("L.S.Characters.Open"), name),
                () => _ctx.Host.Navigate(CharacterPage.IdFor(name), "character.core")), r, 2);
        }
    }

    /// <summary>The selection is made before the handler: building a row never writes. A pin to a core this PC doesn't
    /// have is listed as its own entry and selected, so choosing Automatic clears it.</summary>
    private ComboBox CoreBox(string name)
    {
        var box = new ComboBox { MinWidth = 160 };
        var options = CharacterFields.CoreOptions();
        foreach (var o in options) box.Items.Add(new ComboBoxItem { Content = o.Literal ?? Str(o.LabelKey), Tag = o.Value });
        if (CharacterFields.StalePin(_ctx.S, name) is { } stale)
        {
            box.Items.Add(new ComboBoxItem { Content = string.Format(Str("L.S.CoreTable.Stale"), stale), Tag = "stale" });
            box.SelectedIndex = box.Items.Count - 1;
        }
        else
        {
            var current = CharacterFields.Core(_ctx.S, name);
            box.SelectedIndex = options.ToList().FindIndex(o => Equals(o.Value, current));
        }
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: int core }) SetCore(name, core); };
        AutomationProperties.SetName(box, string.Format(Str("L.S.CoreTable.CoreFor"), name));
        return box;
    }

    /// <summary>A core for this character's client while it's in the background; Automatic removes the pin.</summary>
    public void SetCore(string name, int core)
    {
        var stale = CharacterFields.StalePin(_ctx.S, name) != null;
        if (!stale && CharacterFields.Core(_ctx.S, name) == core && (core >= 0 || !_ctx.S.PerClientCores.ContainsKey(name))) return;
        CharacterFields.SetCore(_ctx.S, name, core);
        _ctx.Applier.Request(ApplyScope.Save);
        _signature = Signature();
        // The "not on this PC" entry goes once the pin is replaced; rebuild after the combo box has finished its event.
        if (stale) Dispatcher.BeginInvoke(new Action(Refresh));
    }

    private int AddRow(bool header = false) => _table.AddRow(header);

    private void Place(FrameworkElement element, int row, int column, int span = 1)
    {
        element.Margin = new Thickness(0, 4, 16, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        _table.Place(element, row, column, span);
    }
}
