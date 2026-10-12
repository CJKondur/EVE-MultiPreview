using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.Views;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Events › Alerts for one group (IDEAS #31): pick a group of Clients › Groups, then set each event
/// to "As everyone", on for this group, or off for this group. The choices are one line of text in one settings key
/// (GroupAlertChoices); App hands them to the gate and to the log monitor when the change is applied.
///
/// A player reads the card to find out why a character was, or was not, told. So it says who the group's members
/// are, and under the rows it lists each member who gets something other than this group's rows say, with the event
/// and a group that says so. The rule is AlertProfiles' (when a character's groups disagree about an event, the
/// event is on), and the card asks the rule: it works out no answer of its own.</summary>
public sealed class GroupAlertsEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.events.groups";

    private readonly SettingsContext _ctx;
    private readonly ComboBox _groups = new() { MinWidth = 220 };
    private readonly StackPanel _empty = new();
    private readonly WrapPanel _picker = new();
    private readonly TextBlock _members = EditorCard.Value("", secondary: true);
    private readonly TextBlock _notKept = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _warning = new();
    private readonly Border _warningLine;
    private readonly Grid _table = new();
    private readonly Button _clear;
    private readonly List<string> _keys = new();
    private readonly HashSet<string> _rules = new(StringComparer.Ordinal);
    /// <summary>Each row's event with the name the row shows for it.</summary>
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    /// <summary>Each row's list, and the place of "Off for this group" in it.</summary>
    private readonly Dictionary<string, (ComboBox List, int Off)> _lists = new(StringComparer.Ordinal);
    private string _signature = "";
    private bool _building;

    public GroupAlertsEditor(SettingsContext ctx)
    {
        _ctx = ctx;

        // No group yet: the card says so, and offers the way to the page where one is made.
        var none = EditorCard.Note("L.S.GroupAlerts.NoGroups");
        none.TextWrapping = TextWrapping.Wrap;
        var toGroups = EditorCard.Button("L.S.GroupAlerts.OpenGroups", () => _ctx.Host.Navigate(ClientsPages.GroupsId));
        toGroups.HorizontalAlignment = HorizontalAlignment.Left;
        toGroups.Margin = new Thickness(0, 8, 0, 0);
        _empty.Children.Add(none);
        _empty.Children.Add(toGroups);

        // The group and Clear, in a row that wraps: a long group name puts the button on a line of its own.
        AutomationProperties.SetName(_groups, Str("L.S.GroupAlerts.Group"));
        _groups.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            Selected = _groups.SelectedItem as string;
            ShowGroup();
            SayLines();
        };
        _clear = EditorCard.Button("L.S.GroupAlerts.Clear", ClearGroup);
        foreach (var part in new FrameworkElement[] { EditorCard.Note("L.S.GroupAlerts.Group", strong: true), _groups, _clear })
        {
            part.Margin = new Thickness(0, 0, 12, 6);
            part.VerticalAlignment = VerticalAlignment.Center;
            _picker.Children.Add(part);
        }

        // Both lines are told to a screen reader when a group is picked (SayLines), and are the help text of the
        // list that picks it (ShowLines).
        _members.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_members, AutomationLiveSetting.Polite);
        _notKept.SetResourceReference(StyleProperty, "Fx.Error");
        Loc.SetKey(_notKept, "L.S.GroupAlerts.NotKept");
        AutomationProperties.SetLiveSetting(_notKept, AutomationLiveSetting.Polite);

        // The warning under the rows has the look of a warning line (the Fx.Info template): a coloured bar, the
        // warning mark, the text.
        _warning.SetResourceReference(StyleProperty, "Fx.Label");
        AutomationProperties.SetLiveSetting(_warning, AutomationLiveSetting.Polite);
        var mark = new TextBlock { Text = ((char)0xE7BA).ToString(), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0) };
        mark.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.IconFont");
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Fx.WarningBrush");
        Loc.SetNameKey(mark, "L.S.Info.Warning");
        DockPanel.SetDock(mark, Dock.Left);
        var warned = new DockPanel();
        warned.Children.Add(mark);
        warned.Children.Add(_warning);
        _warningLine = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 0),
            Child = warned, Visibility = Visibility.Collapsed,
        };
        _warningLine.SetResourceReference(Border.BorderBrushProperty, "Fx.WarningBrush");
        _warningLine.SetResourceReference(Border.CornerRadiusProperty, "Fx.Radius");

        var stack = new StackPanel();
        stack.Children.Add(_empty);
        stack.Children.Add(_picker);
        stack.Children.Add(_members);
        stack.Children.Add(_notKept);
        stack.Children.Add(_table);
        stack.Children.Add(_warningLine);
        Content = EditorCard.Build(ItemId, null, stack);
        // The switches for everyone are in the table above this card, on the same page; rules and groups are made
        // on other pages while this one stays open in the background. The card follows them while it is shown.
        LiveRefresh.Attach(this, Look);
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

    /// <summary>The members of the shown group in this profile (of every group of that name, each member once).</summary>
    public IReadOnlyList<string> Members { get; private set; } = Array.Empty<string>();

    /// <summary>Where a member of the shown group gets, for an event the card lists, something other than this
    /// group's row says: the member, the event, what the member gets, and a group that says so (of several, the
    /// first on the Groups page: which one is named can change with their order there, what the member gets does
    /// not). It is the rule's own answer (AlertProfiles.Overridden), for the events the card has a row for: the rule
    /// also answers for a rule of the player's own that is switched off and for a key nobody knows, and the card
    /// never names a member for something the player can't see on it.</summary>
    public IReadOnlyList<(string Member, string Event, bool On, string Group)> Overridden { get; private set; } =
        Array.Empty<(string, string, bool, string)>();

    /// <summary>The line that says who is in the shown group, or that nobody is; and, on a line of its own, that
    /// more than one group goes by the name.</summary>
    public string MembersLine => _members.Text;

    /// <summary>The warning under the rows: what the rule does, and a line for each case of <see cref="Overridden"/>
    /// (members that get the same thing from the same group share one). "" when there is none.</summary>
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
            _empty.Visibility = SaysNoGroups ? Visibility.Visible : Visibility.Collapsed;
            _picker.Visibility = SaysNoGroups ? Visibility.Collapsed : Visibility.Visible;
            _groups.Items.Clear();
            foreach (var name in Groups) _groups.Items.Add(name);
            _groups.SelectedItem = Selected;
        }
        finally { _building = false; }
        ShowGroup();
    }

    /// <summary>What the card does every two seconds while it is shown: it is built again only when what it shows
    /// is not what it was (a switch for everyone, the rules that are on, the groups and their members, the choices),
    /// and never while one of its lists is open: a list would close under the player's pointer.</summary>
    public void Look()
    {
        if (_groups.IsDropDownOpen || _lists.Values.Any(row => row.List.IsDropDownOpen)) return;
        if (Signature() != _signature) Refresh();
    }

    /// <summary>Everything the card shows that can change without the card's doing, as one text to compare.</summary>
    private string Signature()
    {
        var s = _ctx.S;
        var text = new StringBuilder(s.GroupAlertProfiles).Append('\n');
        foreach (var e in AlertEvents.Listed) text.Append(AlertFields.IsOn(s, e.Key) ? '1' : '0');
        foreach (var rule in _ctx.CustomAlerts.Rules.Where(r => r.Enabled))
            text.Append('\n').Append(rule.AlertType).Append('\t').Append(rule.Severity).Append('\t').Append(rule.Name);
        foreach (var group in MergedGroups.Build(s))
            text.Append('\n').Append(group.Name).Append('\t').Append(string.Join("\t", group.Members));
        return text.ToString();
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
        SayLines();
    }

    /// <summary>A group was picked: a screen reader is told who is in it, and what the warning says (a live region
    /// announces nothing by itself).</summary>
    private void SayLines()
    {
        if (_members.Text.Length > 0) Announce.Changed(_members);
        if (_warning.Text.Length > 0) Announce.Changed(_warning);
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
            _table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _keys.Clear();
            _rules.Clear();
            _names.Clear();
            _lists.Clear();
            _notKept.Visibility = Visibility.Collapsed;
            if (Selected != null)
            {
                foreach (var group in AlertEvents.Groups)
                {
                    AddHeading("L.S.Events.Group." + group);
                    foreach (var e in AlertEvents.Listed.Where(x => x.Group == group))
                        AddEvent(e.Key, AlertEvents.Name(e.Key), e.Severity, AlertFields.IsOn(_ctx.S, e.Key), rule: false);
                }
                var rules = _ctx.CustomAlerts.Rules.Where(r => r.Enabled).ToList();
                if (rules.Count > 0) AddHeading("L.S.Page." + AlertsPages.CustomId);
                foreach (var rule in rules) AddEvent(rule.AlertType, rule.Name, rule.Severity, everyone: true, rule: true);
            }
        }
        finally { _building = false; }
        ShowLines();
    }

    /// <summary>Names as one list, joined as the catalog joins two (a full-width mark in Chinese and Japanese). A
    /// name is only ever an argument of the format.</summary>
    private static string Listed(IEnumerable<string> names)
    {
        string join = Str("L.S.GroupAlerts.Join");
        string? list = null;
        foreach (var name in names) list = list == null ? name : string.Format(join, list, name);
        return list ?? "";
    }

    /// <summary>The note that a group has alert choices of its own, with the place where they are made. It stands
    /// on the group's card on Clients › Groups (where a character is put into the group, and so under its choices),
    /// and, without <paramref name="group"/>, under the event table for every group that has choices: the groups of
    /// the Groups page, in its order. "" when there is nothing to note.</summary>
    public static string Note(AppSettings s, string? group = null)
    {
        var chosen = GroupAlertChoices.Read(s.GroupAlertProfiles);
        if (chosen.Count == 0) return "";
        var names = (group != null ? new[] { group } : MergedGroups.Build(s).Select(g => g.Name).Distinct(StringComparer.Ordinal))
            .Where(chosen.ContainsKey).ToList();
        return names.Count == 0 ? "" : string.Format(Str("L.S.GroupAlerts.Note"), Listed(names));
    }

    /// <summary>The lines around the rows: who is in the group, where a member gets something other than the rows
    /// say, and whether there is anything to clear.</summary>
    private void ShowLines()
    {
        var named = Selected == null ? new List<MergedGroup>() : MergedGroups.Build(_ctx.S).Where(g => g.Name == Selected).ToList();
        Members = named.SelectMany(g => g.Members).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string members = Selected == null ? "" : Members.Count == 0 ? Str("L.S.GroupAlerts.NoMembers") : string.Format(Str("L.S.GroupAlerts.Members"), Listed(Members));
        // Two colour groups can go by one name: they are one entry here, with one set of choices.
        if (named.Count > 1) members += "\n" + Str("L.S.GroupAlerts.SharedName");
        _members.Text = members;
        _members.Visibility = members.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // The rule's own answer, for the rows the card has built: never a member named for an event without a row.
        Overridden = Selected == null ? Array.Empty<(string, string, bool, string)>()
            : AlertProfiles.Overridden(_ctx.S, Selected).Where(o => _names.ContainsKey(o.Event)).ToList();
        var lines = Overridden.GroupBy(o => (o.Event, o.On, o.Group)).Select(same => string.Format(
            Str(same.Key.On ? "L.S.GroupAlerts.GetsOn" : "L.S.GroupAlerts.GetsOff"), Listed(same.Select(o => o.Member)), _names[same.Key.Event], same.Key.Group));
        string warning = Overridden.Count == 0 ? "" : Str("L.S.GroupAlerts.Overridden") + "\n" + string.Join("\n", lines);
        _warning.Text = warning;
        _warningLine.Visibility = warning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // Read to a player who tabs to the list: a screen reader that goes from control to control passes texts by.
        AutomationProperties.SetHelpText(_groups, warning.Length == 0 ? members : members + "\n" + warning);

        bool any = Selected != null && GroupAlertChoices.Read(_ctx.S.GroupAlertProfiles).ContainsKey(Selected);
        _clear.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _signature = Signature();
    }

    private void AddHeading(string key)
    {
        var title = EditorCard.Note(key, strong: true);
        title.Margin = new Thickness(0, 12, 0, 2);
        Place(title, AddRow(), 0);
        Grid.SetColumnSpan(title, 3);
    }

    /// <summary>The place in a row's list of what is stored for its event.</summary>
    private static int Shows(bool? chosen, bool rule, int off) => chosen switch { true when !rule => 1, false => off, _ => 0 };

    /// <param name="severity">The event's severity: the row carries the mark the event table shows for it.</param>
    /// <param name="everyone">What everyone has for this event right now.</param>
    /// <param name="rule">A rule of the player's own: it can be switched off for a group, not on.</param>
    private void AddEvent(string key, string name, string severity, bool everyone, bool rule)
    {
        _keys.Add(key);
        _names[key] = name;
        if (rule) _rules.Add(key);
        int row = AddRow();
        var label = EditorCard.Value(name);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        Place(label, row, 0);
        if (AlertEvents.Severities.Contains(severity)) Place(AlertEvents.SeverityText(severity), row, 1);

        var choice = new ComboBox { MinWidth = 200 };
        AutomationProperties.SetName(choice, string.Format(Str("L.S.GroupAlerts.ChoiceFor"), name, Selected));
        choice.Items.Add(Str(everyone ? "L.S.GroupAlerts.AsEveryoneOn" : "L.S.GroupAlerts.AsEveryoneOff"));
        if (!rule) choice.Items.Add(Str("L.S.GroupAlerts.On"));
        choice.Items.Add(Str("L.S.GroupAlerts.Off"));
        int off = choice.Items.Count - 1;
        choice.SelectedIndex = Shows(Choice(key), rule, off);
        choice.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            Choose(key, choice.SelectedIndex == 0 ? null : choice.SelectedIndex == off ? false : true);
        };
        _lists[key] = (choice, off);
        Place(choice, row, 2);
    }

    /// <summary>Stores the shown group's choice for an event; null is "as everyone". For a rule of the player's
    /// own, "on" is "as everyone": a rule can't be switched on for one group. A choice that can't be kept (no more
    /// choices fit: GroupAlertChoices.Set then gives the old text back) is not stored: the row shows what is stored
    /// again, and the card says so.</summary>
    public void Choose(string eventKey, bool? on)
    {
        if (Selected == null) return;
        if (on == true && (_rules.Contains(eventKey) || CustomAlertTypes.IsCustom(eventKey))) on = null;
        var text = GroupAlertChoices.Set(_ctx.S.GroupAlertProfiles, Selected, eventKey, on);
        if (text != _ctx.S.GroupAlertProfiles)
        {
            _ctx.S.GroupAlertProfiles = text;
            _ctx.Applier.Request(ApplyScope.Save);
        }
        // What is stored is read back: the same text also comes back for a choice that was made already.
        bool kept = Choice(eventKey) == on;
        if (_lists.TryGetValue(eventKey, out var row))
        {
            _building = true;
            try { row.List.SelectedIndex = Shows(Choice(eventKey), _rules.Contains(eventKey), row.Off); }
            finally { _building = false; }
        }
        _notKept.Visibility = kept ? Visibility.Collapsed : Visibility.Visible;
        if (!kept) Announce.Changed(_notKept);
        string warned = _warning.Text;
        ShowLines();
        if (_warning.Text.Length > 0 && _warning.Text != warned) Announce.Changed(_warning);
    }

    /// <summary>Every event of the shown group is "as everyone" again. Asks first, in the app's own dialog, by the
    /// group's name: one click would drop every choice, also those about a rule that is switched off and so not
    /// listed. With nothing to clear nothing is asked.</summary>
    public void ClearGroup()
    {
        if (Selected == null) return;
        var text = GroupAlertChoices.Forget(_ctx.S.GroupAlertProfiles, Selected);
        if (text == _ctx.S.GroupAlertProfiles) return;
        if (_ctx.Host.ConfirmChoice(Str("L.S.GroupAlerts.Clear"), string.Format(Str("L.S.GroupAlerts.ClearAsk"), Selected),
                Str("L.S.GroupAlerts.ClearYes"), Str("L.S.GroupAlerts.ClearNo")) != true) return;
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
