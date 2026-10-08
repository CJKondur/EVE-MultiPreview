using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Clients › Characters table (spec §5): Character, System, Hotkey, Group, Show, Cycle and ›,
/// with "Add all running clients" and "Add a character…" under it. Follows clients starting and
/// closing while it is on screen.</summary>
public sealed class CharactersEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private static readonly string[] Headers =
    {
        "L.S.Characters.ColCharacter", "L.S.Characters.ColSystem", "L.S.Characters.ColHotkey",
        "L.S.Characters.ColGroup", "L.S.Characters.ColShow", "L.S.Characters.ColCycle",
    };

    private readonly SettingsContext _ctx;
    private readonly Grid _table = new();
    private string _signature = "";

    public CharactersEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        var stack = new StackPanel();
        stack.Children.Add(_table);
        var note = EditorCard.Note("L.S.Characters.CycleNote");
        note.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(note);
        var buttons = EditorCard.Row(
            EditorCard.Button("L.S.Running.AddAll", AddRunning),
            EditorCard.Button("L.S.Characters.Add", AddOne));
        buttons.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(buttons);
        Content = EditorCard.Frame(stack);
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Refresh(); });
        Refresh();
    }

    /// <summary>The rows shown, in order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

    private static string Str(string key) => SettingsContext.Str(key);

    private string Signature()
    {
        var running = CharacterDirectory.Running(_ctx);
        return string.Join("\n", CharacterDirectory.Known(_ctx.S, running).Select(n => n + "|" + CharacterStores.SystemOf(_ctx, n)))
               + "\n" + string.Join(",", running);
    }

    public void Refresh()
    {
        var running = CharacterDirectory.Running(_ctx);
        Names = CharacterDirectory.Known(_ctx.S, running);
        _signature = Signature();
        Build(running);
    }

    private void Build(IReadOnlyList<string> running)
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.ColumnDefinitions.Clear();
        foreach (var width in new[]
                 {
                     new GridLength(2, GridUnitType.Star), new GridLength(1.4, GridUnitType.Star), GridLength.Auto,
                     new GridLength(1.4, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto,
                 })
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var header = AddRow();
        for (int c = 0; c < Headers.Length; c++) Place(EditorCard.Note(Headers[c], strong: true), header, c);

        if (Names.Count == 0)
        {
            var empty = EditorCard.Note("L.S.Characters.Empty");
            Place(empty, AddRow(), 0);
            Grid.SetColumnSpan(empty, 7);
            return;
        }

        foreach (var name in Names)
        {
            var n = name;
            int r = AddRow();
            bool isRunning = running.Contains(n, StringComparer.OrdinalIgnoreCase);
            Place(EditorCard.Value(n, strong: true), r, 0);
            Place(EditorCard.Value(CharacterStores.SystemOf(_ctx, n) ?? (isRunning ? "" : Str("L.S.Characters.Offline")), secondary: true), r, 1);
            Place(EditorCard.HotkeyButton(CharacterStores.HotkeyOf(_ctx.S, n), () => { if (CharacterStores.CaptureHotkey(_ctx, n)) Refresh(); }), r, 2);
            var groups = CharacterStores.GroupsOf(_ctx.S, n);
            Place(EditorCard.Value(groups.Count == 0 ? "—" : string.Join(", ", groups), secondary: true), r, 3);
            Place(EditorCard.Toggle(CharacterStores.IsShown(_ctx.S, n), on => CharacterStores.SetShown(_ctx, n, on)), r, 4);
            var inCycle = CharacterStores.IsInCycle(_ctx, n);
            var cycle = EditorCard.Toggle(inCycle ?? true, on => CharacterStores.SetInCycle(_ctx, n, on));
            cycle.IsEnabled = inCycle != null;
            Place(cycle, r, 5);
            Place(EditorCard.Open(string.Format(Str("L.S.Characters.Open"), n), () => _ctx.Host.Navigate(CharacterPage.IdFor(n))), r, 6);
        }
    }

    private int AddRow()
    {
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _table.RowDefinitions.Count - 1;
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 4, 12, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _table.Children.Add(element);
    }

    private void AddRunning()
    {
        var running = CharacterDirectory.Running(_ctx);
        if (running.Count == 0) { _ctx.Host.Toast(Str("L.S.Running.None")); return; }
        var added = CharacterStores.AddRunning(_ctx.S, running);
        if (added == 0) { _ctx.Host.Toast(Str("L.S.Running.AllThere")); return; }
        _ctx.Applier.Request(ApplyScope.Save);
        _ctx.Host.Toast(string.Format(Str("L.S.Running.Added"), added));
        Refresh();
    }

    private void AddOne()
    {
        var name = _ctx.Host.PickCharacter(Str("L.S.Characters.AddTitle"), CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
        if (name == null) return;
        if (!CharacterStores.Add(_ctx.S, name))
        {
            _ctx.Host.Toast(string.Format(Str("L.S.Characters.AddExists"), name.Trim()));
            return;
        }
        _ctx.Applier.Request(ApplyScope.Save);
        Refresh();
    }
}
