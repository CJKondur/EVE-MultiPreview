using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Events › Alerts for one group (IDEAS #31): pick a group of Clients › Groups, then set each event
/// to "As everyone", on for this group, or off for this group. The choices are one line of text in one settings key
/// (GroupAlertChoices); App hands them to the gate and to the log monitor when the change is applied. The card says
/// who the group's members are, and which of them an earlier group's choices reach first.</summary>
public sealed class GroupAlertsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.events.groups";

    private readonly SettingsContext _ctx;
    private readonly ComboBox _groups = new() { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _none = EditorCard.Note("L.S.GroupAlerts.NoGroups");
    private readonly TextBlock _members = EditorCard.Value("", secondary: true);
    private readonly TextBlock _warning = EditorCard.Value("", secondary: true);
    private readonly StackPanel _picker;
    private readonly Grid _table = new();
    private readonly Button _clear;
    private readonly List<string> _keys = new();
    private readonly HashSet<string> _rules = new(StringComparer.Ordinal);
    private bool _building;

    public GroupAlertsEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _none.TextWrapping = TextWrapping.Wrap;
        foreach (var line in new[] { _members, _warning })
        {
            line.TextWrapping = TextWrapping.Wrap;
            line.Margin = new Thickness(0, 6, 0, 0);
        }
        AutomationProperties.SetName(_groups, Str("L.S.GroupAlerts.Group"));
        _groups.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            Selected = _groups.SelectedItem as string;
            ShowGroup();
        };
        _clear = EditorCard.Button("L.S.GroupAlerts.Clear", ClearGroup);
        _clear.Margin = new Thickness(12, 0, 0, 0);
        var label = EditorCard.Note("L.S.GroupAlerts.Group", strong: true);
        label.Margin = new Thickness(0, 0, 12, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        _picker = EditorCard.Row(label, _groups, _clear);
        var stack = new StackPanel();
        stack.Children.Add(_none);
        stack.Children.Add(_picker);
        stack.Children.Add(_members);
        stack.Children.Add(_warning);
        stack.Children.Add(_table);
        Content = EditorCard.Build(ItemId, null, stack);
        Refresh();
    }

    private static string Str(string key) => SettingsContext.Str(key);

    /// <summary>The groups the card offers: the Groups page's, in its order, each name once.</summary>
    public IReadOnlyList<string> Groups { get; private set; } = Array.Empty<string>();

    /// <summary>The group whose choices are shown, or null when there is no group.</summary>
    public string? Selected { get; private set; }

    /// <summary>The events the card lists, in order: the built-in ones, then the player's own rules that are on.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>The card says that there is no group yet.</summary>
    public bool SaysNoGroups { get; private set; }

    /// <summary>The members of the shown group in this profile.</summary>
    public IReadOnlyList<string> Members { get; private set; } = Array.Empty<string>();

    /// <summary>The members of the shown group that an earlier group's choices reach first, each with that group.</summary>
    public IReadOnlyList<(string Character, string Group)> ReachedFirst { get; private set; } = Array.Empty<(string, string)>();

    /// <summary>The line that says who is in the shown group, or that nobody is.</summary>
    public string MembersLine => _members.Text;

    /// <summary>The line that names the members an earlier group's choices reach first; "" when there is none.</summary>
    public string WarningLine => _warning.Text;

    /// <summary>The shown group's choice for an event: on, off, or null for "as everyone".</summary>
    public bool? Choice(string eventKey) =>
        Selected != null && GroupAlertChoices.Read(_ctx.S.GroupAlertProfiles).TryGetValue(Selected, out var chosen)
        && chosen.TryGetValue(eventKey, out var on) ? on : null;

    public void Refresh()
    {
        _building = true;
        try
        {
            Groups = MergedGroups.Build(_ctx.S).Select(g => g.Name).Distinct(StringComparer.Ordinal).ToList();
            if (Selected == null || !Groups.Contains(Selected)) Selected = Groups.FirstOrDefault();
            SaysNoGroups = Groups.Count == 0;
            _none.Visibility = SaysNoGroups ? Visibility.Visible : Visibility.Collapsed;
            _picker.Visibility = SaysNoGroups ? Visibility.Collapsed : Visibility.Visible;
            _groups.Items.Clear();
            foreach (var name in Groups) _groups.Items.Add(name);
            _groups.SelectedItem = Selected;
        }
        finally { _building = false; }
        ShowGroup();
    }

    /// <summary>Shows another group's choices. A name the card does not offer changes nothing.</summary>
    public void Select(string group)
    {
        if (!Groups.Contains(group)) return;
        Selected = group;
        _building = true;
        try { _groups.SelectedItem = group; }
        finally { _building = false; }
        ShowGroup();
    }

    private void ShowGroup()
    {
        _building = true;
        try
        {
            _table.Children.Clear();
            _table.RowDefinitions.Clear();
            _table.ColumnDefinitions.Clear();
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _keys.Clear();
            _rules.Clear();
            ShowLines();
            if (Selected == null) return;
            foreach (var group in AlertEvents.Groups)
            {
                AddHeading("L.S.Events.Group." + group);
                foreach (var e in AlertEvents.Listed.Where(x => x.Group == group)) AddEvent(e.Key, AlertEvents.Name(e.Key), AlertFields.IsOn(_ctx.S, e.Key), rule: false);
            }
            var rules = _ctx.CustomAlerts.Rules.Where(r => r.Enabled).ToList();
            if (rules.Count > 0) AddHeading("L.S.Page." + AlertsPages.CustomId);
            foreach (var rule in rules) AddEvent(rule.AlertType, rule.Name, everyone: true, rule: true);
        }
        finally { _building = false; }
    }

    /// <summary>The two lines under the group's name, and whether there is anything to clear.</summary>
    private void ShowLines()
    {
        Members = Selected == null ? Array.Empty<string>()
            : MergedGroups.Build(_ctx.S).Where(g => g.Name == Selected).SelectMany(g => g.Members).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _members.Text = Selected == null ? "" : Members.Count == 0 ? Str("L.S.GroupAlerts.NoMembers") : string.Format(Str("L.S.GroupAlerts.Members"), string.Join(", ", Members));
        _members.Visibility = _members.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ReachedFirst = Selected == null ? Array.Empty<(string, string)>() : AlertProfiles.Overridden(_ctx.S, Selected);
        _warning.Text = ReachedFirst.Count == 0 ? ""
            : string.Format(Str("L.S.GroupAlerts.Overridden"), string.Join(", ", ReachedFirst.Select(r => r.Character + " (" + r.Group + ")")));
        _warning.Visibility = _warning.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool any = Selected != null && GroupAlertChoices.Read(_ctx.S.GroupAlertProfiles).ContainsKey(Selected);
        _clear.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddHeading(string key)
    {
        var title = EditorCard.Note(key, strong: true);
        title.Margin = new Thickness(0, 12, 0, 2);
        Place(title, AddRow(), 0);
        Grid.SetColumnSpan(title, 2);
    }

    /// <param name="everyone">What everyone has for this event right now.</param>
    /// <param name="rule">A rule of the player's own: it can be switched off for a group, not on.</param>
    private void AddEvent(string key, string name, bool everyone, bool rule)
    {
        _keys.Add(key);
        if (rule) _rules.Add(key);
        int row = AddRow();
        var label = EditorCard.Value(name);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        Place(label, row, 0);

        var choice = new ComboBox { MinWidth = 200 };
        AutomationProperties.SetName(choice, string.Format(Str("L.S.GroupAlerts.ChoiceFor"), name, Selected));
        choice.Items.Add(Str(everyone ? "L.S.GroupAlerts.AsEveryoneOn" : "L.S.GroupAlerts.AsEveryoneOff"));
        if (!rule) choice.Items.Add(Str("L.S.GroupAlerts.On"));
        choice.Items.Add(Str("L.S.GroupAlerts.Off"));
        int off = choice.Items.Count - 1;
        choice.SelectedIndex = Choice(key) switch { true when !rule => 1, false => off, _ => 0 };
        choice.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            Choose(key, choice.SelectedIndex == 0 ? null : choice.SelectedIndex == off ? false : true);
        };
        Place(choice, row, 1);
    }

    /// <summary>Stores the shown group's choice for an event; null is "as everyone". For a rule of the player's
    /// own, "on" is "as everyone": a rule can't be switched on for one group.</summary>
    public void Choose(string eventKey, bool? on)
    {
        if (Selected == null) return;
        if (on == true && (_rules.Contains(eventKey) || CustomAlertTypes.IsCustom(eventKey))) on = null;
        var text = GroupAlertChoices.Set(_ctx.S.GroupAlertProfiles, Selected, eventKey, on);
        if (text == _ctx.S.GroupAlertProfiles) return;
        _ctx.S.GroupAlertProfiles = text;
        _ctx.Applier.Request(ApplyScope.Save);
        ShowLines();
    }

    /// <summary>Every event of the shown group is "as everyone" again.</summary>
    public void ClearGroup()
    {
        if (Selected == null) return;
        var text = GroupAlertChoices.Forget(_ctx.S.GroupAlertProfiles, Selected);
        if (text == _ctx.S.GroupAlertProfiles) return;
        _ctx.S.GroupAlertProfiles = text;
        _ctx.Applier.Request(ApplyScope.Save);
        ShowGroup();
    }

    private int AddRow()
    {
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return _table.RowDefinitions.Count - 1;
    }

    private void Place(FrameworkElement element, int row, int column)
    {
        if (element.Margin == default) element.Margin = new Thickness(0, 4, 16, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        _table.Children.Add(element);
    }
}
