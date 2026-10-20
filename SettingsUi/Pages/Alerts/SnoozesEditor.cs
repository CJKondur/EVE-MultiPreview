using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using EveMultiPreview.Views;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Events › Snoozed right now (IDEAS #30): every event snoozed for one character, with how long it
/// still runs, End on each and End all. It shows the app's state, not a setting: nothing here is saved to the settings
/// file. Looked at again every five seconds while shown, so a snooze that ran out goes and the minutes count down.
///
/// Above the list is the Add row (IDEAS P80 (1)): a snooze can be started here, without a toast. The character is one
/// of the running ones or a typed name, the event one the Events page lists (or a rule of the player's own), the length
/// one of the toast menu's three. Add calls the store itself: App.SnoozeEvent, which a toast's menu uses, is not
/// involved. The names on this page are the real ones, as on every page of this window.</summary>
public sealed class SnoozesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.events.snoozed";

    private readonly SettingsContext _ctx;
    private readonly EventSnoozes? _snoozes;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<IEnumerable<string>> _running;
    private readonly TextBlock _none = EditorCard.Note("L.S.Snoozes.None");
    private readonly StackPanel _rows = new();
    private readonly System.Windows.Controls.Button _endAll;
    private readonly List<string> _lines = new();
    private readonly System.Windows.Controls.ComboBox _character = new() { IsEditable = true, MinWidth = 190 };
    private readonly System.Windows.Controls.ComboBox _event = new() { MinWidth = 210 };
    private readonly System.Windows.Controls.ComboBox _length = new() { MinWidth = 210 };
    private readonly WrapPanel _add = new();
    private readonly TextBlock _refused = EditorCard.Note("L.S.Snoozes.AddRefused");
    private readonly List<string> _eventKeys = new();
    private readonly List<string> _characterNames = new();
    private string _signature = "";

    /// <param name="utcNow">The clock the minutes left are counted from (tests hand one in).</param>
    /// <param name="running">The characters that have a client now, for the Add row (tests hand some in). Default: the
    /// running ones, as the other pages' pickers have them.</param>
    public SnoozesEditor(SettingsContext ctx, Func<DateTime>? utcNow = null, Func<IEnumerable<string>>? running = null)
    {
        _ctx = ctx;
        _snoozes = ctx.Services.Snoozes;   // null in a window opened without the app: nothing to show
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _running = running ?? (() => CharacterDirectory.Running(ctx));
        _endAll = EditorCard.Button("L.S.Snoozes.EndAll", EndAll);

        foreach (var (textKey, _) in SnoozeAdd.Lengths) _length.Items.Add(new ComboBoxItem { Content = SettingsContext.Str(textKey) });
        _length.SelectedIndex = 1;   // an hour: the length most snoozes are for
        _add.Children.Add(EditorCard.Field("L.S.Snoozes.AddCharacter", _character));
        _add.Children.Add(EditorCard.Field("L.S.Snoozes.AddEvent", _event));
        _add.Children.Add(EditorCard.Field("L.S.Snoozes.AddLength", _length));
        var add = EditorCard.Button("L.S.Snoozes.Add", () => Add());
        add.VerticalAlignment = VerticalAlignment.Top;
        add.Margin = new Thickness(0, 0, 0, 8);
        _add.Children.Add(add);
        _add.Visibility = _snoozes == null ? Visibility.Collapsed : Visibility.Visible;
        _refused.Visibility = Visibility.Collapsed;

        var below = new StackPanel();
        below.Children.Add(_add);
        below.Children.Add(_refused);
        below.Children.Add(_none);
        below.Children.Add(_rows);
        Content = EditorCard.Build(ItemId, _endAll, below);
        LiveRefresh.Attach(this, Poll, seconds: 5);
        Refresh();
    }

    /// <summary>What the five-second look does: lists again when the snoozes, the running clients or the rules are not what
    /// the last full listing was made from (tests call it in place of the timer).</summary>
    public void Poll()
    {
        if (Signature() != _signature) Refresh();
    }

    /// <summary>The snoozes the card lists, by character and then by event.</summary>
    public IReadOnlyList<EventSnooze> Shown { get; private set; } = Array.Empty<EventSnooze>();

    /// <summary>What each row says after the character's name (tests).</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>The Add row is on the card: there is a store to add to (tests).</summary>
    public bool AddRowShown => _add.Visibility == Visibility.Visible;

    /// <summary>The characters the Add row offers (tests).</summary>
    public IReadOnlyList<string> CharacterChoices => _characterNames;

    /// <summary>The event keys the Add row offers, in order (tests).</summary>
    public IReadOnlyList<string> EventChoices => _eventKeys;

    /// <summary>The lengths the Add row offers, as it words them (tests).</summary>
    public IReadOnlyList<string> LengthChoices => _length.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content).ToList();

    /// <summary>What is in the character box, chosen or typed (tests).</summary>
    public string CharacterText
    {
        get => _character.Text;
        set => _character.Text = value;
    }

    /// <summary>Chooses an event of the Add row by its key; one it does not offer chooses none (tests).</summary>
    public void ChooseEvent(string key) => _event.SelectedIndex = _eventKeys.IndexOf(key);

    /// <summary>Chooses a length of the Add row by its place in <see cref="SnoozeAdd.Lengths"/> (tests).</summary>
    public void ChooseLength(int index) => _length.SelectedIndex = index;

    private IReadOnlyList<EventSnooze> Active() => _snoozes?.Active() ?? Array.Empty<EventSnooze>();

    /// <summary>The player's own rules that raise alerts: a rule that is off has nothing to snooze.</summary>
    private IEnumerable<CustomAlertRule> Rules() => _ctx.Services.CustomAlerts?.Rules.Where(r => r.Enabled) ?? Enumerable.Empty<CustomAlertRule>();

    private string Signature()
    {
        var now = _utcNow();
        return string.Join("\n", Active().Select(s => s.Character + "|" + s.EventKey + "|" + (s.UntilRestart ? 0 : SnoozeText.MinutesLeft(s, now))))
            + "\n#" + string.Join("|", Running()) + "\n#" + string.Join("|", Rules().Select(r => r.AlertType + "=" + r.Name));
    }

    private List<string> Running() => _running().Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The keyboard is in the character box (its text box too). Asked of the property itself, which is what WPF's
    /// own triggers read, so that a test can set it without taking the foreground.</summary>
    private bool BeingTypedIn => (bool)_character.GetValue(UIElement.IsKeyboardFocusWithinProperty);

    /// <summary>The Add row's characters and events: the clients that run and the rules that are on change while this
    /// page is open. What is typed, and the event chosen, stay. A list that is open is left alone: it would close under
    /// the pointer. So is the character box while the player is typing in it: clearing its items resets the text, and the
    /// caret and the selection with it. False when it was left alone, so that the next look tries again.</summary>
    private bool FillChoices()
    {
        if (_character.IsDropDownOpen || _event.IsDropDownOpen || BeingTypedIn) return false;
        var typed = _character.Text;
        var chosen = _event.SelectedIndex >= 0 && _event.SelectedIndex < _eventKeys.Count ? _eventKeys[_event.SelectedIndex] : null;

        _characterNames.Clear();
        _characterNames.AddRange(Running());
        _character.Items.Clear();
        foreach (var name in _characterNames) _character.Items.Add(name);
        _character.Text = typed;

        _eventKeys.Clear();
        _event.Items.Clear();
        foreach (var e in AlertEvents.Listed) AddEvent(e.Key, AlertEvents.Name(e.Key));
        foreach (var rule in Rules())
            AddEvent(rule.AlertType, string.IsNullOrWhiteSpace(rule.Name) ? AlertHub.EventDisplayName(rule.AlertType) : rule.Name);
        _event.SelectedIndex = chosen == null ? -1 : _eventKeys.IndexOf(chosen);
        return true;
    }

    /// <summary>The sign that Add made nothing: shown until the next Add, which hides it when it works.</summary>
    public bool RefusalShown => _refused.Visibility == Visibility.Visible;

    private void AddEvent(string key, string name)
    {
        _eventKeys.Add(key);
        _event.Items.Add(new ComboBoxItem { Content = name });
    }

    /// <summary>Starts the snooze the Add row describes, in the store itself. False when there is nothing to start: no
    /// store, no character, no event, or a name a snooze cannot have (<see cref="SnoozeAdd.TryBuild"/>, which refuses
    /// a name that is too long rather than cutting it). A refusal says so on the card (<see cref="RefusalShown"/>).</summary>
    public bool Add()
    {
        if (_snoozes == null) return false;
        int eventAt = _event.SelectedIndex, lengthAt = _length.SelectedIndex;
        if (eventAt < 0 || eventAt >= _eventKeys.Count || lengthAt < 0 || lengthAt >= SnoozeAdd.Lengths.Count
            || SnoozeAdd.TryBuild(_character.Text, _eventKeys[eventAt], SnoozeAdd.Lengths[lengthAt].Length) is not { } made)
        {
            _refused.Visibility = Visibility.Visible;
            return false;
        }
        _refused.Visibility = Visibility.Collapsed;
        _snoozes.Snooze(made.Character, made.EventKey, made.Length);
        Refresh();
        return true;
    }

    public void Refresh()
    {
        var now = _utcNow();
        Shown = Active();
        // The signature stands for the Add row as well: kept from the last full listing while the row was left alone
        // (a list was open), the next look sees the difference and lists again.
        var signature = Signature();
        if (FillChoices()) _signature = signature;
        _none.Visibility = Shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _endAll.Visibility = Shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _rows.Children.Clear();
        _lines.Clear();
        foreach (var snooze in Shown)
        {
            var line = SnoozeText.Line(snooze, now);
            _lines.Add(line);
            var end = EditorCard.Button("L.S.Snoozes.End", () => End(snooze));
            AutomationProperties.SetName(end, string.Format(SettingsContext.Str("L.S.Snoozes.EndFor"),
                AlertHub.EventDisplayName(snooze.EventKey), snooze.Character));
            DockPanel.SetDock(end, Dock.Right);
            var who = EditorCard.Value(snooze.Character, strong: true);
            who.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(who, Dock.Left);
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(end);
            row.Children.Add(who);
            row.Children.Add(EditorCard.Value(line, secondary: true));
            _rows.Children.Add(row);
        }
    }

    /// <summary>Ends one snooze: that alert comes again for that character.</summary>
    public void End(EventSnooze snooze)
    {
        _snoozes?.End(snooze.Character, snooze.EventKey);
        Refresh();
    }

    public void EndAll()
    {
        _snoozes?.EndAll();
        Refresh();
    }
}
