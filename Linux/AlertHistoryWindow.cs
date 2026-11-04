using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;
using EveMultiPreview.Views;

namespace EveMultiPreview.Linux;

/// <summary>What the history window asks of the app.</summary>
/// <param name="SwitchTo">Brings that character's client to the front. False when the client isn't running.</param>
/// <param name="HistoryOn">Whether "Keep an alert history" is on. While it is off the window says so above the list.</param>
/// <param name="SettingsLoaded">Whether the settings file was read at startup. When it was not, nothing is recorded.</param>
/// <param name="Settings">The settings as they are now (the colour an alert flashed in is read from them).</param>
public sealed record AlertHistoryActions(Func<string, bool> SwitchTo, Func<bool> HistoryOn, Func<bool> SettingsLoaded, Func<AppSettings> Settings);

/// <summary>One row as the list shows it (the Windows window's HistoryRowVM).</summary>
public sealed class HistoryRowVM : INotifyPropertyChanged
{
    private string _age = "";

    public HistoryRowVM(HistoryRow row, string time, string since, string eventName, string note, IBrush bar, string severityName)
    {
        Row = row;
        Time = time;
        TimeTip = row.Count > 1 ? since + " – " + time : time;
        Event = eventName;
        Note = note;
        Bar = bar;
        SeverityName = severityName;
    }

