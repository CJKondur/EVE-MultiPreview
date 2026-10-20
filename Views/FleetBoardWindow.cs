using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using DataGrid = System.Windows.Controls.DataGrid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.Views;

/// <summary>What the fleet board asks of the app. App wires it; tests hand in their own inputs and clock.</summary>
public sealed class FleetBoardActions
{
    /// <summary>What is known about each character now (<see cref="FleetBoardRows.Gather"/>), asked at every refresh, on the window's thread.</summary>
    public Func<IReadOnlyList<FleetBoardInput>> Inputs { get; init; } = () => Array.Empty<FleetBoardInput>();
    /// <summary>The time now, UTC.</summary>
    public Func<DateTime> NowUtc { get; init; } = () => DateTime.UtcNow;
    /// <summary>Told when a refresh failed, with the exception's TYPE name only: its message may hold a character's name.</summary>
    public Action<string> Failed { get; init; } = _ => { };
}

/// <summary>The fleet board (IDEAS #18): one row per logged-in character with its system, how long it has been there, its
/// session time, the age and count of its alerts, and its CPU and RAM, for the times when the thumbnails are too small to
/// read. Click a header to sort. It reads through <see cref="FleetBoardActions.Inputs"/> once a second, from a timer that
/// runs only while the window is open, and changes nothing. Names and systems are drawn through the streamer mask
/// (<see cref="RefreshStreamerMask"/>) and are never logged. Built in code, in the shape of <see cref="StatReportWindow"/>,
/// so a look restyles it and the literal scan reads it.</summary>
public sealed class FleetBoardWindow : Window
{
    /// <summary>The seconds between two refreshes while the window is open.</summary>
    public const int RefreshSeconds = 1;

    private readonly FleetBoardActions _do;
    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<FleetBoardRow> _rows = new();
    private readonly TextBlock _empty;
    private readonly DispatcherTimer _timer;
    private bool _closed;

