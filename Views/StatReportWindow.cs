using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using ListView = System.Windows.Controls.ListView;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using SelectionMode = System.Windows.Controls.SelectionMode;

namespace EveMultiPreview.Views;

/// <summary>What the session report window asks of the app. App wires it; tests hand in a temp folder.</summary>
public sealed class StatReportActions
{
    /// <summary>The folder the stat logs are in (Tools › Stats, StatLogDirectory), asked at every read.</summary>
    public Func<string> Folder { get; init; } = () => "";
    /// <summary>The local date today.</summary>
    public Func<DateOnly> Today { get; init; } = () => DateOnly.FromDateTime(DateTime.Now);
    /// <summary>Tests: the folder is read at once, on the calling thread, instead of on a background thread.</summary>
    public bool Synchronous { get; init; }
}

/// <summary>Session reports (IDEAS #20): what the stat logs say, per character and day. The characters on the left, the
/// days of the chosen one as a table on the right, and under the chooser of a number a bar for each of the last 30 days
/// (WPF shapes, no chart library). It only reads the stat log folder, on a background thread, and writes nothing. Names are
/// drawn through the streamer mask (RefreshStreamerMask) and are never logged. Built in code, in the shape of
/// <see cref="BugReportWindow"/>, so a look restyles it and the literal scan reads it.</summary>
public sealed class StatReportWindow : Window
{
    /// <summary>The height of the tallest bar.</summary>
    public const double StripHeight = 96;

    private sealed record CharacterItem(string Name)
    {
        public string Shown => StreamerMask.Name(StreamerKind.Character, Name);
    }

    private sealed record MetricChoice(StatMetric Metric, string Text);

    private readonly StatReportActions _do;
    private readonly ListBox _list = new();
    private readonly ComboBox _metric = new();
    private readonly Grid _strip = new() { Height = StripHeight };
    private readonly TextBlock _scale, _from, _to, _hint, _status, _note;
    private readonly Border _hintBox = new();
    private readonly ListView _table = new();
    private StatReportModel? _model;
    private bool _loading, _closed, _filling;

