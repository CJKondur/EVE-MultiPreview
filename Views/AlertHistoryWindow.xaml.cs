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
    /// commas (AlertHistoryCsv.SeparatorFor). App leaves it as it is; tests hand one in.</summary>
    public Func<string> ListSeparator { get; init; } = () => CultureInfo.CurrentCulture.TextInfo.ListSeparator;
    /// <summary>Tests: file work runs at once, on the calling thread, instead of on a background thread.</summary>
    public bool Synchronous { get; init; }
}

/// <summary>One row as the list shows it.</summary>
public sealed class HistoryRowVM : INotifyPropertyChanged
{
    private string _age = "";

    public HistoryRowVM(HistoryRow row, string time, string eventName, string note, Brush bar, string severityName)
    {
        Row = row;
        Time = time;
        Event = eventName;
        Note = note;
        Bar = bar;
        SeverityName = severityName;
    }

    public HistoryRow Row { get; }
    public string Time { get; }
    public string Character => Row.Newest.Character;
    public string Event { get; }
    /// <summary>"×3" for a run of three, "" for one alert.</summary>
    public string Count => Row.Count > 1 ? "×" + Row.Count.ToString(CultureInfo.CurrentCulture) : "";
    /// <summary>What the Alert column shows: the name, and the count behind it for a run.</summary>
    public string EventText => Count.Length > 0 ? Event + "  " + Count : Event;
    /// <summary>What became of the alert when the player got nothing of it ("Muted", "No client window"), else "".</summary>
    public string Note { get; }
    public string SystemName => Row.Newest.SolarSystem;
    public string Text => Row.Newest.Text;
    public Brush Bar { get; }
    public string SeverityName { get; }
    public string Age { get => _age; private set { if (_age != value) { _age = value; Raise(nameof(Age)); Raise(nameof(ReadOut)); } } }

    /// <summary>The row in one sentence, for a screen reader.</summary>
    public string ReadOut => string.Join(", ", new[] { Time, Age, Character, Event, Count, Note, SeverityName, SystemName, Text }.Where(p => p.Length > 0));

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
    private int _badLines;

