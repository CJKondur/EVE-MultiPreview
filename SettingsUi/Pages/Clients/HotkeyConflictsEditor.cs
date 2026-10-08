using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;

namespace EveMultiPreview.SettingsUi;

/// <summary>Clients › Hotkeys &amp; switching › Conflict check: recalculated every second while
/// shown; each use opens the card that sets it.</summary>
public sealed class HotkeyConflictsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new();
    private string _signature = "";

    public HotkeyConflictsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _summary.SetResourceReference(StyleProperty, "Fx.Label");
        var below = new StackPanel();
        below.Children.Add(_summary);
        below.Children.Add(_rows);
        Content = EditorCard.Build("clients.hotkeys.conflicts", null, below);
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Refresh(); }, seconds: 1);
        Refresh();
    }

    public IReadOnlyList<HotkeyClash> Clashes { get; private set; } = Array.Empty<HotkeyClash>();

    private IReadOnlyList<HotkeyUse> Uses() => HotkeyUses.All(_ctx.S, SettingsRegistry.Settings).ToList();

    private string Signature() =>
        _ctx.S.HotkeyWildcardModifiers + "|" + string.Join("\n", Uses().Select(u => u.Id + "=" + u.Hotkey));

    public void Refresh()
    {
        var uses = Uses();
        _signature = Signature();
        Clashes = HotkeyConflicts.Find(uses, _ctx.S.HotkeyWildcardModifiers);
        _summary.Text = Clashes.Count == 0
            ? SettingsContext.Str("L.S.Conflicts.None")
            : string.Format(SettingsContext.Str("L.S.Conflicts.Count"), Clashes.Count);
        _rows.Children.Clear();
        foreach (var clash in Clashes)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var key = EditorCard.Value(clash.KeyText);
            key.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.MonoFont");
            key.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(key);
            row.Children.Add(Link(clash.First));
            row.Children.Add(Link(clash.Second));
            _rows.Children.Add(row);
        }
    }

    private UIElement Link(HotkeyUse use)
    {
        var target = HotkeyConflicts.Locate(use.Id, SettingsRegistry.Pages);
        var button = new Button { Content = use.Label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), IsEnabled = target != null };
        button.Click += (_, _) => { if (target is { } t) _ctx.Host.Navigate(t.PageId, t.ItemId); };
        return button;
    }
}
