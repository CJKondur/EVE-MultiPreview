using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Orientation = System.Windows.Controls.Orientation;

namespace EveMultiPreview.SettingsUi;

/// <summary>Home › Your clients (spec §5): one tile per running character with its system, its
/// hotkey (click to record), a thumbnail switch and › to its page. Empty state with no client.</summary>
public sealed class HomeTilesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly WrapPanel _tiles = new();
    private string _signature = "";

    public HomeTilesEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Content = _tiles;
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Refresh(); });
        Refresh();
    }

    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    private string Signature() =>
        string.Join("\n", CharacterDirectory.Running(_ctx).Select(n =>
            n + "|" + CharacterStores.SystemOf(_ctx, n) + "|" + CharacterStores.IsShown(_ctx.S, n) + "|" + CharacterStores.HotkeyOf(_ctx.S, n)));

    public void Refresh()
    {
        Names = CharacterDirectory.Running(_ctx);
        _signature = Signature();
        _tiles.Children.Clear();
        if (Names.Count == 0)
        {
            var empty = EditorCard.Build("home.empty", null);
            empty.MinWidth = 420;
            _tiles.Children.Add(empty);
            return;
        }
        foreach (var name in Names) _tiles.Children.Add(Tile(name));
    }

    private UIElement Tile(string name)
    {
        var head = new DockPanel();
        var open = EditorCard.Open(string.Format(SettingsContext.Str("L.S.Characters.Open"), name), () => _ctx.Host.Navigate(CharacterPage.IdFor(name)));
        DockPanel.SetDock(open, Dock.Right);
        head.Children.Add(open);
        head.Children.Add(EditorCard.Value(name, strong: true));
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(EditorCard.Value(CharacterStores.SystemOf(_ctx, name) ?? "", secondary: true));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var key = EditorCard.HotkeyButton(CharacterStores.HotkeyOf(_ctx.S, name), () => { if (CharacterStores.CaptureHotkey(_ctx, name)) Refresh(); });
        key.Margin = new Thickness(0, 0, 16, 0);
        controls.Children.Add(key);
        controls.Children.Add(EditorCard.Field("L.S.Home.Show", EditorCard.Toggle(CharacterStores.IsShown(_ctx.S, name), on => CharacterStores.SetShown(_ctx, name, on))));
        stack.Children.Add(controls);
        var tile = EditorCard.Frame(stack);
        tile.Width = 320;
        tile.Margin = new Thickness(0, 0, 8, 8);
        return tile;
    }
}
