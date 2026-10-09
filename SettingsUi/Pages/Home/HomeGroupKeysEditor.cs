using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Orientation = System.Windows.Controls.Orientation;

namespace EveMultiPreview.SettingsUi;

/// <summary>Home › Switching: one row per hotkey group of the current profile with its Next and
/// Previous keys, edited exactly as on Clients › Groups (GroupKeys.Capture). Rows follow profile
/// switches and Groups-page edits; building writes nothing.</summary>
public sealed class HomeGroupKeysEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly StackPanel _rows = new();
    private string _signature = "";

    public HomeGroupKeysEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        Content = _rows;
        LiveRefresh.Attach(this, () => { if (Signature(Build()) != _signature) Refresh(); });
        Refresh();
    }

    /// <summary>The groups shown (those with a key half), in Groups-page order.</summary>
    public IReadOnlyList<MergedGroup> Groups { get; private set; } = Array.Empty<MergedGroup>();

    private IReadOnlyList<MergedGroup> Build() => MergedGroups.Build(_ctx.S).Where(g => g.Keys != null).ToList();

    private string Signature(IReadOnlyList<MergedGroup> groups) =>
        _ctx.S.LastUsedProfile + "\n" + string.Join("\n", groups.Select(g => $"{g.Name}|{g.NextKey}|{g.PreviousKey}|{g.Members.Count}"));

    public void Refresh()
    {
        Groups = Build();
        _signature = Signature(Groups);
        _rows.Children.Clear();
        foreach (var g in Groups) _rows.Children.Add(Row(g));
    }

    /// <summary>Public for tests: the click handler of a row's key button.</summary>
    public void Capture(MergedGroup g, bool next)
    {
        if (GroupKeys.Capture(_ctx, g, next)) Refresh();
    }

    private UIElement Row(MergedGroup g)
    {
        var stack = new StackPanel();
        stack.Children.Add(EditorCard.Value(string.Format(SettingsContext.Str("L.S.Home.GroupRow"), g.Name, g.Members.Count), strong: true));
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        keys.Children.Add(KeyField("L.S.Groups.Next", g, next: true));
        keys.Children.Add(KeyField("L.S.Groups.Previous", g, next: false));
        stack.Children.Add(keys);
        var card = EditorCard.Frame(stack);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private UIElement KeyField(string labelKey, MergedGroup g, bool next)
    {
        var button = EditorCard.HotkeyButton(next ? g.NextKey : g.PreviousKey, () => Capture(g, next));
        AutomationProperties.SetName(button, EditorCard.ForName(labelKey, g.Name));
        return EditorCard.Field(labelKey, button);
    }
}
