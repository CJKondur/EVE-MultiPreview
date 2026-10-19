using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Clients › Groups › Focus mode › Group to show (IDEAS #35): the group of the client you are in, or one group by
/// its name. The list is built from the groups as this page has them at every refresh, so a new or renamed group is
/// offered at once. A stored name no group has any more stays in the list, so the choice can be seen and changed. Writes
/// only FocusModeGroup.</summary>
public sealed class FocusGroupEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "clients.groups.focusGroup";
    private readonly SettingsContext _ctx;
    private bool _loading;

    public FocusGroupEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        List.SelectionChanged += (_, _) =>
        {
            if (_loading || List.SelectedIndex < 0) return;
            var chosen = List.SelectedIndex == 0 ? "" : Names[List.SelectedIndex - 1];
            if (chosen == (_ctx.S.FocusModeGroup ?? "")) return;
            _ctx.S.FocusModeGroup = chosen;
            _ctx.Applier.Request(ApplyScope.Save);   // App reads it on its half-second timer
        };
        Content = EditorCard.Build(ItemId, List);
        Refresh();
    }

    public ComboBox List { get; } = new() { MinWidth = 220 };

    /// <summary>The group names after the first entry ("the group of the client you are in").</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    public void Refresh()
    {
        _loading = true;
        var names = MergedGroups.Build(_ctx.S).Select(g => g.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var stored = _ctx.S.FocusModeGroup ?? "";
        if (stored.Length > 0 && !names.Contains(stored, StringComparer.Ordinal)) names.Add(stored);
        Names = names;
        List.Items.Clear();
        List.Items.Add(SettingsContext.Str("L.S.Opt.FocusActiveGroup"));
        foreach (var name in names) List.Items.Add(name);
        List.SelectedIndex = stored.Length == 0 ? 0 : names.IndexOf(stored) + 1;
        _loading = false;
    }

    /// <summary>Picks an entry as a click would (0 = the group of the client you are in).</summary>
    public void Choose(int index) => List.SelectedIndex = index;
}