    public HistoryRow Row { get; }
    public string Time { get; }
    /// <summary>The time cell's tooltip: the time, or for a run the time of its first alert and of its last.</summary>
    public string TimeTip { get; }
    /// <summary>The character as the history kept it: the key the switch and the filters work by (never masked).</summary>
    public string Character => Row.Newest.Character;
    public string CharacterText => StreamerMask.Name(StreamerKind.Character, Character);
    public string Event { get; }
    public string Count => Row.Count > 1 ? "×" + Row.Count.ToString(CultureInfo.CurrentCulture) : "";
    public string EventText => Count.Length > 0 ? Event + "  " + Count : Event;
    /// <summary>What became of the alert when the player got nothing of it ("Muted", "No client window"), else "".</summary>
    public string Note { get; }
    public string SystemText => StreamerMask.Name(StreamerKind.System, Row.Newest.SolarSystem);
    public string TextShown => StreamerMask.Name(StreamerKind.Label, Row.Newest.Text);
    public IBrush Bar { get; }
    public string SeverityName { get; }
    public string Age { get => _age; private set { if (_age != value) { _age = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Age))); } } }

    public void Tick(DateTime utcNow) => Age = AlertAge.Text(utcNow - Row.Newest.TimeUtc);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The alert history (IDEAS #1), as the Windows app's window has it: every alert that was kept, newest first,
/// with the range, character, alert, severity and search filters, a jump to the client, a CSV export and Clear. It
/// reads the day files on a background thread (AlertHistory.Read), never writes one itself, and shows a new alert
/// within two seconds. Streamer mode masks names and systems.</summary>
public sealed class AlertHistoryWindow : Window
{
    private sealed record Choice(string Value, string Text);

    private static readonly double[] ColumnWidths = { 132, 76, 156, 200, 110, 112 };   // the Windows columns; the text column takes the rest
    private readonly AlertHistory _store;
    private readonly AlertHistoryActions _do;
    private readonly ComboBox _range = new() { MinWidth = 150 }, _character = new() { MinWidth = 170 },
        _event = new() { MinWidth = 170 }, _severity = new() { MinWidth = 130 };
    private readonly TextBox _search = new() { Width = 190 };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _message = new() { FontSize = 13, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { Opacity = 0.65, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Border _offNotice = new() { IsVisible = false, Margin = new Thickness(20, 0, 20, 8), Padding = new Thickness(12, 8), BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Brushes.Orange, Background = new SolidColorBrush(Color.Parse("#14FFFFFF")) };
    private readonly TextBlock _offText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Button _switch = new() { IsEnabled = false, Padding = new Thickness(16, 6) };
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, IBrush> _bars = new(StringComparer.OrdinalIgnoreCase);
    private List<Choice> _ranges = new(), _characters = new(), _events = new(), _severities = new();
    private IReadOnlyList<AlertHistoryEntry> _loaded = Array.Empty<AlertHistoryEntry>();
    private IReadOnlyList<AlertHistoryEntry> _shown = Array.Empty<AlertHistoryEntry>();
    private List<HistoryRowVM> _rows = new();
    private bool _more, _loading, _again, _closed, _filling = true, _applying;
    private int _badLines, _unreadableDays, _changeWaiting;
    private bool _streamer;

    public AlertHistoryWindow(AlertHistory store, AlertHistoryActions actions)
    {
        _store = store;
        _do = actions;
        RequestedThemeVariant = ThemeVariant.Dark;
        Width = 1040;
        Height = 640;
        MinWidth = 760;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "EVE MultiPreview - " + Str("L.Hist.Title", "Alert history");
        using (var icon = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Icon.ico"))
            if (icon != null) Icon = new WindowIcon(icon);

        _ranges = new()
        {
            new(nameof(HistoryRange.Today), Str("L.Hist.Range.Today", "Today")),
            new(nameof(HistoryRange.Week), Str("L.Hist.Range.Week", "Last 7 days")),
            new(nameof(HistoryRange.All), Str("L.Hist.Range.All", "Everything kept")),
        };
        foreach (var c in _ranges) _range.Items.Add(c.Text);
        _range.SelectedIndex = 0;
        _severities = new List<Choice> { new("", Str("L.Hist.AllSeverities", "All severities")) }
            .Concat(AlertEvents.Severities.Select(s => new Choice(s, SeverityName(s)))).ToList();
        foreach (var c in _severities) _severity.Items.Add(c.Text);
        _severity.SelectedIndex = 0;
        FillFilters();

        var filters = new WrapPanel { Margin = new Thickness(20, 16, 20, 4) };
        filters.Children.Add(Filter(Str("L.Hist.Range", "Show"), _range));
        filters.Children.Add(Filter(Str("L.Hist.Col.Character", "Character"), _character));
        filters.Children.Add(Filter(Str("L.Hist.Col.Event", "Alert"), _event));
        filters.Children.Add(Filter(Str("L.S.Events.ColSeverity", "Severity"), _severity));
        filters.Children.Add(Filter(Str("L.Hist.Search", "Search"), _search));
        _range.SelectionChanged += (_, _) => { if (!_filling && _range.SelectedIndex >= 0) Reload(); };
        foreach (var box in new[] { _character, _event, _severity })
            box.SelectionChanged += (_, _) => { if (!_filling && box.SelectedIndex >= 0) Apply(); };
        _search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_filling) Apply(); };

        _offNotice.Child = _offText;
        _empty.Text = Str("L.Hist.Empty", "No alerts kept for this range.");

        // The list: a header row and the rows share the column widths; the last column (text) takes what is left.
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 3)) } });
        _list.ItemTemplate = new FuncDataTemplate<HistoryRowVM>((vm, _) => vm == null ? new Panel() : RowOf(vm));
        _list.SelectionChanged += (_, _) =>
        {
            _switch.IsEnabled = _list.SelectedItem != null;
            if (!_applying && _list.SelectedItem != null) Say(null);   // the player chose another row: what was said about the last goes
        };
        _list.DoubleTapped += (_, e) => { if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(true) != null) SwitchSelected(); };
        // Enter in the list switches as a double-click does. Tunnelled: the list's own key handling sees the key first otherwise.
        _list.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SwitchSelected(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var listArea = new Grid { Margin = new Thickness(20, 4, 20, 16), RowDefinitions = new RowDefinitions("Auto,*") };
        var header = HeaderRow();
        listArea.Children.Add(header);
        var listHost = new Grid();
        Grid.SetRow(listHost, 1);
        listHost.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF")), Child = _list });
        listHost.Children.Add(_empty);
        listArea.Children.Add(listHost);

        // Footer: what was done last and the count on the left; the buttons on the right (Export, Clear, Switch, Close).
        var export = new Button { Content = Str("L.Hist.Export", "Export CSV…"), Padding = new Thickness(16, 6) };
        var clear = new Button { Content = Str("L.Hist.Clear", "Clear history…"), Padding = new Thickness(16, 6) };
        var close = new Button { Content = Str("L.Hist.Close", "Close"), Padding = new Thickness(16, 6) };
        _switch.Content = Str("L.Hist.Switch", "Switch to client");
        export.Click += (_, _) => ExportShown();
        clear.Click += (_, _) => ClearAll();
        _switch.Click += (_, _) => SwitchSelected();
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new[] { export, clear, _switch, close }) buttons.Children.Add(b);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        texts.Children.Add(_message);
        texts.Children.Add(_status);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(texts);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        var footerBox = new Border
        {
            Padding = new Thickness(20, 12), BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#22FFFFFF")), Background = new SolidColorBrush(Color.Parse("#14FFFFFF")), Child = footer,
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(filters);
        Grid.SetRow(_offNotice, 1);
        root.Children.Add(_offNotice);
        Grid.SetRow(listArea, 2);
        root.Children.Add(listArea);
        Grid.SetRow(footerBox, 3);
        root.Children.Add(footerBox);
        Content = root;

        // A new alert shows within two seconds, however many arrive: one re-read, never one per alert.
        _store.Added += OnStoreChanged;
        _store.Cleared += OnStoreCleared;
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); Reload(); };
        _streamer = StreamerMask.IsOn;
        _ageTimer.Tick += (_, _) => TickAges();
        _ageTimer.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _store.Added -= OnStoreChanged;
            _store.Cleared -= OnStoreCleared;
            _ageTimer.Stop();
            _reloadTimer.Stop();
            _store.ForgetReads();   // IDEAS P70: the parsed days are the window's, not the session's
        };
        Reload();
    }

    private static string Str(string key, string english) => LocalizationService.Str(key, english);

    private static StackPanel Filter(string label, Control box)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 8) };
        panel.Children.Add(new TextBlock { Text = label, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        panel.Children.Add(box);
        return panel;
    }

    private static Grid Columns()
    {
        var grid = new Grid();
        foreach (var w in ColumnWidths) grid.ColumnDefinitions.Add(new ColumnDefinition(w, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 160 });
        return grid;
    }

    private static Control HeaderRow()
    {
        var grid = Columns();
        grid.Margin = new Thickness(7, 0, 0, 4);   // the list's border and an item's padding
        string[] names =
        {
            Str("L.Hist.Col.Time", "Time"), Str("L.Hist.Col.Age", "Age"), Str("L.Hist.Col.Character", "Character"), Str("L.Hist.Col.Event", "Alert"),
            Str("L.Hist.Col.System", "System"), Str("L.Hist.Csv.Outcome", "Outcome"), Str("L.Hist.Col.Text", "Text"),
        };
        for (int i = 0; i < names.Length; i++)
        {
            var cell = new TextBlock { Text = names[i], FontWeight = FontWeight.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>A row: every cell is plain text (a rule's name and matched text are shown as they are, never read as markup);
    /// a cell that doesn't fit ends in "…" and shows the whole text as its tooltip. The event cell starts with a bar in the
    /// alert's colour.</summary>
    private static Control RowOf(HistoryRowVM vm)
    {
        var grid = Columns();
        TextBlock Cell(string text, int column, string? tip = null)
        {
            var cell = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if ((tip ?? text).Length > 0) ToolTip.SetTip(cell, tip ?? text);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
            return cell;
        }
        Cell(vm.Time, 0, vm.TimeTip);
        var age = Cell(vm.Age, 1);
        age.Bind(TextBlock.TextProperty, new Binding(nameof(HistoryRowVM.Age)));
        Cell(vm.CharacterText, 2);
        var eventCell = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 8, 1), Background = vm.Bar };
        ToolTip.SetTip(bar, vm.SeverityName);
        eventCell.Children.Add(bar);
        var name = new TextBlock { Text = vm.EventText, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(name, vm.EventText);
        eventCell.Children.Add(name);
        Grid.SetColumn(eventCell, 3);
        grid.Children.Add(eventCell);
        Cell(vm.SystemText, 4);
        Cell(vm.Note, 5);
        Cell(vm.TextShown, 6);
        return grid;
    }

    /// <summary>The rows shown now, newest first (tests).</summary>
    public IReadOnlyList<HistoryRowVM> Rows => _rows;

    private HistoryRange Range => Enum.Parse<HistoryRange>(_ranges[Math.Max(0, _range.SelectedIndex)].Value);

    // ── Loading ──

    /// <summary>Reads the chosen range again, off the UI thread, then shows it. A second call while one is reading waits for it and reads once more.</summary>
    public void Reload()
    {
        if (_closed) return;
        if (_loading) { _again = true; return; }
        _loading = true;
        var since = AlertHistoryView.SinceUtc(Range, DateTime.Now);
        InBackground(() => _store.Read(since, AlertHistoryView.MaxEntries), page =>
        {
            _loading = false;
            _loaded = page.Entries;
            _more = page.More;
            _badLines = page.BadLines;
            _unreadableDays = page.UnreadableDays;
            FillFilters();
            Apply();
            if (_again) { _again = false; Reload(); }
        });
    }

    /// <summary>File work runs on a background thread and its result comes back on the UI thread; a closed window takes no result.</summary>
    private void InBackground<T>(Func<T> work, Action<T> then) =>
        Task.Run(work).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            if (task.IsCompletedSuccessfully) then(task.Result);
            else
            {
                _loading = false;
                ErrorLog.Shared.Record(AlertWays.Failure("Alert history window", task.Exception?.GetBaseException() ?? new InvalidOperationException("The alert history could not be read.")), "App", DateTime.Now);
            }
        }));

    /// <summary>On the log monitor's thread, once per alert. However many alerts arrive before the UI thread gets to look,
    /// one message is on its way to it: the first alert sends it, the others see that it is waiting.</summary>
    private void OnStoreChanged(AlertHistoryEntry _)
    {
        if (Interlocked.Exchange(ref _changeWaiting, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _changeWaiting, 0);
            if (!_closed && !_reloadTimer.IsEnabled) _reloadTimer.Start();
        });
    }

    private void OnStoreCleared() => Dispatcher.UIThread.Post(Reload);

    // ── Filters ──

    /// <summary>The character and alert lists hold what the loaded range holds; a choice that is still there stays chosen.</summary>
    private void FillFilters()
    {
        _filling = true;
        try
        {
            string character = Chosen(_character, _characters), alert = Chosen(_event, _events);
            _characters = new List<Choice> { new("", Str("L.Hist.AllCharacters", "All characters")) }
                .Concat(AlertHistoryView.Characters(_loaded).Select(n => new Choice(n, StreamerMask.Name(StreamerKind.Character, n)))).ToList();
            _events = new List<Choice> { new("", Str("L.Hist.AllEvents", "All alerts")) }
                .Concat(AlertHistoryView.Events(_loaded, AlertHistoryView.EventName).Select(e => new Choice(e.Key, e.Name))).ToList();
            Refill(_character, _characters, character);
            Refill(_event, _events, alert);
        }
        finally { _filling = false; }
    }

    private static string Chosen(ComboBox box, List<Choice> choices) =>
        box.SelectedIndex >= 0 && box.SelectedIndex < choices.Count ? choices[box.SelectedIndex].Value : "";

    private static void Refill(ComboBox box, List<Choice> choices, string value)
    {
        box.Items.Clear();
        foreach (var c in choices) box.Items.Add(c.Text);
        int at = choices.FindIndex(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
        box.SelectedIndex = at < 0 ? 0 : at;
    }

    private HistoryFilter CurrentFilter => new(Chosen(_character, _characters), Chosen(_event, _events), Chosen(_severity, _severities), _search.Text ?? "");

    /// <summary>Filters what is loaded, folds repeats, and shows the rows. The rows are new objects each time, so the
    /// selection is put back on its run when the run is still there (also when it grew, which it does during a fight).</summary>
    private void Apply()
    {
        var selected = (_list.SelectedItem as HistoryRowVM)?.Row.Newest;
        var nowUtc = DateTime.UtcNow;
        var nowLocal = nowUtc.ToLocalTime();
        _shown = AlertHistoryView.Filter(_loaded, CurrentFilter, AlertHistoryView.EventName);
        _rows = AlertHistoryView.Collapse(_shown).Select(row =>
        {
            var vm = new HistoryRowVM(row, AlertHistoryView.TimeText(row.Newest.TimeUtc, nowLocal), AlertHistoryView.TimeText(row.OldestUtc, nowLocal),
                AlertHistoryView.EventName(row.Newest), OutcomeNote(row.Newest), Bar(row.Newest), SeverityName(row.Newest.Severity));
            vm.Tick(nowUtc);
            return vm;
        }).ToList();
        _applying = true;
        try
        {
            _list.ItemsSource = _rows;
            var again = selected == null ? null : _rows.FirstOrDefault(r => AlertHistoryView.Holds(r.Row, selected));
            if (again != null) _list.SelectedItem = again;
        }
        finally { _applying = false; }
        _empty.IsVisible = _rows.Count == 0;
        ShowStatus();
        ShowWhetherTheHistoryIsOn();
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
        if (_unreadableDays > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, Str("L.Hist.UnreadDays", "{0} day(s) could not be read."), _unreadableDays));
        _status.Text = string.Join("  ", parts);
    }

    /// <summary>The line above the list that says the history is switched off (or that nothing is recorded because the
    /// settings could not be read). Looked at with every refresh and every tick, so it follows the setting while the window is open.</summary>
    private void ShowWhetherTheHistoryIsOn()
    {
        bool loaded = _do.SettingsLoaded();
        bool off = !loaded || !_do.HistoryOn();
        _offNotice.IsVisible = off;
        if (off)
            _offText.Text = loaded
                ? Str("L.Linux.HistOff", "The alert history is switched off, so new alerts are not kept. Switch it on under Settings › Alerts › History.")
                : Str("L.Hist.NotLoaded", "Nothing is recorded in this session: the settings file could not be read.");
    }

    /// <summary>Every second: the ages, the history-off line, and a change of streamer mode (names and systems are drawn again;
    /// what is chosen, searched for and switched to stays the real name).</summary>
    private void TickAges()
    {
        var now = DateTime.UtcNow;
        foreach (var row in _rows) row.Tick(now);
        ShowWhetherTheHistoryIsOn();
        if (StreamerMask.IsOn != _streamer)
        {
            _streamer = StreamerMask.IsOn;
            FillFilters();
            Apply();
        }
    }

    private void Say(string? text)
    {
        _message.Text = text ?? "";
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    /// <summary>The message above the status (tests).</summary>
    public string Message => _message.Text ?? "";

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

    /// <summary>Said in the row only when nothing of the alert reached the player (muted, no client window, snoozed), or when
    /// do-not-disturb held its sound.</summary>
    private static string OutcomeNote(AlertHistoryEntry alert) =>
        alert.Outcome is AlertOutcome.Muted or AlertOutcome.NoWindow or AlertOutcome.Snoozed ? OutcomeName(alert.Outcome)
        : alert.SoundHeld ? Str("L.Hist.SoundHeld", "Sound held") : "";

    /// <summary>The colour the alert flashed its thumbnail in (AlertColours.Resolve): the event's own, else its severity's.</summary>
    private IBrush Bar(AlertHistoryEntry e)
    {
        var colour = AlertColours.Resolve(_do.Settings(), e.EventKey, e.Severity);
        if (!_bars.TryGetValue(colour, out var brush))
        {
            var c = AppSettings.ParseColor(colour, System.Windows.Media.Color.FromRgb(255, 255, 255));
            _bars[colour] = brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        }
        return brush;
    }

    // ── What the buttons do ──

    /// <summary>Brings the selected row's client to the front; says so when that client isn't running.</summary>
    public void SwitchSelected()
    {
        if (_list.SelectedItem is not HistoryRowVM row) return;
        Say(_do.SwitchTo(row.Character)
            ? null
            : string.Format(CultureInfo.CurrentCulture, Str("L.Hist.NotRunning", "{0}'s client isn't running."), row.CharacterText));
    }

    /// <summary>Writes the alerts behind the rows shown (every one, not one per folded row) to a CSV file the player picks.
    /// Where this desktop has no file dialog the file goes to the settings folder and the message says where.</summary>
    public async void ExportShown()
    {
        try { await ExportCore(); }
        catch (Exception ex)
        {
            ErrorLog.Shared.Record(AlertWays.Failure("Alert history: export", ex), "App", DateTime.Now);
            Say(Str("L.Hist.ExportFailed", "The file couldn't be written. Is it open in another program?"));
        }
    }

    private async Task ExportCore()
    {
        var entries = _shown;
        string name = $"alert-history-{DateTime.Now:yyyy-MM-dd}.csv";
        var header = new[]
        {
            Str("L.Hist.Csv.TimeUtc", AlertHistoryCsv.Columns[0]), Str("L.Hist.Csv.LocalTime", AlertHistoryCsv.Columns[1]),
            Str("L.Hist.Col.Character", AlertHistoryCsv.Columns[2]), Str("L.Hist.Col.Event", AlertHistoryCsv.Columns[3]),
            Str("L.S.Events.ColSeverity", AlertHistoryCsv.Columns[4]), Str("L.Hist.Col.System", AlertHistoryCsv.Columns[5]),
            Str("L.Hist.Csv.Outcome", AlertHistoryCsv.Columns[6]), Str("L.Hist.Col.Text", AlertHistoryCsv.Columns[7]),
            Str("L.Hist.Csv.Key", AlertHistoryCsv.Columns[8]), Str("L.Hist.Csv.SoundHeld", AlertHistoryCsv.Columns[9]),
        };
        // Semicolons between the fields where this system lists with one (Excel there opens a comma file as one column).
        string listSeparator = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
        var text = AlertHistoryCsv.Text(header, entries, AlertHistoryView.EventName, SeverityName, OutcomeName, TimeZoneInfo.Local,
            AlertHistoryCsv.SeparatorFor(listSeparator), listSeparator, Str("L.Hist.Csv.Yes", "Yes"));

        string? where = null;      // what the message names
        bool noDialog = false, cancelled = false;
        try
        {
            // EVEMP_NO_FILE_DIALOG=1: behave as on a desktop without a file dialog (the tests use it).
            if (Environment.GetEnvironmentVariable("EVEMP_NO_FILE_DIALOG") == "1") throw new NotSupportedException();
            var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Str("L.Hist.Export", "Export CSV…").TrimEnd('…'),
                SuggestedFileName = name,
                DefaultExtension = "csv",
                FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
            });
            if (picked == null) cancelled = true;
            else if (picked.TryGetLocalPath() is { } path)
            {
                await Task.Run(() => AlertHistoryCsv.Write(path, text));
                where = Path.GetFileName(path);
            }
            else
            {
                // A sandboxed portal hands out a stream, not a path.
                await using var stream = await picked.OpenWriteAsync();
                var bytes = AlertHistoryCsv.FileEncoding.GetPreamble().Concat(AlertHistoryCsv.FileEncoding.GetBytes(text)).ToArray();
                await stream.WriteAsync(bytes);
                where = picked.Name;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or PlatformNotSupportedException)
        {
            noDialog = true;   // no portal or file chooser on this desktop
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Say(Str("L.Hist.ExportFailed", "The file couldn't be written. Is it open in another program?"));
            return;
        }
        if (cancelled) return;
        if (noDialog)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ConfigDir);
                string path = Path.Combine(AppPaths.ConfigDir, name);
                await Task.Run(() => AlertHistoryCsv.Write(path, text));
                where = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Say(Str("L.Hist.ExportFailed", "The file couldn't be written. Is it open in another program?"));
                return;
            }
        }
        Say(string.Format(CultureInfo.CurrentCulture, Str("L.Hist.Exported", "{0} alerts written to {1}"), entries.Count, where)
            + (noDialog ? "  " + Str("L.Linux.HistNoDialog", "This desktop has no file dialog, so the file was saved in the settings folder.") : ""));
    }

    /// <summary>Asks, then deletes every day file and empties the list.</summary>
    public async void ClearAll()
    {
        if (!await Confirm(Str("L.Hist.ClearTitle", "Clear the alert history?"),
                Str("L.Hist.ClearBody", "Every alert kept so far is deleted from this PC. This can't be undone."))) return;
        InBackground(() => _store.Clear(TimeSpan.FromSeconds(10)), cleared =>
        {
            Say(cleared ? Str("L.Hist.Cleared", "The alert history was cleared.") : Str("L.Hist.ClearFailed", "The history couldn't be cleared just now. Try again."));
            Reload();
        });
    }

    /// <summary>A yes/no question in a small window over this one. True for the first button.</summary>
    private async Task<bool> Confirm(string title, string body)
    {
        var dialog = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ThemeVariant.Dark, ShowInTaskbar = false,
        };
        var yes = new Button { Content = Str("L.Common.DeleteWord", "Delete"), Padding = new Thickness(16, 6) };
        var no = new Button { Content = Str("L.Common.Cancel", "Cancel"), Padding = new Thickness(16, 6), IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(yes);
        row.Children.Add(no);
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(row);
        dialog.Content = panel;
        return await dialog.ShowDialog<bool>(this);
    }
}