    public AlertHistoryWindow(AlertHistory store, AppSettings settings, AlertHistoryActions actions)
    {
        InitializeComponent();
        FontFamily = UiFont.For(LocalizationService.CurrentLanguage);
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

    private void OnStoreChanged(AlertHistoryEntry _) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!_closed && !_reloadTimer.IsEnabled) _reloadTimer.Start();
    }));

    private void OnStoreCleared() => Dispatcher.BeginInvoke(new Action(Reload));

    // ── Filters ──

    /// <summary>The character and alert lists hold what the loaded range holds; a choice that is still there stays chosen.</summary>
    private void FillFilters()
    {
        _filling = true;
        var character = Chosen(CmbCharacter);
        var alert = Chosen(CmbEvent);
        CmbCharacter.ItemsSource = new[] { new Choice("", Str("L.Hist.AllCharacters", "All characters")) }
            .Concat(AlertHistoryView.Characters(_loaded).Select(n => new Choice(n, n))).ToList();
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

    /// <summary>Filters what is loaded, folds repeats, and shows the rows. The selected row stays selected when it is
    /// still there.</summary>
    private void Apply()
    {
        var selected = (AlertList.SelectedItem as HistoryRowVM)?.Row.Newest;
        var nowUtc = _do.UtcNow();
        var nowLocal = nowUtc.ToLocalTime();
        _shown = AlertHistoryView.Filter(_loaded, Filter, AlertHistoryView.EventName);
        _rows = AlertHistoryView.Collapse(_shown).Select(row =>
        {
            var vm = new HistoryRowVM(row, AlertHistoryView.TimeText(row.Newest.TimeUtc, nowLocal), AlertHistoryView.EventName(row.Newest),
                OutcomeNote(row.Newest.Outcome), Bar(row.Newest), SeverityName(row.Newest.Severity));
            vm.Tick(nowUtc);
            return vm;
        }).ToList();
        AlertList.ItemsSource = _rows;
        if (selected != null) AlertList.SelectedItem = _rows.FirstOrDefault(r => SameAlert(r.Row.Newest, selected));
        TxtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowStatus();
    }

    private void ShowStatus()
    {
        var parts = new List<string> { string.Format(CultureInfo.CurrentCulture, Str("L.Hist.Count", "Alerts: {0}"), _shown.Count) };
        if (_more) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("L.Hist.More", "Only the newest {0} of this range are loaded."), AlertHistoryView.MaxEntries));
        if (_badLines > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("L.Hist.BadLines", "{0} lines of the history files could not be read."), _badLines));
        TxtStatus.Text = string.Join("  ", parts);
    }

    /// <summary>The same alert before and after a re-read (a re-read makes new objects).</summary>
    private static bool SameAlert(AlertHistoryEntry a, AlertHistoryEntry b) =>
        a.TimeUtc == b.TimeUtc && a.Character == b.Character && a.EventKey == b.EventKey;

    private void TickAges()
    {
        var now = _do.UtcNow();
        foreach (var row in _rows) row.Tick(now);
    }

    private void Say(string? message)
    {
        TxtMessage.Text = message ?? "";
        TxtMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
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
        _ => Str("L.Hist.Outcome.Shown", "Shown"),
    };

    /// <summary>Said in the row only when nothing of the alert reached the player. An alert that was shown needs no
    /// note, and "no toast" would stand in every row of a player who doesn't use the hub.</summary>
    private static string OutcomeNote(AlertOutcome outcome) =>
        outcome is AlertOutcome.Muted or AlertOutcome.NoWindow ? OutcomeName(outcome) : "";

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
    /// are, never read as markup. A cell that doesn't fit ends in "…" and shows the whole text as its tooltip.</summary>
    private GridView Columns()
    {
        var view = new GridView { AllowsColumnReorder = false };
        view.Columns.Add(TextColumn("L.Hist.Col.Time", "Time", nameof(HistoryRowVM.Time), 132));
        view.Columns.Add(TextColumn("L.Hist.Col.Age", "Age", nameof(HistoryRowVM.Age), 76));
        view.Columns.Add(TextColumn("L.Hist.Col.Character", "Character", nameof(HistoryRowVM.Character), 156));
        view.Columns.Add(new GridViewColumn { Header = Str("L.Hist.Col.Event", "Alert"), Width = 200, CellTemplate = EventCell() });
        view.Columns.Add(TextColumn("L.Hist.Col.System", "System", nameof(HistoryRowVM.SystemName), 110));
        view.Columns.Add(TextColumn("L.Hist.Csv.Outcome", "Outcome", nameof(HistoryRowVM.Note), 112));
        var text = TextColumn("L.Hist.Col.Text", "Matched text", nameof(HistoryRowVM.Text), TextColumnMin);
        view.Columns.Add(text);
        // The matched text gets the room a wider window gives (the other columns keep the width the player set).
        AlertList.SizeChanged += (_, _) =>
        {
            double others = view.Columns.Where(c => c != text).Sum(c => c.ActualWidth);
            text.Width = Math.Max(TextColumnMin, AlertList.ActualWidth - others - 36);   // 36: the scroll bar and the list's edges
        };
        return view;
    }

    private static GridViewColumn TextColumn(string key, string english, string property, double width)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        text.SetBinding(ToolTipProperty, new Binding(property));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new GridViewColumn { Header = Str(key, english), Width = width, CellTemplate = new DataTemplate { VisualTree = text } };
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
        name.SetBinding(ToolTipProperty, new Binding(nameof(HistoryRowVM.EventText)));
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
            : string.Format(CultureInfo.CurrentCulture, Str("L.Hist.NotRunning", "{0}'s client isn't running."), row.Character));
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
        // between the fields where this PC lists with one: Excel there opens a comma file as a single column.
        var text = AlertHistoryCsv.Text(header, entries, AlertHistoryView.EventName, SeverityName, OutcomeName, TimeZoneInfo.Local,
            AlertHistoryCsv.SeparatorFor(_do.ListSeparator()));
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
        if (e.AddedItems.Count > 0) Say(null);
    }

    /// <summary>A double-click on a row switches; one on a column header, the scroll bar or the empty part of the
    /// list does nothing.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideARow(e.OriginalSource as DependencyObject, AlertList)) SwitchSelected();
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
