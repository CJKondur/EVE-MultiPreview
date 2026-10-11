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
/// file. Looked at again every five seconds while shown, so a snooze that ran out goes and the minutes count down.</summary>
public sealed class SnoozesEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "alerts.events.snoozed";

    private readonly EventSnoozes? _snoozes;
    private readonly Func<DateTime> _utcNow;
    private readonly TextBlock _none = EditorCard.Note("L.S.Snoozes.None");
    private readonly StackPanel _rows = new();
    private readonly System.Windows.Controls.Button _endAll;
    private readonly List<string> _lines = new();
    private string _signature = "";

    /// <param name="utcNow">The clock the minutes left are counted from (tests hand one in).</param>
    public SnoozesEditor(SettingsContext ctx, Func<DateTime>? utcNow = null)
    {
        _snoozes = ctx.Services.Snoozes;   // null in a window opened without the app: nothing to show
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _endAll = EditorCard.Button("L.S.Snoozes.EndAll", EndAll);
        var below = new StackPanel();
        below.Children.Add(_none);
        below.Children.Add(_rows);
        Content = EditorCard.Build(ItemId, _endAll, below);
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Refresh(); }, seconds: 5);
        Refresh();
    }

    /// <summary>The snoozes the card lists, by character and then by event.</summary>
    public IReadOnlyList<EventSnooze> Shown { get; private set; } = Array.Empty<EventSnooze>();

    /// <summary>What each row says after the character's name (tests).</summary>
    public IReadOnlyList<string> Lines => _lines;

    private IReadOnlyList<EventSnooze> Active() => _snoozes?.Active() ?? Array.Empty<EventSnooze>();

    private string Signature()
    {
        var now = _utcNow();
        return string.Join("\n", Active().Select(s => s.Character + "|" + s.EventKey + "|" + (s.UntilRestart ? 0 : SnoozeText.MinutesLeft(s, now))));
    }

    public void Refresh()
    {
        var now = _utcNow();
        Shown = Active();
        _signature = Signature();
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