    public StatReportWindow(StatReportActions actions)
    {
        _do = actions;
        UiStyles.Attach(this);
        Title = Str("L.SessRpt.Title", "Session reports");
        Width = 1240;
        Height = 660;
        MinWidth = 760;
        MinHeight = 460;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Fx.BgBrush");
        UiFont.Apply(this, LocalizationService.CurrentLanguage);

        var dock = new DockPanel { Margin = new Thickness(16, 12, 16, 12), LastChildFill = true };
        void Add(UIElement e, Dock side) { DockPanel.SetDock(e, side); dock.Children.Add(e); }

        // The hint above everything, shown only while there is nothing to report.
        _hint = Line("", "Fx.TextBrush");
        _hintBox.Child = _hint;
        _hintBox.Padding = new Thickness(12, 8, 12, 8);
        _hintBox.Margin = new Thickness(0, 0, 0, 10);
        _hintBox.SetResourceReference(Border.BackgroundProperty, "Fx.CardBrush");
        _hintBox.Visibility = Visibility.Collapsed;
        Add(_hintBox, Dock.Top);

        // The foot: the honesty note and the status on the left, the buttons on the right.
        _note = Line(Str("L.SessRpt.Note", "Active time is estimated: it adds up the gaps of up to 5 minutes between log lines. Mined is counted in units, not m³, because the logs don't carry the ore's volume."), "Fx.TextSecondaryBrush");
        _status = Line("", "Fx.TextBrush");
        _status.Margin = new Thickness(0, 4, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var refresh = new Button { Content = Str("L.SessRpt.Refresh", "Refresh"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0) };
        refresh.Click += (_, _) => Reload();
        var close = new Button { Content = Str("L.SessRpt.Close", "Close"), IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(refresh);
        buttons.Children.Add(close);
        var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(_note);
        foot.Children.Add(_status);
        foot.Children.Add(buttons);
        Add(foot, Dock.Bottom);

        // The characters on the left.
        var left = new DockPanel { Width = 210, Margin = new Thickness(0, 0, 12, 0) };
        var characters = Line(Str("L.SessRpt.Characters", "Characters"), "Fx.TextBrush");
        characters.Margin = new Thickness(0, 0, 0, 4);
        DockPanel.SetDock(characters, Dock.Top);
        left.Children.Add(characters);
        _list.DisplayMemberPath = nameof(CharacterItem.Shown);
        AutomationProperties.SetName(_list, Str("L.SessRpt.Characters", "Characters"));
        _list.SelectionChanged += (_, _) => { if (!_filling) ShowCharacter(); };
        left.Children.Add(_list);
        Add(left, Dock.Left);

        // The right side: the chooser of a number, the strip, the table.
        var right = new DockPanel();
        var chooser = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        chooser.Children.Add(Line(Str("L.SessRpt.Chart", "Chart"), "Fx.TextBrush", new Thickness(0, 4, 8, 0)));
        _metric.DisplayMemberPath = nameof(MetricChoice.Text);
        _metric.MinWidth = 180;
        _metric.ItemsSource = new[]
        {
            new MetricChoice(StatMetric.Bounty, Str("L.SessRpt.Col.Bounty", "Bounty (ISK)")),
            new MetricChoice(StatMetric.Mined, Str("L.SessRpt.Col.Mined", "Mined (units)")),
            new MetricChoice(StatMetric.DamageOut, Str("L.SessRpt.Col.DamageOut", "Damage out")),
            new MetricChoice(StatMetric.DamageIn, Str("L.SessRpt.Col.DamageIn", "Damage in")),
            new MetricChoice(StatMetric.RepsOut, Str("L.SessRpt.Col.RepsOut", "Repairs out")),
            new MetricChoice(StatMetric.RepsIn, Str("L.SessRpt.Col.RepsIn", "Repairs in")),
            new MetricChoice(StatMetric.Active, Str("L.SessRpt.Col.Active", "Active (estimated)")),
        };
        _metric.SelectedIndex = 0;
        AutomationProperties.SetName(_metric, Str("L.SessRpt.Chart", "Chart"));
        _metric.SelectionChanged += (_, _) => DrawStrip();
        chooser.Children.Add(_metric);
        DockPanel.SetDock(chooser, Dock.Top);
        right.Children.Add(chooser);

        var stripBox = new Border { Child = _strip, Padding = new Thickness(4, 4, 4, 0), Margin = new Thickness(0, 0, 0, 2) };
        stripBox.SetResourceReference(Border.BackgroundProperty, "Fx.CardBrush");
        DockPanel.SetDock(stripBox, Dock.Top);
        right.Children.Add(stripBox);

        // Under the strip: its first day, its highest day, today.
        var axis = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        _from = Line("", "Fx.TextSecondaryBrush");
        _scale = Line("", "Fx.TextSecondaryBrush");
        _scale.HorizontalAlignment = HorizontalAlignment.Center;
        _to = Line("", "Fx.TextSecondaryBrush");
        _to.HorizontalAlignment = HorizontalAlignment.Right;
        axis.Children.Add(_from);
        axis.Children.Add(_scale);
        axis.Children.Add(_to);
        DockPanel.SetDock(axis, Dock.Top);
        right.Children.Add(axis);

        _table.View = Columns();
        _table.SelectionMode = SelectionMode.Single;
        AutomationProperties.SetName(_table, Str("L.SessRpt.Title", "Session reports"));
        ScrollViewer.SetHorizontalScrollBarVisibility(_table, ScrollBarVisibility.Auto);
        right.Children.Add(_table);
        dock.Children.Add(right);

        var frame = new Border { Child = dock };
        frame.SetResourceReference(StyleProperty, "Fx.Dialog");
        Content = frame;

        Closed += (_, _) => _closed = true;
        Reload();
    }

    // ── What tests read ──

    public StatReportState State => _model?.State ?? StatReportState.NoFolder;

    /// <summary>The names as the list draws them (masked while streamer mode is on).</summary>
    public IReadOnlyList<string> CharacterTexts => _list.Items.Cast<CharacterItem>().Select(i => i.Shown).ToList();

    /// <summary>The chosen character as the files have the name (never masked), or null.</summary>
    public string? SelectedCharacter => (_list.SelectedItem as CharacterItem)?.Name;

    public void SelectCharacter(string name) => _list.SelectedItem = _list.Items.Cast<CharacterItem>().FirstOrDefault(i => i.Name == name);

    public StatMetric Metric
    {
        get => ((MetricChoice)_metric.SelectedItem).Metric;
        set => _metric.SelectedItem = ((IEnumerable<MetricChoice>)_metric.ItemsSource).First(c => c.Metric == value);
    }

    public IReadOnlyList<StatTableRow> TableRows => _table.Items.Cast<StatTableRow>().ToList();

    /// <summary>The bars' heights, oldest day first.</summary>
    public IReadOnlyList<double> BarHeights => _strip.Children.OfType<Rectangle>().Select(r => r.Height).ToList();

    /// <summary>The line about nothing to report, or "" while there is something.</summary>
    public string Hint => _hintBox.Visibility == Visibility.Visible ? _hint.Text : "";

    public string Status => _status.Text;
    public string Note => _note.Text;
    public string AxisText => _from.Text + "|" + _scale.Text + "|" + _to.Text;

    // ── Loading ──

    /// <summary>Reads the stat log folder again, off the UI thread, then shows it. A read while one is running is ignored.</summary>
    public void Reload()
    {
        if (_closed || _loading) return;
        string folder = _do.Folder() ?? "";
        var today = _do.Today();
        if (string.IsNullOrWhiteSpace(folder)) { Show(folder, Array.Empty<CharacterReport>(), today); return; }   // nothing to read
        _loading = true;
        if (_do.Synchronous) { Finish(StatReportFolder.Read(folder, today, StatReportFolder.MaxDays), folder, today); return; }
        Task.Run(() => StatReportFolder.Read(folder, today, StatReportFolder.MaxDays)).ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed) return;
            if (task.IsCompletedSuccessfully) Finish(task.Result, folder, today);
            else
            {
                _loading = false;
                SayHint(Str("L.SessRpt.Failed", "The stat files couldn't be read."));   // not logged: the exception's text may name a file, and the file a character
            }
        })));
    }

    private void Finish(IReadOnlyList<CharacterReport> reports, string folder, DateOnly today)
    {
        _loading = false;
        Show(folder, reports, today);
    }

    private void Show(string folder, IReadOnlyList<CharacterReport> reports, DateOnly today)
    {
        _model = new StatReportModel(folder, reports, today, StatReportFolder.MaxDays,
            Str("L.SessRpt.Reps.Tip", "Armor {0}, shield {1}, capacitor {2}, other {3}"));
        switch (_model.State)
        {
            case StatReportState.NoFolder:
                SayHint(Str("L.SessRpt.NoFolder", "Nothing to report yet. Turn on stat logging and choose a log folder in the settings; MultiPreview then saves one file per character per day, and this window reads them."));
                break;
            case StatReportState.NoFiles:
                SayHint(string.Format(CultureInfo.CurrentCulture, Str("L.SessRpt.NoFiles", "No stat files from the last {0} days were found in the log folder. Is stat logging turned on?"), _model.Days));
                break;
            default:
                SayHint("");
                break;
        }
        FillCharacters();
    }

    private void SayHint(string text)
    {
        _hint.Text = text;
        _hintBox.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Streamer mode was switched on or off: the list is drawn again. What is chosen stays the real name.</summary>
    public void RefreshStreamerMask() => FillCharacters();

    /// <summary>The list holds the model's characters; the one that was chosen stays chosen, else the first is.</summary>
    private void FillCharacters()
    {
        var model = _model;
        if (model == null) return;
        var chosen = SelectedCharacter;
        _filling = true;
        _list.ItemsSource = model.Characters.Select(n => new CharacterItem(n)).ToList();
        _list.SelectedItem = _list.Items.Cast<CharacterItem>().FirstOrDefault(i => string.Equals(i.Name, chosen, StringComparison.OrdinalIgnoreCase))
                             ?? _list.Items.Cast<CharacterItem>().FirstOrDefault();
        _filling = false;
        ShowCharacter();
    }

    /// <summary>The table and the strip of the chosen character, and the line under the window's foot.</summary>
    private void ShowCharacter()
    {
        var model = _model;
        if (model == null) return;
        _table.ItemsSource = model.Table(SelectedCharacter, Str("L.SessRpt.Total", "Total"));
        _status.Text = string.Format(CultureInfo.CurrentCulture, Str("L.SessRpt.Status", "Characters: {0}, days shown: {1}"), model.Characters.Count, model.Days);
        DrawStrip();
    }

    /// <summary>One bar per day of the last 30, oldest first, as tall as the day's number is against the highest day. A
    /// day with no log has no bar. The bar's tooltip says the day and the number.</summary>
    private void DrawStrip()
    {
        var model = _model;
        _strip.Children.Clear();
        _strip.ColumnDefinitions.Clear();
        if (model == null || _metric.SelectedItem is not MetricChoice choice) return;
        var series = model.Series(SelectedCharacter, choice.Metric);
        double max = series.Count == 0 ? 0 : series.Max();
        for (int i = 0; i < series.Count; i++)
        {
            _strip.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = new Rectangle
            {
                Height = series[i] > 0 && max > 0 ? Math.Max(2, series[i] / max * StripHeight) : 0,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1, 0, 1, 0),
                ToolTip = model.FirstDay.AddDays(i).ToString("d", CultureInfo.CurrentCulture) + ": " + StatReportModel.Text(choice.Metric, series[i]),
            };
            bar.SetResourceReference(Shape.FillProperty, "Fx.AccentBrush");
            Grid.SetColumn(bar, i);
            _strip.Children.Add(bar);
        }
        _from.Text = model.FirstDay.ToString("d", CultureInfo.CurrentCulture);
        _to.Text = model.Today.ToString("d", CultureInfo.CurrentCulture);
        _scale.Text = max > 0 ? string.Format(CultureInfo.CurrentCulture, Str("L.SessRpt.Highest", "Highest day: {0}"), StatReportModel.Text(choice.Metric, max)) : "";
    }

    // ── What the table shows ──

    /// <summary>The eight columns. Every cell is plain text (TextBlock.Text); the reps cells say the split by kind as a tooltip.</summary>
    private static GridView Columns()
    {
        var view = new GridView { AllowsColumnReorder = false };
        view.Columns.Add(Column("L.SessRpt.Col.Day", "Day", nameof(StatTableRow.Day), 100));
        view.Columns.Add(Column("L.SessRpt.Col.Active", "Active (estimated)", nameof(StatTableRow.Active), 150));
        view.Columns.Add(Column("L.SessRpt.Col.Bounty", "Bounty (ISK)", nameof(StatTableRow.Bounty), 140));
        view.Columns.Add(Column("L.SessRpt.Col.Mined", "Mined (units)", nameof(StatTableRow.Mined), 130));
        view.Columns.Add(Column("L.SessRpt.Col.DamageOut", "Damage out", nameof(StatTableRow.DamageOut), 130));
        view.Columns.Add(Column("L.SessRpt.Col.DamageIn", "Damage in", nameof(StatTableRow.DamageIn), 130));
        view.Columns.Add(Column("L.SessRpt.Col.RepsOut", "Repairs out", nameof(StatTableRow.RepsOut), 130, nameof(StatTableRow.RepsOutTip)));
        view.Columns.Add(Column("L.SessRpt.Col.RepsIn", "Repairs in", nameof(StatTableRow.RepsIn), 130, nameof(StatTableRow.RepsInTip)));
        return view;
    }

    private static GridViewColumn Column(string key, string english, string property, double width, string? tooltip = null)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        if (tooltip != null) text.SetBinding(ToolTipProperty, new Binding(tooltip));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new GridViewColumn { Header = Str(key, english), Width = width, CellTemplate = new DataTemplate { VisualTree = text } };
    }

    private static TextBlock Line(string text, string brushKey, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(StyleProperty, "Fx.Label");
        block.SetResourceReference(TextBlock.FontSizeProperty, "Fx.Size.Body");
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private static string Str(string key, string english) => LocalizationService.Str(key, english);
}
