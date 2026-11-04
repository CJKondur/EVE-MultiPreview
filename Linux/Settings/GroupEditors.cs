using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

internal sealed partial class SettingsEditors
{
    /// <summary>One card per group (name, colour, keys, members) and the row that makes a group:
    /// "clients.groups.editor" (also Home's "home.groupKeys").</summary>
    internal Control GroupList()
    {
        var page = new StackPanel { Spacing = 2 };
        var groups = MergedGroups.Build(S);
        if (groups.Count == 0) page.Children.Add(Ui.Note(T("L.S.clients.groups.empty", "No groups yet.")));
        var known = CharacterDirectory.Known(S, _host.RunningCharacters());
        foreach (var group in groups) page.Children.Add(GroupCard(group, known));

        var name = new TextBox { Width = 240, PlaceholderText = T("L.S.clients.groups.new", "Make a group") };
        var make = new Button { Content = T("L.S.clients.groups.new", "Make a group") };
        var status = LinuxLooks.Paint(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "Fx.CriticalBrush");
        make.Click += (_, _) =>
        {
            if (MergedGroups.New(S, name.Text ?? "") == null)
            {
                status.Text = T("L.Linux.GroupNameUsed", "Type a name no group uses yet.");
                return;
            }
            Save();
            _refresh();
        };
        var makeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        makeRow.Children.Add(name);
        makeRow.Children.Add(make);
        makeRow.Children.Add(status);
        page.Children.Add(makeRow);
        return page;
    }

    /// <summary>"Group to show" of focus mode: the group of the client you are in, or one group by name. The shared
    /// window's "clients.groups.focusGroup".</summary>
    internal Control FocusGroupBox()
    {
        var groups = MergedGroups.Build(S);
        var chosen = new ComboBox { Width = 280 };
        chosen.Items.Add(T("L.S.Opt.FocusActiveGroup", "The group of the client you are in"));
        var names = groups.Select(g => g.Name).Distinct(StringComparer.Ordinal).ToList();
        // A chosen group that no longer exists stays listed, so the box shows what is stored (focus mode says it is gone).
        if (S.FocusModeGroup is { Length: > 0 } stored && !names.Contains(stored)) names.Add(stored);
        foreach (var n in names) chosen.Items.Add(n);
        chosen.SelectedIndex = string.IsNullOrEmpty(S.FocusModeGroup) ? 0 : names.IndexOf(S.FocusModeGroup) + 1;
        chosen.SelectionChanged += (_, _) =>
        {
            if (chosen.SelectedIndex < 0) return;
            S.FocusModeGroup = chosen.SelectedIndex == 0 ? "" : names[chosen.SelectedIndex - 1];
            Save();
        };
        return Ui.Row(T("L.S.clients.groups.focusGroup", "Group to show"), chosen,
            T("L.S.clients.groups.focusGroup.Desc", "The group of the client you are in follows you as you switch clients; a group you choose stays the same."));
    }

    private Control GroupCard(MergedGroup group, IReadOnlyList<string> known)
    {
        var body = new StackPanel { Spacing = 6 };

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var nameBox = new TextBox { Text = group.Name, FontWeight = FontWeight.SemiBold, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        void Rename()
        {
            var wanted = nameBox.Text?.Trim() ?? "";
            if (wanted == group.Name) return;
            if (MergedGroups.Rename(S, group, wanted)) { Save(); _host.HotkeysChanged(); }
            else nameBox.Text = group.Name;
        }
        nameBox.LostFocus += (_, _) => Rename();
        nameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Rename(); };
        title.Children.Add(nameBox);
        var delete = new Button { Content = T("L.Common.DeleteWord", "Delete") };
        delete.Click += (_, _) =>
        {
            MergedGroups.Delete(S, group, everywhere: true);
            Save();
            _host.HotkeysChanged();
            _refresh();
        };
        Grid.SetColumn(delete, 1);
        title.Children.Add(delete);
        body.Children.Add(title);

        body.Children.Add(Ui.Row(T("L.S.Events.ColColour", "Colour"), Ui.Colour(group.Colour?.Color ?? "", v => { MergedGroups.SetColor(S, group, v); Save(); })));
        body.Children.Add(Ui.Row(T("L.Linux.GroupNext", "Next key"), HotkeyField("group:" + group.Name + ":next", group.NextKey, key => GroupKey(group, true, key))));
        body.Children.Add(Ui.Row(T("L.Linux.GroupPrevious", "Previous key"), HotkeyField("group:" + group.Name + ":previous", group.PreviousKey, key => GroupKey(group, false, key))));

        var members = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var member in known.Concat(group.Members).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tick = Ui.Check(member, group.Members.Contains(member, StringComparer.OrdinalIgnoreCase), on =>
            {
                if (on) MergedGroups.AddMember(group, member);
                else MergedGroups.RemoveMember(group, member);
                Save();
                _host.HotkeysChanged();
            });
            tick.Margin = new Thickness(0, 0, 14, 0);
            members.Children.Add(tick);
        }
        body.Children.Add(Ui.Row(T("L.Linux.GroupMembers", "Members"), members));
        return Ui.Card(body);
    }

    private void GroupKey(MergedGroup group, bool next, string key)
    {
        if (!MergedGroups.SetKey(S, group, next, key)) _refresh();   // another group of that name owns the key half: show what is stored
    }
}
