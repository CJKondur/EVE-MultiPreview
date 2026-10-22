using System;
using System.Collections.Generic;
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

/// <summary>Alerts › Sounds › Sounds for one group (IDEAS #58): pick a group of Clients › Groups, then give it a sound
/// for every event, or for one event. The sounds are one line of text in one settings key (GroupSoundChoices); App
/// hands them to GroupSounds when the change is applied, and App.PlayAlertSound asks it at every alert, so a change
/// only saves.
///
/// A player reads the card to find out which tone an alert will have. So it says who the group's members are and,
/// under the rows, each member another group on the Groups page reaches first with a sound of its own. The rule is
/// GroupSounds', and the card asks it: it works out no answer of its own.</summary>
public sealed class GroupSoundsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.sounds.groups";

    private readonly SettingsContext _ctx;
    private readonly ComboBox _groups = new() { MinWidth = 220 };
    private readonly StackPanel _empty = new();
    private readonly StackPanel _shown = new();
    private readonly EditorTable _table = new();
    private readonly TextBlock _members = EditorCard.Value("", secondary: true);
    private readonly StackPanel _overlaps = new();
    private readonly List<string> _keys = new();
    private string _signature = "";
    private bool _building;

    public GroupSoundsEditor(SettingsContext ctx)
    {
        _ctx = ctx;

        // No group yet: the card says so, and offers the way to the page where one is made.
        var none = EditorCard.Note("L.S.GroupSounds.NoGroups");
        none.TextWrapping = TextWrapping.Wrap;
        var toGroups = EditorCard.Button("L.S.GroupAlerts.OpenGroups", () => _ctx.Host.Navigate(ClientsPages.GroupsId));
        toGroups.HorizontalAlignment = HorizontalAlignment.Left;
        toGroups.Margin = new Thickness(0, 8, 0, 0);
        _empty.Children.Add(none);
        _empty.Children.Add(toGroups);

        EditorCard.ClipNames(_groups);   // IDEAS P88: a long group name ends in an ellipsis, its tooltip has the whole name
        AutomationProperties.SetName(_groups, Str("L.S.GroupSounds.Group"));
        _groups.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            Selected = _groups.SelectedItem as string;
            Show();
        };
        var picker = EditorCard.Field("L.S.GroupSounds.Group", _groups);
        _members.Margin = new Thickness(0, 0, 0, 6);
        _members.TextWrapping = TextWrapping.Wrap;
        _shown.Children.Add(picker);
        _shown.Children.Add(_members);
        _shown.Children.Add(_table);
        _shown.Children.Add(_overlaps);

        var stack = new StackPanel();
        stack.Children.Add(_empty);
        stack.Children.Add(_shown);
        Content = EditorCard.Build(ItemId, null, stack);
        // Groups and rules are made on other pages while this one stays open in the background: the card follows
        // them while it is shown.
        LiveRefresh.Attach(this, Look);
        Refresh();
    }

    private static string Str(string key) => SettingsContext.Str(key);

    /// <summary>The groups the card offers: the Groups page's, in its order, each name once.</summary>
    public IReadOnlyList<string> Groups { get; private set; } = Array.Empty<string>();

    /// <summary>The group whose sounds are shown, or null when there is no group.</summary>
    public string? Selected { get; private set; }

    /// <summary>The event keys of the rows, in order: <see cref="GroupSoundChoices.EveryEvent"/>, the built-in
    /// events, then the player's own rules that are on.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>The members of the shown group in this profile (of every group of that name, each member once).</summary>
    public IReadOnlyList<string> Members { get; private set; } = Array.Empty<string>();

    /// <summary>The members of the shown group that an earlier group on the Groups page reaches with a sound of its
    /// own, and that group: what the overlap lines say (outside a window a text is its key). GroupSounds' own answer.</summary>
    public IReadOnlyList<(string Member, string Group)> Overlapping { get; private set; } = Array.Empty<(string, string)>();

    /// <summary>The card says that there is no group yet.</summary>
    public bool SaysNoGroups { get; private set; }

    public void Refresh()
    {
        _building = true;
        try
        {
            Groups = MergedGroups.Build(_ctx.S).Select(g => g.Name).Distinct(StringComparer.Ordinal).ToList();
            if (Selected == null || !Groups.Contains(Selected)) Selected = Groups.FirstOrDefault();
            SaysNoGroups = Groups.Count == 0;
            _empty.Visibility = SaysNoGroups ? Visibility.Visible : Visibility.Collapsed;
            _shown.Visibility = SaysNoGroups ? Visibility.Collapsed : Visibility.Visible;
            _groups.Items.Clear();
            foreach (var name in Groups) _groups.Items.Add(name);
            _groups.SelectedItem = Selected;
        }
        finally { _building = false; }
        Show();
    }

    /// <summary>What the card does every two seconds while it is shown: it is built again only when what it shows is
    /// not what it was, and never while its list is open (it would close under the player's pointer).</summary>
    public void Look()
    {
        if (_groups.IsDropDownOpen || Signature() == _signature) return;
        Refresh();
    }

    /// <summary>Everything the card shows that can change without the card's doing, as one text to compare.</summary>
    private string Signature()
    {
        var text = new System.Text.StringBuilder(_ctx.S.GroupAlertSounds);
        foreach (var rule in Rules()) text.Append('\n').Append(rule.AlertType).Append('\t').Append(rule.Name);
        foreach (var group in MergedGroups.Build(_ctx.S))
            text.Append('\n').Append(group.Name).Append('\t').Append(string.Join("\t", group.Members));
        return text.ToString();
    }

    private IEnumerable<CustomAlertRule> Rules() => _ctx.CustomAlerts.Rules.Where(r => r.Enabled);

    /// <summary>Shows another group's sounds. A name the card does not offer changes nothing.</summary>
    public void Select(string group)
    {
        if (!Groups.Contains(group)) return;
        Selected = group;
        _building = true;
        try { _groups.SelectedItem = group; }
        finally { _building = false; }
        Show();
    }

    /// <summary>The shown group's sound file for an event key (<see cref="GroupSoundChoices.EveryEvent"/> for the
    /// group's sound for every event), or "".</summary>
    private string Own(string eventKey) =>
        Selected != null && GroupSoundChoices.Read(_ctx.S.GroupAlertSounds).TryGetValue(Selected, out var chosen)
        && chosen.TryGetValue(eventKey, out var file) ? file : "";

    private void Show()
    {
        _table.Reset(new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto);
        _keys.Clear();
        _overlaps.Children.Clear();
        if (Selected != null)
        {
            AddRow(GroupSoundChoices.EveryEvent, Str("L.S.GroupSounds.Every"), rule: false);
            foreach (var e in AlertEvents.Listed) AddRow(e.Key, AlertEvents.Name(e.Key), rule: false);
            foreach (var rule in Rules()) AddRow(rule.AlertType, rule.Name, rule: true);
        }

        var named = Selected == null ? new List<MergedGroup>() : MergedGroups.Build(_ctx.S).Where(g => g.Name == Selected).ToList();
        // A member without a name (a file edited by hand) is nobody: not listed.
        Members = named.SelectMany(g => g.Members).Where(member => !string.IsNullOrEmpty(member)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _members.Text = Members.Count == 0 ? "" : string.Format(Str("L.S.GroupSounds.Members"), string.Join(", ", Members));
        _members.Visibility = Members.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        Overlapping = Selected == null ? Array.Empty<(string, string)>() : GroupSounds.Overlaps(_ctx.S, Selected);
        foreach (var (member, group) in Overlapping)
        {
            var line = EditorCard.Value(string.Format(Str("L.S.GroupSounds.Overlap"), member, group), secondary: true);
            line.TextWrapping = TextWrapping.Wrap;
            line.Margin = new Thickness(0, 6, 0, 0);
            _overlaps.Children.Add(line);
        }
        _signature = Signature();
    }

    /// <summary>One row. A built-in event is named from the catalog (so its name follows the language), a rule of
    /// the player's own by its name. <paramref name="name"/> is what the row's buttons are named for.</summary>
    private void AddRow(string key, string name, bool rule)
    {
        _keys.Add(key);
        int r = _table.AddRow();
        FrameworkElement label;
        if (key == GroupSoundChoices.EveryEvent) label = EditorCard.Note("L.S.GroupSounds.Every", strong: true);
        else if (rule) label = EditorCard.Value(name);
        else
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(StyleProperty, "Fx.Label");
            Loc.SetKey(text, "L.AlertEvt." + key);
            label = text;
        }
        Place(label, r, 0);
        var own = Own(key);
        Place(own.Length > 0 ? EditorCard.Value(System.IO.Path.GetFileName(own), secondary: true) : EditorCard.Note("L.S.GroupSounds.UsesEvent"), r, 1);
        Place(RowButton("L.S.Sounds.Change", "L.S.Sounds.ChangeFor", name, () => Change(key)), r, 2);
        Place(RowButton("L.S.Sounds.Play", "L.S.Sounds.PlayFor", name, () => Play(key)), r, 3);
        if (own.Length > 0) Place(RowButton("L.S.Sounds.Clear", "L.S.Sounds.ClearFor", name, () => Clear(key)), r, 4);
    }

    private static Button RowButton(string key, string nameKey, string name, Action click)
    {
        var button = EditorCard.Button(key, click);
        AutomationProperties.SetName(button, string.Format(Str(nameKey), name));
        return button;
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 4, 12, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        _table.Place(element, row, column);
    }

    /// <summary>Change…: the open-file dialog, starting at the current file. Only a file on a drive of this PC is
    /// kept (CustomRuleCheck.LocalSound, as for a rule's sound): the card says so for any other.</summary>
    public void Change(string eventKey)
    {
        if (Selected == null) return;
        var picked = _ctx.Host.PickFile(Own(eventKey), AlertsPages.SoundFilter);
        if (picked == null) return;
        if (CustomRuleCheck.LocalSound(picked).Length == 0) _ctx.Host.Toast(Str(AlertsPages.SoundRefusedKey(picked)));
        else Store(eventKey, picked);
    }

    /// <summary>Clear: the group has no sound for this event again.</summary>
    public void Clear(string eventKey) => Store(eventKey, "");

    private void Store(string eventKey, string path)
    {
        if (Selected == null) return;
        var text = GroupSoundChoices.Set(_ctx.S.GroupAlertSounds, Selected, eventKey, path);
        if (text != _ctx.S.GroupAlertSounds)
        {
            _ctx.S.GroupAlertSounds = text;
            _ctx.Applier.Request(ApplyScope.Save);
        }
        Refresh();
    }

    /// <summary>Play: the group's sound for the row, else what would play without it (for "Every event" the default
    /// sound), at the alert volume. Says so when there is nothing to play.</summary>
    public bool Play(string eventKey)
    {
        var s = _ctx.S;
        var file = Own(eventKey);
        if (file.Length == 0 && eventKey != GroupSoundChoices.EveryEvent) file = Own(GroupSoundChoices.EveryEvent);
        if (file.Length == 0) file = eventKey == GroupSoundChoices.EveryEvent ? s.AlertSoundFile : AlertSoundChoice.FileFor(s, eventKey) ?? "";
        if (AlertSoundPreview.Play(file, s.AlertSoundVolume)) return true;
        _ctx.Host.Toast(Str("L.S.Sounds.Missing"));
        return false;
    }
}
