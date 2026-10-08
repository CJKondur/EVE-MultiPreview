using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Clients › Groups (spec §5, §8.3): "Make a group", then one card per merged group with
/// name, colour, next and previous keys, members in switching order (drag to reorder), Add, Add all
/// running clients and Delete. Every write goes through MergedGroups; building writes nothing.</summary>
public sealed class GroupsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly StackPanel _cards = new();

    public GroupsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        var root = new StackPanel();
        root.Children.Add(EditorCard.Build("clients.groups.new", EditorCard.Button("L.S.Act.clients.groups.new", NewGroup)));
        root.Children.Add(_cards);
        Content = root;
        Refresh();
    }

    /// <summary>The groups shown, in order.</summary>
    public IReadOnlyList<MergedGroup> Groups { get; private set; } = Array.Empty<MergedGroup>();

    public void Refresh()
    {
        Groups = MergedGroups.Build(_ctx.S);
        _cards.Children.Clear();
        if (Groups.Count == 0) _cards.Children.Add(EditorCard.Frame(EditorCard.Note("L.S.clients.groups.empty")));
        foreach (var g in Groups) _cards.Children.Add(BuildCard(g));
    }

    /// <summary>Delete with the spec §8.3 question. Public for tests.</summary>
    public void Delete(MergedGroup g)
    {
        bool? everywhere;
        if (g.Colour != null && g.Keys != null)
            everywhere = _ctx.Host.ConfirmChoice(Str("L.S.Groups.DeleteTitle"), string.Format(Str("L.S.Groups.DeleteBoth"), g.Name));
        else
            everywhere = _ctx.Host.Confirm(Str("L.S.Groups.DeleteTitle"),
                string.Format(Str(g.Colour != null ? "L.S.Groups.DeleteColour" : "L.S.Groups.DeleteKeys"), g.Name))
                ? g.Colour != null
                : null;
        if (everywhere == null) return;
        MergedGroups.Delete(_ctx.S, g, everywhere.Value);
        Changed(ApplyScope.Thumbnails);
    }

    private static string Str(string key) => SettingsContext.Str(key);

    private void Changed(ApplyScope scope)
    {
        _ctx.Applier.Request(scope);
        Refresh();
    }

    private void NewGroup()
    {
        var name = _ctx.Host.AskText(Str("L.S.Act.clients.groups.new"), Str("L.S.Groups.NamePrompt"), "");
        if (name == null) return;
        if (MergedGroups.New(_ctx.S, name) == null)
        {
            _ctx.Host.Toast(string.Format(Str("L.S.Groups.NameTaken"), name.Trim()));
            return;
        }
        Changed(ApplyScope.Save);
    }

    private FrameworkElement BuildCard(MergedGroup g)
    {
        var stack = new StackPanel();

        // Name and Delete.
        var name = new TextBox { Text = g.Name, MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(name, Str("L.S.Groups.NameBox"));
        void CommitName()
        {
            if (name.Text.Trim() == g.Name) return;
            if (!MergedGroups.Rename(_ctx.S, g, name.Text))
            {
                _ctx.Host.Toast(string.Format(Str("L.S.Groups.NameTaken"), name.Text.Trim()));
                name.Text = g.Name;
                return;
            }
            name.Text = g.Name;   // in place: a rebuild would eat the click that moved focus
            _ctx.Applier.Request(ApplyScope.Thumbnails);
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var delete = EditorCard.Button("L.S.Groups.Delete", () => Delete(g), destructive: true);
        DockPanel.SetDock(delete, Dock.Right);
        head.Children.Add(delete);
        head.Children.Add(name);
        stack.Children.Add(head);

        // Colour and keys.
        var settings = new WrapPanel();
        UIElement colour = g.Colour == null
            ? EditorCard.Button("L.S.Groups.AddColour", () => PickColour(g))
            : EditorCard.Swatch(g.Colour.Color, () => PickColour(g));
        settings.Children.Add(EditorCard.Field("L.S.Groups.Colour", colour));
        settings.Children.Add(EditorCard.Field("L.S.Groups.Next", EditorCard.HotkeyButton(g.NextKey, () => CaptureKey(g, next: true))));
        settings.Children.Add(EditorCard.Field("L.S.Groups.Previous", EditorCard.HotkeyButton(g.PreviousKey, () => CaptureKey(g, next: false))));
        stack.Children.Add(settings);

        // Members in switching order: drag a chip onto another to move it there, or onto empty space to move it last.
        stack.Children.Add(EditorCard.Note("L.S.Groups.Members"));
        var members = new WrapPanel { AllowDrop = true, Background = Brushes.Transparent, Margin = new Thickness(0, 6, 0, 0) };
        int n = 0;
        foreach (var m in g.Members)
        {
            var member = m;
            var chip = EditorCard.Chip($"{++n}  {member}", () => { MergedGroups.RemoveMember(g, member); Changed(ApplyScope.Thumbnails); });
            chip.AllowDrop = true;
            Point? down = null;
            chip.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(chip);
            chip.PreviewMouseLeftButtonUp += (_, _) => down = null;
            chip.PreviewMouseMove += (_, e) =>
            {
                if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(chip);
                if (Math.Abs(p.X - down.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(p.Y - down.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                down = null;
                DragDrop.DoDragDrop(chip, member, DragDropEffects.Move);
            };
            chip.Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.StringFormat) is not string dragged) return;
                e.Handled = true;
                MergedGroups.MoveMember(g, dragged, member);
                Changed(ApplyScope.Thumbnails);
            };
            members.Children.Add(chip);
        }
        members.Drop += (_, e) =>
        {
            if (e.Handled || e.Data.GetData(DataFormats.StringFormat) is not string dragged) return;
            MergedGroups.MoveMember(g, dragged, null);
            Changed(ApplyScope.Thumbnails);
        };
        members.Children.Add(EditorCard.Button("L.S.Groups.AddMember", () => AddMember(g)));
        var addRunning = EditorCard.Button("L.S.Running.AddAll", () => AddRunning(g));
        addRunning.Margin = new Thickness(6, 0, 0, 6);
        members.Children.Add(addRunning);
        stack.Children.Add(members);

        var scope = EditorCard.Note("L.S.Groups.Scope");
        scope.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(scope);
        return EditorCard.Frame(stack);
    }

    private void PickColour(MergedGroup g)
    {
        var picked = _ctx.Host.PickColor(g.Colour?.Color ?? new ThumbnailGroup().Color);
        if (picked == null) return;
        MergedGroups.SetColor(_ctx.S, g, picked);
        Changed(ApplyScope.Thumbnails);
    }

    private void CaptureKey(MergedGroup g, bool next)
    {
        var id = next ? HotkeyUses.GroupNextId(g.Name) : HotkeyUses.GroupPreviousId(g.Name);
        var key = _ctx.Host.CaptureHotkey(id, next ? g.NextKey : g.PreviousKey);
        if (key == null) return;
        if (!MergedGroups.SetKey(_ctx.S, g, next, key))
        {
            _ctx.Host.Toast(string.Format(Str("L.S.Groups.DuplicateName"), g.Name));
            return;
        }
        Changed(ApplyScope.Save);
    }

    private void AddMember(MergedGroup g)
    {
        var name = _ctx.Host.PickCharacter(Str("L.S.Groups.AddMemberTitle"),
            CharacterDirectory.AllKnown(_ctx.S, CharacterDirectory.Running(_ctx)));
        if (name == null || !MergedGroups.AddMember(g, name)) return;
        Changed(ApplyScope.Thumbnails);
    }

    private void AddRunning(MergedGroup g)
    {
        var running = CharacterDirectory.Running(_ctx);
        if (running.Count == 0) { _ctx.Host.Toast(Str("L.S.Running.None")); return; }
        var added = running.Count(name => MergedGroups.AddMember(g, name));
        if (added == 0) { _ctx.Host.Toast(Str("L.S.Running.AllThere")); return; }
        _ctx.Host.Toast(string.Format(Str("L.S.Running.Added"), added));
        Changed(ApplyScope.Thumbnails);
    }
}
