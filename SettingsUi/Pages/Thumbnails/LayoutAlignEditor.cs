using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Thumbnails › Layout &amp; screens › Align, distribute and nudge (IDEAS #36). A thumbnail never takes the keyboard
/// (WS_EX_NOACTIVATE) and the wall has no selection, so the selection is a tick list of the characters that are logged in,
/// and the buttons work on the ticked ones: align an edge, space evenly, make the same size, nudge 1 or 10 px. Each button
/// is one ThumbnailManager.ApplyAlign (one Undo step, places saved). Aligning and matching need 2 ticked, spacing evenly 3,
/// nudging 1; the other buttons are greyed out. The first character ticked is the one Match width/height copies.</summary>
public sealed class LayoutAlignEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private static readonly (AlignOp Op, string Key)[] Operations =
    {
        (AlignOp.Left, "L.S.Act.thumbs.layout.alignLeft"), (AlignOp.Right, "L.S.Act.thumbs.layout.alignRight"),
        (AlignOp.Top, "L.S.Act.thumbs.layout.alignTop"), (AlignOp.Bottom, "L.S.Act.thumbs.layout.alignBottom"),
        (AlignOp.DistributeH, "L.S.Act.thumbs.layout.distributeH"), (AlignOp.DistributeV, "L.S.Act.thumbs.layout.distributeV"),
        (AlignOp.MatchWidth, "L.S.Act.thumbs.layout.matchWidth"), (AlignOp.MatchHeight, "L.S.Act.thumbs.layout.matchHeight"),
    };

    /// <summary>The arrow buttons of a row: glyph, the screen-reader text, and the direction.</summary>
    private static readonly (string Glyph, string Key, int Dx, int Dy)[] Arrows =
    {
        ("←", "L.S.Align.NudgeLeft", -1, 0), ("→", "L.S.Align.NudgeRight", 1, 0),
        ("↑", "L.S.Align.NudgeUp", 0, -1), ("↓", "L.S.Align.NudgeDown", 0, 1),
    };

    /// <summary>The two distances of the nudge rows.</summary>
    public static readonly int[] NudgeSteps = { 1, 10 };

    private readonly SettingsContext _ctx;
    private readonly List<string> _ticked = new();                 // in the order they were ticked
    private readonly List<string> _listed = new();
    private readonly WrapPanel _list = new();
    private readonly TextBlock _none = EditorCard.Note("L.S.Align.None");
    private readonly TextBlock _count = EditorCard.Value("");
    private readonly Button _all;
    private readonly Button _clear;
    private readonly Dictionary<AlignOp, Button> _buttons = new();
    private readonly List<(Button Button, int Dx, int Dy)> _nudges = new();
    private string _signature = "";

    public LayoutAlignEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _all = EditorCard.Button("L.S.Align.All", TickAll);
        _clear = EditorCard.Button("L.S.Align.Clear", ClearTicks);
        var picker = new StackPanel();
        picker.Children.Add(EditorCard.Note("L.S.Align.Pick", strong: true));
        _none.Margin = new Thickness(0, 6, 0, 0);
        picker.Children.Add(_none);
        _list.Margin = new Thickness(0, 6, 0, 6);
        picker.Children.Add(_list);
        picker.Children.Add(EditorCard.Row(_all, _clear, _count));

        var operations = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (op, key) in Operations)
        {
            var captured = op;
            var button = EditorCard.Button(key, () => Run(captured));
            button.Margin = new Thickness(0, 0, 8, 8);
            _buttons[op] = button;
            operations.Children.Add(button);
        }

        var below = new StackPanel();
        below.Children.Add(picker);
        below.Children.Add(operations);
        foreach (var step in NudgeSteps)
        {
            var row = new List<UIElement> { EditorCard.Note(step == 1 ? "L.S.Align.Nudge1" : "L.S.Align.Nudge10") };
            foreach (var (glyph, key, dx, dy) in Arrows)
            {
                var distance = step;
                var button = new Button { Content = glyph, MinWidth = 36, Padding = new Thickness(8, 4, 8, 4) };
                AutomationProperties.SetName(button, string.Format(CultureInfo.CurrentCulture, SettingsContext.Str(key), distance));
                var (stepX, stepY) = (dx * distance, dy * distance);
                button.Click += (_, _) => Run(AlignOp.Nudge, stepX, stepY);
                _nudges.Add((button, stepX, stepY));
                row.Add(button);
            }
            var strip = EditorCard.Row(row.ToArray());
            strip.Margin = new Thickness(0, 0, 0, 8);
            below.Children.Add(strip);
        }
        below.Children.Add(EditorCard.Note("L.S.Align.Hint"));

        Content = EditorCard.Build("thumbs.layout.align", null, below);
        // Characters log in and out while the page is open.
        LiveRefresh.Attach(this, Refresh);
        Refresh();
    }

    /// <summary>The characters shown in the list, sorted.</summary>
    public IReadOnlyList<string> Listed => _listed;

    /// <summary>The ticked characters in the order they were ticked.</summary>
    public IReadOnlyList<string> Ticked => _ticked;

    public Button ButtonFor(AlignOp op) => _buttons[op];

    /// <summary>The arrow buttons, row by row (1 px, then 10 px): left, right, up, down.</summary>
    public IReadOnlyList<Button> NudgeButtons => _nudges.Select(n => n.Button).ToList();

    /// <summary>Whether the buttons of <paramref name="op"/> work with what is ticked now.</summary>
    public bool CanRun(AlignOp op) =>
        _ctx.Services.Thumbnails != null && _ticked.Count >= LayoutAlign.MinItems(op);

    public void Tick(string name, bool on)
    {
        var at = _ticked.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (on && at < 0 && _listed.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) _ticked.Add(name);
        else if (!on && at >= 0) _ticked.RemoveAt(at);
        else return;
        SyncBoxes();
        UpdateButtons();
    }

    public void TickAll()
    {
        foreach (var name in _listed)
            if (!_ticked.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) _ticked.Add(name);
        SyncBoxes();
        UpdateButtons();
    }

    public void ClearTicks()
    {
        _ticked.Clear();
        SyncBoxes();
        UpdateButtons();
    }

    /// <summary>What a button does: one ApplyAlign over the ticked characters. Nothing when the button is greyed out.</summary>
    public void Run(AlignOp op, int dx = 0, int dy = 0)
    {
        if (!CanRun(op)) return;
        _ctx.Services.Thumbnails!.ApplyAlign(op, _ticked.ToList(), dx, dy);
        Refresh();
    }

    public void Refresh()
    {
        var names = CharacterDirectory.Running(_ctx);
        var signature = string.Join("\n", names);
        if (signature != _signature)
        {
            _signature = signature;
            _listed.Clear();
            _listed.AddRange(names);
            _ticked.RemoveAll(t => !_listed.Any(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase)));
            Rebuild();
        }
        UpdateButtons();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        foreach (var name in _listed)
        {
            var who = name;
            var box = new CheckBox { Content = who, Margin = new Thickness(0, 2, 16, 2) };
            box.IsChecked = _ticked.Any(n => string.Equals(n, who, StringComparison.OrdinalIgnoreCase));
            box.Checked += (_, _) => Tick(who, true);
            box.Unchecked += (_, _) => Tick(who, false);
            _list.Children.Add(box);
        }
        _none.Visibility = _listed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Brings the boxes in line with the ticks (Tick all, Untick all). The handlers find nothing to change.</summary>
    private void SyncBoxes()
    {
        foreach (CheckBox box in _list.Children)
            box.IsChecked = _ticked.Any(n => string.Equals(n, (string)box.Content, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateButtons()
    {
        foreach (var (op, button) in _buttons) button.IsEnabled = CanRun(op);
        foreach (var (button, _, _) in _nudges) button.IsEnabled = CanRun(AlignOp.Nudge);
        _all.IsEnabled = _listed.Count > 0;
        _clear.IsEnabled = _ticked.Count > 0;
        _count.Text = string.Format(CultureInfo.CurrentCulture, SettingsContext.Str("L.S.Align.Ticked"), _ticked.Count);
    }
}