    public FleetBoardWindow(FleetBoardActions actions)
    {
        _do = actions;
        UiStyles.Attach(this);
        Title = Str("L.Fleet.Title", "Fleet board");
        Width = 1000;
        Height = 460;
        MinWidth = 720;
        MinHeight = 240;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Fx.BgBrush");
        UiFont.Apply(this, LocalizationService.CurrentLanguage);

        var dock = new DockPanel { Margin = new Thickness(16, 12, 16, 12), LastChildFill = true };

        // The foot: what the numbers are, and the close button.
        var note = Line(Str("L.Fleet.Note", "Alerts are counted since MultiPreview started. Docking isn't logged, so the board can't say when a character undocked."), "Fx.TextSecondaryBrush");
        var close = new Button { Content = Str("L.Fleet.Close", "Close"), IsCancel = true, Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(note);
        foot.Children.Add(close);
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        // The table, with the hint over it while there is no one to list.
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.CanUserDeleteRows = false;
        _grid.CanUserReorderColumns = false;
        _grid.CanUserSortColumns = true;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.ItemsSource = _rows;
        _grid.SetResourceReference(Control.BackgroundProperty, "Fx.CardBrush");
        _grid.SetResourceReference(Control.ForegroundProperty, "Fx.TextBrush");
        _grid.SetResourceReference(Control.BorderBrushProperty, "Fx.BorderBrush");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "Fx.CardBrush");
        _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Fx.BorderBrush");
        var head = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        head.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Fx.CardBrush")));
        head.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Fx.TextBrush")));
        head.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        _grid.ColumnHeaderStyle = head;
        System.Windows.Automation.AutomationProperties.SetName(_grid, Str("L.Fleet.Title", "Fleet board"));
        AddColumns();
        _grid.Sorting += (_, e) =>
        {
            e.Handled = true;   // the sort is FleetBoardRows.Sort, redone at each refresh; WPF's own would reset when the rows are replaced
            if (e.Column.Header is FrameworkElement { Tag: FleetBoardColumn column }) SortBy(column);
        };
        _empty = Line(Str("L.Fleet.Empty", "No EVE client with a logged-in character is open."), "Fx.TextSecondaryBrush", new Thickness(12, 40, 12, 0));
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.IsHitTestVisible = false;
        _empty.Visibility = Visibility.Collapsed;
        var body = new Grid();
        body.Children.Add(_grid);
        body.Children.Add(_empty);
        dock.Children.Add(body);

        var frame = new Border { Child = dock };
        frame.SetResourceReference(StyleProperty, "Fx.Dialog");
        Content = frame;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshSeconds) };
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
        Refresh();
        _timer.Start();
    }

    // ── What tests read ──

    /// <summary>The rows as the table holds them, in the order shown.</summary>
    public IReadOnlyList<FleetBoardRow> Rows => _rows.ToList();

    /// <summary>The line about no one to list, or "" while there are rows.</summary>
    public string EmptyText => _empty.Visibility == Visibility.Visible ? _empty.Text : "";

    public FleetBoardColumn SortColumn { get; private set; } = FleetBoardColumn.Name;
    public bool Descending { get; private set; }

    /// <summary>Whether the one-second timer runs: only while the window is open.</summary>
    public bool TimerRunning => _timer.IsEnabled;

    // ── Sorting ──

    /// <summary>What a click on a column header does: that column ascending, or the other way round when it is the sorted one already.</summary>
    public void SortBy(FleetBoardColumn column)
    {
        Descending = column == SortColumn && !Descending;
        SortColumn = column;
        MarkSortedColumn();
        Refresh();
    }

    private void MarkSortedColumn()
    {
        foreach (var c in _grid.Columns)
            c.SortDirection = c.Header is FrameworkElement { Tag: FleetBoardColumn column } && column == SortColumn
                ? (Descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
    }

    // ── Refreshing ──

    /// <summary>Reads the inputs and shows them, sorted as chosen. Nothing once the window is closed. A failure is told to
    /// <see cref="FleetBoardActions.Failed"/> by its type only (never a name) and the rows stay as they were: a timer's tick must not throw.</summary>
    public void Refresh()
    {
        if (_closed) return;
        try
        {
            var rows = FleetBoardRows.Sort(FleetBoardRows.Build(_do.Inputs(), _do.NowUtc(),
                name => StreamerMask.Name(StreamerKind.Character, name),
                system => StreamerMask.Name(StreamerKind.System, system)), SortColumn, Descending);
            Fill(rows);
        }
        catch (Exception ex)
        {
            try { _do.Failed(ex.GetType().Name); } catch { /* a tick must not throw */ }
        }
    }

    /// <summary>Streamer mode was switched on or off: the table is drawn again now, not at the next second.</summary>
    public void RefreshStreamerMask() => Refresh();

    /// <summary>Puts the new rows in place of the old one by one, so the scroll position and the chosen row stay (a new item
    /// source would send the table back to the top every second).</summary>
    private void Fill(IReadOnlyList<FleetBoardRow> rows)
    {
        var chosen = (_grid.SelectedItem as FleetBoardRow)?.Name;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i >= _rows.Count) _rows.Add(rows[i]);
            else if (_rows[i] != rows[i]) _rows[i] = rows[i];
        }
        while (_rows.Count > rows.Count) _rows.RemoveAt(_rows.Count - 1);
        if (chosen != null) _grid.SelectedItem = _rows.FirstOrDefault(r => r.Name == chosen);
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── What the table shows ──

    private void AddColumns()
    {
        var statsTip = Str("L.Fleet.Tip.Stats", "Read from the same monitor as the thumbnails' stats, every few seconds. A dash means no reading yet.");
        Add(FleetBoardColumn.Name, "L.Fleet.Col.Name", "Character", nameof(FleetBoardRow.Name), 190, null, null);
        Add(FleetBoardColumn.System, "L.Fleet.Col.System", "System", nameof(FleetBoardRow.System), 140, null, null);
        Add(FleetBoardColumn.Since, "L.Fleet.Col.Since", "In system since", nameof(FleetBoardRow.SinceText), 130,
            Str("L.Fleet.Tip.Since", "When MultiPreview saw the character arrive in this system. For the system it already knew at start-up, the time of the character's latest Local chat log."), nameof(FleetBoardRow.SinceTip));
        Add(FleetBoardColumn.Session, "L.Fleet.Col.Session", "Session", nameof(FleetBoardRow.SessionText), 100, null, null);
        Add(FleetBoardColumn.LastAlert, "L.Fleet.Col.LastAlert", "Last alert", nameof(FleetBoardRow.LastAlertText), 100, null, null);
        Add(FleetBoardColumn.Alerts, "L.Fleet.Col.Alerts", "Alerts", nameof(FleetBoardRow.Alerts), 80,
            Str("L.Fleet.Tip.Alerts", "Alerts since MultiPreview started, as the alert history holds them (the newest 2,000 of all characters, and none while the history is off)."), null);
        Add(FleetBoardColumn.Cpu, "L.Fleet.Col.Cpu", "CPU", nameof(FleetBoardRow.CpuText), 90, statsTip, null);
        Add(FleetBoardColumn.Ram, "L.Fleet.Col.Ram", "RAM", nameof(FleetBoardRow.RamText), 100, statsTip, null);
        MarkSortedColumn();
    }

    /// <summary>One column. The header is a text block that remembers which column it is (<c>Tag</c>) and can carry a tooltip;
    /// a cell may carry the tooltip of another property of its row.</summary>
    private void Add(FleetBoardColumn column, string key, string english, string property, double width, string? headerTip, string? cellTipProperty)
    {
        var header = new TextBlock { Text = Str(key, english), Tag = column, TextTrimming = TextTrimming.CharacterEllipsis };
        if (headerTip != null) header.ToolTip = headerTip;
        var cell = new Style(typeof(TextBlock));
        cell.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        cell.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 3, 8, 3)));
        if (cellTipProperty != null) cell.Setters.Add(new Setter(ToolTipProperty, new Binding(cellTipProperty)));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            Width = width,
            ElementStyle = cell,
            SortMemberPath = property,
        });
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
