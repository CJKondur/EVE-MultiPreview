using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using ListViewItem = System.Windows.Controls.ListViewItem;

namespace EveMultiPreview.Views;

/// <summary>What the history window asks of the app. App wires each one; tests give recorders, so no test opens a
/// file dialog, asks a question on screen or brings a window to the front.</summary>
public sealed class AlertHistoryActions
{
    /// <summary>Brings that character's client to the front, the way a click on a toast does. False when the client
    /// isn't running.</summary>
    public Func<string, bool> SwitchTo { get; init; } = _ => false;
    /// <summary>Asks where to save a file, given a name to suggest. Null when the player cancelled.</summary>
    public Func<string, string?> PickSaveFile { get; init; } = _ => null;
    /// <summary>A yes/no question (title, body). True for yes.</summary>
    public Func<string, string, bool> Confirm { get; init; } = (_, _) => false;
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    /// <summary>This PC's list separator: where it is ";" the CSV export puts semicolons between its fields, else
    /// commas (AlertHistoryCsv.SeparatorFor). The writer is told it as well: where it is neither of the two, a
    /// spreadsheet on this PC could begin a cell behind it. App leaves it as it is; tests hand one in.</summary>
    public Func<string> ListSeparator { get; init; } = () => CultureInfo.CurrentCulture.TextInfo.ListSeparator;
    /// <summary>Whether the "Keep an alert history" setting is on. While it is off the window says so above the
    /// list: what is listed is then what was kept before, and nothing new arrives. Asked at every refresh.</summary>
    public Func<bool> HistoryOn { get; init; } = () => true;
    /// <summary>Tests: file work runs at once, on the calling thread, instead of on a background thread.</summary>
    public bool Synchronous { get; init; }
}

/// <summary>One row as the list shows it.</summary>
public sealed class HistoryRowVM : INotifyPropertyChanged
{
    private string _age = "";

    /// <param name="since">For a run of several alerts: when its first one was, as the Time column would show it.</param>
    public HistoryRowVM(HistoryRow row, string time, string eventName, string note, Brush bar, string severityName, string? since = null)
    {
        Row = row;
        Time = time;
        TimeTip = string.IsNullOrEmpty(since) ? time : since + " – " + time;
        Event = eventName;
        Note = note;
        Bar = bar;
        SeverityName = severityName;
    }

    public HistoryRow Row { get; }
    /// <summary>When the alert was; for a run, when its newest alert was.</summary>
    public string Time { get; }
    /// <summary>The time cell's tooltip: the time, or for a run the time of its first alert and of its last.</summary>
    public string TimeTip { get; }
    /// <summary>The character, as the history kept it: the key the switch and the filters work by (never masked).</summary>
    public string Character => Row.Newest.Character;
    /// <summary>What the Character column draws: the name, or what streamer mode (IDEAS #19) shows instead.</summary>
    public string CharacterText => StreamerMask.Name(StreamerKind.Character, Character);
    public string Event { get; }
    /// <summary>"×3" for a run of three, "" for one alert.</summary>
    public string Count => Row.Count > 1 ? "×" + Row.Count.ToString(CultureInfo.CurrentCulture) : "";
    /// <summary>What the Alert column shows: the name, and the count behind it for a run.</summary>
    public string EventText => Count.Length > 0 ? Event + "  " + Count : Event;
    /// <summary>What became of the alert when the player got nothing of it ("Muted", "No client window"), else "".</summary>
    public string Note { get; }
    public string SystemName => Row.Newest.SolarSystem;
    /// <summary>What the System column draws: the system, or nothing while streamer mode is on.</summary>
    public string SystemText => StreamerMask.Name(StreamerKind.System, SystemName);
    /// <summary>The matched or stored text, as the history kept it (the search and the CSV work from it, never masked).</summary>
    public string Text => Row.Newest.Text;
    /// <summary>What the Text column draws: the text, or nothing while streamer mode (IDEAS #19) is on. It can carry a system
    /// ("Jita → Perimeter 3/4") or a name from a matched log line.</summary>
    public string TextShown => StreamerMask.Name(StreamerKind.Label, Text);
    public Brush Bar { get; }
    public string SeverityName { get; }
    public string Age { get => _age; private set { if (_age != value) { _age = value; Raise(nameof(Age)); Raise(nameof(ReadOut)); } } }

