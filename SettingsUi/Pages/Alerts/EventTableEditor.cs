using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Alerts › Events › the event table (spec §5): Event, Severity, Colour, Lasts, Badge, Steady and On, one
/// row per event in groups. Every cell writes its own dictionary entry; the runtime reads them at the next alert,
/// so a change only saves.</summary>
public sealed class EventTableEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private static readonly string[] Columns =
    {
        "L.S.Events.ColEvent", "L.S.Events.ColSeverity", "L.S.Events.ColColour", "L.S.Events.ColLasts",
        "L.S.Events.ColBadge", "L.S.Events.ColSteady", "L.S.Events.ColOn",
    };

    private readonly SettingsContext _ctx;
    private readonly EditorTable _table = new();
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _groupsNote = EditorCard.Value("", secondary: true);
    private readonly TextBlock _logiNote = EditorCard.Value("", secondary: true);
    /// <summary>IDEAS P89: the line under the rows of "Capacitor neutralized" and "Jammed" that says "English client
    /// only"; each is shown while a running client's log header is not English.</summary>
    private readonly List<(string Key, TextBlock Mark)> _englishMarks = new();

    public EventTableEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _error.SetResourceReference(StyleProperty, "Fx.Error");
        var note = EditorCard.Note("L.S.Events.Note");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 8, 0, 0);
        _groupsNote.Margin = new Thickness(0, 8, 0, 0);
        _logiNote.Margin = new Thickness(0, 8, 0, 0);
        _logiNote.TextWrapping = TextWrapping.Wrap;
        var stack = new StackPanel();
        stack.Children.Add(_table);
        stack.Children.Add(_error);
        stack.Children.Add(note);
        stack.Children.Add(_groupsNote);
        stack.Children.Add(_logiNote);
        Content = EditorCard.Build(AlertsPages.EventTableId, null, stack);
        // IDEAS #31: the switches of this table are for everyone. A group can have choices of its own, made on a card
        // further down this same page; while one has, a line under the table names it.
        LiveRefresh.Attach(this, ShowGroupsNote);
        Refresh();
    }

    /// <summary>The event keys in the order shown.</summary>
    public IReadOnlyList<string> Keys { get; private set; } = Array.Empty<string>();

    /// <summary>The line under the table that names the groups with alert choices of their own; "" while none has any.</summary>
    public string GroupsNote => _groupsNote.Text;

    /// <summary>The events whose row says "English client only" now.</summary>
    public IReadOnlyList<string> EnglishOnlyShown =>
        _englishMarks.Where(m => m.Mark.Visibility == Visibility.Visible).Select(m => m.Key).ToList();

    /// <summary>IDEAS P109: the line under the table that says "Logi idle" is on but no group is marked as logi; "" otherwise.</summary>
    public string LogiNote => _logiNote.Text;

    /// <summary>Puts the line under the table on what the settings hold now, and the "English client only" lines on
    /// what the log monitor has read. Looked at every two seconds while the table is shown: the choices are made on
    /// another card of the page, and a client can log in. That card calls this at once when it has
    /// cleared a group's choices (RE2-m7), so that the line does not name the group for two seconds more.</summary>
    public void ShowGroupsNote()
    {
        ShowEnglishMarks();
        _groupsNote.Text = GroupAlertsEditor.Note(_ctx.S);
        _groupsNote.Visibility = _groupsNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _logiNote.Text = GroupAlertsEditor.LogiIdleNote(_ctx.S);
        _logiNote.Visibility = _logiNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>IDEAS P89: with no way to ask the monitor (a window made without the app) no row says it.</summary>
    private void ShowEnglishMarks()
    {
        var show = _ctx.Services.LogHeaderNotEnglish?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (_, mark) in _englishMarks) mark.Visibility = show;
    }

    private static string Str(string key) => SettingsContext.Str(key);

    public void Refresh()
    {
        _englishMarks.Clear();
        _table.Reset(Columns.Select((_, c) => c == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto).ToArray());
        int header = AddRow(header: true);
        for (int c = 0; c < Columns.Length; c++) Place(EditorTable.Head(Columns[c]), header, c);
        var keys = new List<string>();
        foreach (var group in AlertEvents.Groups)
        {
            var title = EditorCard.Note("L.S.Events.Group." + group, strong: true);
            Place(title, AddRow(), 0, span: Columns.Length);
            title.Margin = new Thickness(0, 12, 0, 2);
            foreach (var e in AlertEvents.Listed.Where(x => x.Group == group))
            {
                AddEvent(e);
                keys.Add(e.Key);
            }
        }
        Keys = keys;
        ShowError(null);
        ShowGroupsNote();
    }

    private void AddEvent(AlertEventDef e)
    {
        int r = AddRow();
        var name = AlertEvents.Name(e.Key);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "Fx.Label");
        Loc.SetKey(label, e.LabelKey);
        Place(label, r, 0);
        Place(AlertEvents.SeverityText(e.Severity), r, 1);

        // The picker opens on the colour shown now (the old button reopened on the colour the row was built with).
        var swatch = EditorCard.Swatch(AlertFields.Colour(_ctx.S, e), () => PickColour(e.Key));
        AutomationProperties.SetName(swatch, string.Format(Str("L.S.Events.PickColour"), name));
        var colour = EditorCard.Row(swatch);
        if (AlertFields.HasOwnColour(_ctx.S, e.Key))
        {
            var back = string.Format(Str("L.S.Events.ResetColour"), name);
            var reset = new Button { Content = "↺", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), ToolTip = back };
            AutomationProperties.SetName(reset, back);
            reset.Click += (_, _) => ClearColour(e.Key);
            colour.Children.Add(reset);
        }
        Place(colour, r, 2);
        Place(LastsBox(e.Key, name), r, 3);

        var badge = EditorCard.Toggle(AlertFields.Badge(_ctx.S, e.Key), on => Store(() => AlertFields.SetBadge(_ctx.S, e.Key, on)));
        AutomationProperties.SetName(badge, string.Format(Str("L.S.Events.BadgeFor"), name));
        Place(badge, r, 4);

        var steady = EditorCard.Toggle(AlertFields.Steady(_ctx.S, e), on => Store(() => AlertFields.SetSteady(_ctx.S, e.Key, on)));
        AutomationProperties.SetName(steady, string.Format(Str("L.S.Events.SteadyFor"), name));
        if (e.Severity == AlertEvents.Info)
        {
            steady.IsEnabled = false;   // painted steady whatever is stored
            steady.ToolTip = Str("L.S.Events.InfoSteady");
        }
        Place(steady, r, 5);

        var onSwitch = EditorCard.Toggle(AlertFields.IsOn(_ctx.S, e.Key), v => Store(() => AlertFields.SetOn(_ctx.S, e.Key, v)));
        AutomationProperties.SetName(onSwitch, string.Format(Str("L.S.Events.OnFor"), name));
        Place(onSwitch, r, 6);

        // IDEAS P89: these two are read from an English client's log only. A line of their own under the row, across the
        // table, shown while a running client's log header is not English (ShowEnglishMarks); the row is gone while it is collapsed.
        if (e.Key is EwarLines.Neutralized or EwarLines.Jammed)
        {
            var mark = EditorCard.Note("L.S.Events.EnglishOnly");
            mark.Visibility = Visibility.Collapsed;
            Place(mark, AddRow(), 0, span: Columns.Length);
            _englishMarks.Add((e.Key, mark));
        }
    }

    /// <summary>Stores on Enter or when the box loses focus; a bad entry shows an error and the old value comes back.</summary>
    private TextBox LastsBox(string key, string name)
    {
        string Shown() => AlertFields.Lasts(_ctx.S, key)?.ToString(CultureInfo.CurrentCulture) ?? "";
        var given = Shown();
        var box = new TextBox { Text = given, Width = 56, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, string.Format(Str("L.S.Events.LastsFor"), name));
        void Commit()
        {
            if (box.Text == given) return;   // untouched: nothing to store
            var error = CommitLasts(key, box.Text);
            if (error != null)
            {
                box.Text = given;
                ShowError(string.Format(CultureInfo.CurrentCulture, Str(error), AlertFields.MinLasts, AlertFields.MaxLasts));
                return;
            }
            ShowError(null);
            box.Text = given = Shown();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Commit(); k.Handled = true; } };
        return box;
    }

    /// <summary>A typed "Lasts": empty = the severity's time, else 0–3600 s. Returns the L.* key of an error, or null.</summary>
    public string? CommitLasts(string key, string text)
    {
        var error = AlertFields.CheckLasts(text, out var seconds);
        if (error != null) return error;
        if (AlertFields.Lasts(_ctx.S, key) == seconds) return null;
        AlertFields.SetLasts(_ctx.S, key, seconds);
        _ctx.Applier.Request(ApplyScope.Save);
        return null;
    }

    public void PickColour(string key)
    {
        var picked = _ctx.Host.PickColor(AlertFields.Colour(_ctx.S, AlertEvents.Find(key)));
        if (picked == null) return;
        AlertFields.SetColour(_ctx.S, key, picked);
        _ctx.Applier.Request(ApplyScope.Save);
        Refresh();
    }

    /// <summary>↺: the event takes its severity's colour again.</summary>
    public void ClearColour(string key)
    {
        if (!AlertFields.HasOwnColour(_ctx.S, key)) return;
        AlertFields.ClearColour(_ctx.S, key);
        _ctx.Applier.Request(ApplyScope.Save);
        Refresh();
    }

    private void Store(Action write)
    {
        write();
        _ctx.Applier.Request(ApplyScope.Save);
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private int AddRow(bool header = false) => _table.AddRow(header);

    /// <summary>Badge, Steady and On (columns 4–6) are switches: their headers and switches are centred in the column, so
    /// each header sits over its switch (left-aligned, a header lined up with the switch's On/Off text instead).</summary>
    private const int FirstSwitchColumn = 4;

    private void Place(FrameworkElement element, int row, int column, int span = 1)
    {
        element.Margin = new Thickness(0, 4, 16, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = column >= FirstSwitchColumn ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _table.Place(element, row, column, span);
    }
}