    /// <summary>The row in one sentence, for a screen reader.</summary>
    public string ReadOut => string.Join(", ", new[] { Time, Age, CharacterText, Event, Count, Note, SeverityName, SystemText, TextShown }.Where(p => p.Length > 0));

    /// <summary>A list row with no name of its own is read out by what its item says it is: the sentence above. (The
    /// row keeps the theme's own style this way; a style set on it would replace the one that lays out the columns.)</summary>
    public override string ToString() => ReadOut;

    public void Tick(DateTime utcNow) => Age = AlertAge.Text(utcNow - Row.Newest.TimeUtc);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The alert history (IDEAS #1): every alert that was kept, newest first, with filters, a jump to the client,
/// a CSV export and Clear. It reads the day files on a background thread and never writes one itself. Opened from the
/// alert hub, the tray menu and Alerts › Alert hub.</summary>
public partial class AlertHistoryWindow : Window
{
    private sealed record Choice(string Value, string Text);

    private readonly AlertHistory _store;
    private readonly AppSettings _settings;
    private readonly AlertHistoryActions _do;
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, Brush> _bars = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AlertHistoryEntry> _loaded = Array.Empty<AlertHistoryEntry>();
    private IReadOnlyList<AlertHistoryEntry> _shown = Array.Empty<AlertHistoryEntry>();
    private List<HistoryRowVM> _rows = new();
    private bool _more, _loading, _again, _closed;
    private bool _filling = true;      // until the lists are filled: a choice set by code is not the player's
    private bool _applying;            // while Apply puts the selection back: that is not the player choosing a row
    private int _badLines;
    private int _changeWaiting;        // 1 while a message from the log monitor's thread is on its way to this one
    private readonly double _smallestByDesign;   // the window's MinHeight as the XAML has it

    public AlertHistoryWindow(AlertHistory store, AppSettings settings, AlertHistoryActions actions)
    {
        UiStyles.Attach(this);
        InitializeComponent();
        // The wheel over a closed dropdown list (the four filters) is not "the next choice": it goes past the list.
        PreviewMouseWheel += EveMultiPreview.SettingsUi.SettingsShell.WheelPastClosedLists;
        UiFont.Apply(this, LocalizationService.CurrentLanguage);
        _store = store;
        _settings = settings;
        _do = actions;

        AlertList.View = Columns();
        CmbRange.ItemsSource = new[]
        {
            new Choice(nameof(HistoryRange.Today), Str("L.Hist.Range.Today", "Today")),
            new Choice(nameof(HistoryRange.Week), Str("L.Hist.Range.Week", "Last 7 days")),
            new Choice(nameof(HistoryRange.All), Str("L.Hist.Range.All", "Everything kept")),
        };
        CmbRange.SelectedIndex = 0;
        CmbSeverity.ItemsSource = new[] { new Choice("", Str("L.Hist.AllSeverities", "All severities")) }
            .Concat(AlertEvents.Severities.Select(s => new Choice(s, SeverityName(s)))).ToList();
        CmbSeverity.SelectedIndex = 0;
        FillFilters();

        _smallestByDesign = MinHeight;
        LayoutUpdated += (_, _) => KeepRoomForTheList();

        // A new alert shows within two seconds, however many arrive: one re-read, never one per alert.
        _store.Added += OnStoreChanged;
        _store.Cleared += OnStoreCleared;
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); Reload(); };
        _ageTimer.Tick += (_, _) => TickAges();
        _ageTimer.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _store.Added -= OnStoreChanged;
            _store.Cleared -= OnStoreCleared;
            _ageTimer.Stop();
            _reloadTimer.Stop();
        };
        Reload();
    }

    private static string Str(string key, string english) => LocalizationService.Str(key, english);

    /// <summary>Makes the window's smallest height follow what the filters, the line above the list and the footer need
    /// at the window's width, so the list keeps its own smallest height (its row's MinHeight) and none of them is
    /// pushed out of the window. On an everyday day that is the XAML's MinHeight in every language. In Russian, with a
    /// message, a status of three sentences and the line for a history that is off, they need about 150 px more, and
    /// at the fixed minimum the list had 13 px left, or none. Each of the three is in a row of its own height, so what
    /// it asks for (DesiredSize) doesn't depend on the window's height: this settles in one pass.</summary>
    private void KeepRoomForTheList()
    {
        if (WindowState != WindowState.Normal || Content is not FrameworkElement content || content.ActualHeight <= 0) return;
        // The title bar and the borders: the window's height less the room it gives its content. (Not the content's own
        // height: content that asks for more than there is gets laid out at what it asks for, and cut off.)
        double frame = Math.Max(0, ActualHeight - System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(content).Height);
        double needed = Filters.DesiredSize.Height + OffNotice.DesiredSize.Height + Footer.DesiredSize.Height + ListRow.MinHeight + frame;
        double smallest = Math.Max(_smallestByDesign, Math.Ceiling(needed));
        if (Math.Abs(MinHeight - smallest) >= 1) MinHeight = smallest;           // a window smaller than that grows to it (WPF does that)
    }

    /// <summary>The rows shown now, newest first (tests).</summary>
    public IReadOnlyList<HistoryRowVM> Rows => _rows;

    /// <summary>The alerts behind the rows: what Export writes (tests).</summary>
    public IReadOnlyList<AlertHistoryEntry> Shown => _shown;

    private HistoryRange Range => Enum.Parse<HistoryRange>(((Choice)CmbRange.SelectedItem).Value);

    // ── Loading ──

    /// <summary>Reads the chosen range again, off the UI thread, then shows it. A second call while one is reading
    /// waits for it and reads once more.</summary>
    public void Reload()
    {
        if (_closed) return;
        if (_loading) { _again = true; return; }
        _loading = true;
        var since = AlertHistoryView.SinceUtc(Range, _do.UtcNow().ToLocalTime());
        InBackground(() => _store.Read(since, AlertHistoryView.MaxEntries), page =>
        {
            _loading = false;
            _loaded = page.Entries;
            _more = page.More;
            _badLines = page.BadLines;
            FillFilters();
            Apply();
            if (_again) { _again = false; Reload(); }
        });
    }

    /// <summary>File work runs on a background thread and its result comes back on the UI thread; a closed window
    /// takes no result.</summary>
    private void InBackground<T>(Func<T> work, Action<T> then)
    {
        if (_do.Synchronous) { then(work()); return; }
        Task.Run(work).ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed) return;
            if (task.IsCompletedSuccessfully) then(task.Result);
            else { _loading = false; App.LogError(task.Exception?.GetBaseException() ?? new InvalidOperationException("The alert history could not be read.")); }
        })));
    }

    /// <summary>On the log monitor's thread, once per alert. However many alerts arrive before this window's thread gets
    /// to look, one message is on its way to it, not one per alert: the first alert sends it, and the others see that
    /// it is waiting. The flag is taken down before the timer is looked at, so an alert that arrives later sends again.</summary>
    private void OnStoreChanged(AlertHistoryEntry _)
    {
        if (System.Threading.Interlocked.Exchange(ref _changeWaiting, 1) == 1) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            System.Threading.Volatile.Write(ref _changeWaiting, 0);
            if (!_closed && !_reloadTimer.IsEnabled) _reloadTimer.Start();
        }));
    }

    private void OnStoreCleared() => Dispatcher.BeginInvoke(new Action(Reload));

    /// <summary>Streamer mode (IDEAS #19) was switched on or off: the character list and the rows are drawn again. What is
    /// chosen, searched for and switched to stays the real name.</summary>
    public void RefreshStreamerMask()
    {
        FillFilters();
        Apply();
    }

    // ── Filters ──

    /// <summary>The character and alert lists hold what the loaded range holds; a choice that is still there stays chosen.</summary>
    private void FillFilters()
    {
        _filling = true;
        var character = Chosen(CmbCharacter);
        var alert = Chosen(CmbEvent);
        CmbCharacter.ItemsSource = new[] { new Choice("", Str("L.Hist.AllCharacters", "All characters")) }
            .Concat(AlertHistoryView.Characters(_loaded).Select(n => new Choice(n, StreamerMask.Name(StreamerKind.Character, n)))).ToList();
        CmbEvent.ItemsSource = new[] { new Choice("", Str("L.Hist.AllEvents", "All alerts")) }
            .Concat(AlertHistoryView.Events(_loaded, AlertHistoryView.EventName).Select(e => new Choice(e.Key, e.Name))).ToList();
        Choose(CmbCharacter, character);
        Choose(CmbEvent, alert);
        _filling = false;
    }

    private static string Chosen(System.Windows.Controls.ComboBox box) => (box.SelectedItem as Choice)?.Value ?? "";

    private static void Choose(System.Windows.Controls.ComboBox box, string value)
    {
        var items = (IReadOnlyList<Choice>)box.ItemsSource;
        int at = items.ToList().FindIndex(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = at < 0 ? 0 : at;
    }

    private HistoryFilter Filter => new(Chosen(CmbCharacter), Chosen(CmbEvent), Chosen(CmbSeverity), TxtSearch.Text ?? "");

    /// <summary>Filters what is loaded, folds repeats, and shows the rows. The rows are new objects each time, so what
    /// the player had is put back: the selection stays on its run when the run is still there (also when it grew,
    /// which it does during a fight), the keyboard goes back to that row when it was in the list and the row is in
    /// view, and the message stays. The list is left where the player scrolled it (FocusRow).</summary>
    private void Apply()
    {
        var selected = (AlertList.SelectedItem as HistoryRowVM)?.Row.Newest;
        bool hadKeyboard = AlertList.IsKeyboardFocusWithin;
        var nowUtc = _do.UtcNow();
        var nowLocal = nowUtc.ToLocalTime();
        _shown = AlertHistoryView.Filter(_loaded, Filter, AlertHistoryView.EventName);
        _rows = AlertHistoryView.Collapse(_shown).Select(row =>
        {
            var vm = new HistoryRowVM(row, AlertHistoryView.TimeText(row.Newest.TimeUtc, nowLocal), AlertHistoryView.EventName(row.Newest),
                OutcomeNote(row.Newest.Outcome), Bar(row.Newest), SeverityName(row.Newest.Severity),
                row.Count > 1 ? AlertHistoryView.TimeText(row.OldestUtc, nowLocal) : null);
            vm.Tick(nowUtc);
            return vm;
        }).ToList();
        _applying = true;
        try
        {
            AlertList.ItemsSource = _rows;
            var again = selected == null ? null : _rows.FirstOrDefault(r => AlertHistoryView.Holds(r.Row, selected));
            if (again != null)
            {
                AlertList.SelectedItem = again;
                if (hadKeyboard) FocusRow(again);
            }
        }
        finally { _applying = false; }
        TxtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowStatus();
        ShowWhetherTheHistoryIsOn();
    }

    /// <summary>Puts the keyboard back on a row after a re-read. Replacing the rows takes it off the row that had it
    /// (WPF leaves it on the list itself), and the arrow keys would then start from the top of what the list shows
    /// after every re-read. Only called when the list had the keyboard, so this never takes it from another control or
    /// another program.
    ///
    /// It never scrolls the list. A re-read comes every two seconds while alerts arrive, and a list that went back to
    /// the selected row each time could not be read anywhere else. So a row that isn't in view is left alone (it has
    /// no container): the keyboard stays on the list, and goes to the row at the first re-read that finds it in view,
    /// or when the player presses an arrow or page key (OnListPreviewKeyDown).
    /// And a row in view takes the keyboard without being brought into view, which WPF does for whatever takes the
    /// keyboard: a row cut off at the list's lower edge would otherwise pull the list down with it, a row for every
    /// alert that arrives, and the newest alerts would leave at the top.</summary>
    private void FocusRow(HistoryRowVM row)
    {
        AlertList.UpdateLayout();                               // the new rows have no containers until the list is laid out
        if (AlertList.ItemContainerGenerator.ContainerFromItem(row) is not ListViewItem item) return;
        RequestBringIntoViewEventHandler stay = (_, e) => e.Handled = true;
        item.RequestBringIntoView += stay;
        try { item.Focus(); }
        finally { item.RequestBringIntoView -= stay; }
    }

    private void ShowStatus()
    {
        // The alerts are counted. Where runs are folded there are fewer rows than alerts, and the count says both.
        var parts = new List<string>
        {
            _rows.Count == _shown.Count
                ? string.Format(CultureInfo.CurrentCulture, Str("L.Hist.Count", "Alerts: {0}"), _shown.Count)
                : string.Format(CultureInfo.CurrentCulture, Str("L.Hist.CountRows", "Alerts: {0} in {1} rows"), _shown.Count, _rows.Count),
        };
        if (_more) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("L.Hist.More", "Only the newest {0} of this range are loaded."), AlertHistoryView.MaxEntries));
        if (_badLines > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("L.Hist.BadLines", "{0} lines of the history files could not be read."), _badLines));
        TxtStatus.Text = string.Join("  ", parts);
    }

    /// <summary>The line above the list that says the history is switched off. Looked at with every refresh and with
    /// the ten-second tick, so it follows the setting while the window is open.</summary>
    private void ShowWhetherTheHistoryIsOn()
    {
        bool off = !_do.HistoryOn();
        if (off == (OffNotice.Visibility == Visibility.Visible)) return;
        OffNotice.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        if (off) Announce.Changed(TxtOff);
    }

    private void TickAges()
    {
        var now = _do.UtcNow();
        foreach (var row in _rows) row.Tick(now);
        ShowWhetherTheHistoryIsOn();
    }

    private void Say(string? message)
    {
        TxtMessage.Text = message ?? "";
        TxtMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        // The line is a live region (LiveSetting in the XAML), which by itself announces nothing: a screen reader is
        // told that the text changed, each time something is said.
        if (!string.IsNullOrEmpty(message)) Announce.Changed(TxtMessage);
    }

    /// <summary>The message under the list (tests).</summary>
    public string Message => TxtMessage.Text;

    // ── What a row shows ──

    private static string SeverityName(string severity) =>
        AlertEvents.Severities.Contains(severity) ? Str(AlertEvents.SeverityKey(severity), severity) : severity;

    public static string OutcomeName(AlertOutcome outcome) => outcome switch
    {
        AlertOutcome.NoToast => Str("L.Hist.Outcome.NoToast", "No toast"),
        AlertOutcome.Muted => Str("L.Hist.Outcome.Muted", "Muted"),
        AlertOutcome.NoWindow => Str("L.Hist.Outcome.NoWindow", "No client window"),
        AlertOutcome.Snoozed => Str("L.Hist.Outcome.Snoozed", "Snoozed"),
        _ => Str("L.Hist.Outcome.Shown", "Shown"),
    };

    /// <summary>Said in the row only when nothing of the alert reached the player (AlertHistoryView.HasNote, which the
    /// rows are folded by as well: two alerts with the same note here are one run).</summary>
    private static string OutcomeNote(AlertOutcome outcome) => AlertHistoryView.HasNote(outcome) ? OutcomeName(outcome) : "";

    /// <summary>The colour the alert flashed its thumbnail in (AlertColours.Resolve): the event's own, else its severity's.</summary>
    private Brush Bar(AlertHistoryEntry e)
    {
        var colour = AlertColours.Resolve(_settings, e.EventKey, e.Severity);
        if (!_bars.TryGetValue(colour, out var brush))
        {
            var solid = new SolidColorBrush(ThumbnailManager.ParseColor(colour));
            solid.Freeze();
            _bars[colour] = brush = solid;
        }
        return brush;
    }

    /// <summary>The last column's narrowest width; it takes whatever the window has left.</summary>
    private const double TextColumnMin = 160;

    /// <summary>The seven columns. Built here because a column's header and cell can't take a resource key in XAML the
    /// way a control can. Every cell is plain text (TextBlock.Text): a rule's name and matched text are shown as they
    /// are, never read as markup. A cell that doesn't fit ends in "…" and shows the whole text as its tooltip; the
    /// time of a run of several alerts shows from when to when the run was.</summary>
    private GridView Columns()
    {
        var view = new GridView { AllowsColumnReorder = false };
        view.Columns.Add(TextColumn("L.Hist.Col.Time", "Time", nameof(HistoryRowVM.Time), 132, nameof(HistoryRowVM.TimeTip)));
        view.Columns.Add(TextColumn("L.Hist.Col.Age", "Age", nameof(HistoryRowVM.Age), 76));
        view.Columns.Add(TextColumn("L.Hist.Col.Character", "Character", nameof(HistoryRowVM.CharacterText), 156));
        view.Columns.Add(new GridViewColumn { Header = Str("L.Hist.Col.Event", "Alert"), Width = 200, CellTemplate = EventCell() });
        view.Columns.Add(TextColumn("L.Hist.Col.System", "System", nameof(HistoryRowVM.SystemText), 110));
        view.Columns.Add(TextColumn("L.Hist.Csv.Outcome", "Outcome", nameof(HistoryRowVM.Note), 112));
        var text = TextColumn("L.Hist.Col.Text", "Text", nameof(HistoryRowVM.TextShown), TextColumnMin);
        view.Columns.Add(text);
        // The text column gets the room a wider window gives (the other columns keep the width the player set).
        AlertList.SizeChanged += (_, _) =>
        {
            double others = view.Columns.Where(c => c != text).Sum(c => c.ActualWidth);
            text.Width = Math.Max(TextColumnMin, AlertList.ActualWidth - others - 36);   // 36: the scroll bar and the list's edges
        };
        return view;
    }

    private static GridViewColumn TextColumn(string key, string english, string property, double width, string? tooltip = null)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        text.SetBinding(ToolTipProperty, Tip(tooltip ?? property));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new GridViewColumn { Header = Str(key, english), Width = width, CellTemplate = new DataTemplate { VisualTree = text } };
    }

    /// <summary>A cell's tooltip: the text, and none at all for an empty cell.</summary>
    private static Binding Tip(string property) => new(property) { Converter = NoEmptyTip.Instance };

    /// <summary>An empty text is no tooltip. Left as "" it still opens one: an empty box at the mouse.</summary>
    private sealed class NoEmptyTip : IValueConverter
    {
        public static readonly NoEmptyTip Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string { Length: > 0 } ? value : null;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>A bar in the alert's colour, then its name with "×3" behind it for a run.</summary>
    private static DataTemplate EventCell()
    {
        var bar = new FrameworkElementFactory(typeof(Border));
        bar.SetValue(WidthProperty, 4.0);
        bar.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        bar.SetValue(MarginProperty, new Thickness(0, 1, 8, 1));
        bar.SetBinding(Border.BackgroundProperty, new Binding(nameof(HistoryRowVM.Bar)));
        bar.SetBinding(ToolTipProperty, new Binding(nameof(HistoryRowVM.SeverityName)));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(HistoryRowVM.EventText)));
        name.SetBinding(ToolTipProperty, Tip(nameof(HistoryRowVM.EventText)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.AppendChild(bar);
        row.AppendChild(name);
        return new DataTemplate { VisualTree = row };
    }

    // ── What the buttons do ──

    /// <summary>Brings the selected row's client to the front, the way a click on its toast does; says so when that
    /// client isn't running.</summary>
    public void SwitchSelected()
    {
        if (AlertList.SelectedItem is not HistoryRowVM row) return;
        Say(_do.SwitchTo(row.Character)
            ? null
            : string.Format(CultureInfo.CurrentCulture, Str("L.Hist.NotRunning", "{0}'s client isn't running."), row.CharacterText));
    }

    /// <summary>Writes the alerts behind the rows shown (every one, not one per folded row) to a CSV file the player picks.</summary>
    public void ExportShown()
    {
        var path = _do.PickSaveFile($"alert-history-{_do.UtcNow().ToLocalTime():yyyy-MM-dd}.csv");
        if (path == null) return;
        var entries = _shown;
        var header = new[]
        {
            Str("L.Hist.Csv.TimeUtc", AlertHistoryCsv.Columns[0]), Str("L.Hist.Csv.LocalTime", AlertHistoryCsv.Columns[1]),
            Str("L.Hist.Col.Character", AlertHistoryCsv.Columns[2]), Str("L.Hist.Col.Event", AlertHistoryCsv.Columns[3]),
            Str("L.S.Events.ColSeverity", AlertHistoryCsv.Columns[4]), Str("L.Hist.Col.System", AlertHistoryCsv.Columns[5]),
            Str("L.Hist.Csv.Outcome", AlertHistoryCsv.Columns[6]), Str("L.Hist.Col.Text", AlertHistoryCsv.Columns[7]),
            Str("L.Hist.Csv.Key", AlertHistoryCsv.Columns[8]),
        };
        // The names are worked out here, on the UI thread; only the file is written in the background. Semicolons
        // between the fields where this PC lists with one: Excel there opens a comma file as a single column. A PC
        // that lists with something else gets commas, and the writer looks out for that something inside the fields.
        string listSeparator = _do.ListSeparator();
        var text = AlertHistoryCsv.Text(header, entries, AlertHistoryView.EventName, SeverityName, OutcomeName, TimeZoneInfo.Local,
            AlertHistoryCsv.SeparatorFor(listSeparator), listSeparator);
        InBackground(() =>
        {
            try { AlertHistoryCsv.Write(path, text); return true; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return false; }
        }, written => Say(written
            ? string.Format(CultureInfo.CurrentCulture, Str("L.Hist.Exported", "{0} alerts written to {1}"), entries.Count, System.IO.Path.GetFileName(path))
            : Str("L.Hist.ExportFailed", "The file couldn't be written. Is it open in another program?")));
    }

    /// <summary>Asks, then deletes every day file and empties the list.</summary>
    public void ClearAll()
    {
        if (!_do.Confirm(Str("L.Hist.ClearTitle", "Clear the alert history?"),
                Str("L.Hist.ClearBody", "Every alert kept so far is deleted from this PC. This can't be undone."))) return;
        InBackground(() => _store.Clear(TimeSpan.FromSeconds(10)), cleared =>
        {
            Say(cleared ? Str("L.Hist.Cleared", "The alert history was cleared.") : Str("L.Hist.ClearFailed", "The history couldn't be cleared just now. Try again."));
            Reload();
        });
    }

    private void OnSwitch(object sender, RoutedEventArgs e) => SwitchSelected();
    private void OnExport(object sender, RoutedEventArgs e) => ExportShown();
    private void OnClear(object sender, RoutedEventArgs e) => ClearAll();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e) { if (!_filling) Reload(); }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (!_filling) Apply(); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (!_filling) Apply(); }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnSwitch.IsEnabled = AlertList.SelectedItem != null;
        // The player chose another row: what was said about the last one goes. Not when a refresh puts the selection back.
        if (!_applying && e.AddedItems.Count > 0) Say(null);
    }

    /// <summary>A double-click with the left button on a row switches; one with another button, or on a column header,
    /// the scroll bar or the empty part of the list, does nothing.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && IsInsideARow(e.OriginalSource as DependencyObject, AlertList)) SwitchSelected();
    }

    /// <summary>Enter in the list switches to the selected row's client, as a double-click does. Only in the list: the
    /// Switch button is no default button, so Enter in the search box or on a filter list changes no client.</summary>
    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SwitchSelected();
        e.Handled = true;
    }

    /// <summary>An arrow key or a page key while the list itself has the keyboard and a row is chosen. That is how a
    /// re-read leaves things when the chosen row is out of view (FocusRow), and from there WPF would start the key at
    /// the top: the first row would be chosen, and the list would jump to it. So the chosen row is brought into view
    /// and takes the keyboard first, and the key then does its work from that row (it is not marked as handled).
    /// This is the player's own key: it may scroll the list, where a re-read never does. A row that is out of view
    /// has no container until the list has gone to it and is laid out.</summary>
    private void OnListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.PageUp or Key.PageDown)) return;
        if (!AlertList.IsKeyboardFocused || AlertList.SelectedItem is not { } chosen) return;
        AlertList.ScrollIntoView(chosen);
        AlertList.UpdateLayout();
        (AlertList.ItemContainerGenerator.ContainerFromItem(chosen) as ListViewItem)?.Focus();
    }

    /// <summary>Whether an element sits inside one of the list's rows.</summary>
    public static bool IsInsideARow(DependencyObject? element, DependencyObject list)
    {
        for (var node = element; node != null && node != list; node = Parent(node))
            if (node is ListViewItem) return true;
        return false;

        static DependencyObject? Parent(DependencyObject node) =>
            node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    }
}
