using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>IDEAS #16 on Linux (the Windows HotkeySheetWindow, keep in sync): the hotkeys of the current profile (the shared
/// HotkeySheet.Rows of the cards Linux shows) on a window over the thumbnails, centred on the monitor under the pointer. It
/// never takes the focus from the game (not activated; the app marks it no-focus on the X server), and a click on it or its
/// 30 seconds close it. The rows' texts are a person's and a card's words: they are shown, never a format.</summary>
public sealed class HotkeySheetWindow : Window
{
    public static readonly TimeSpan Lasts = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _timer = new() { Interval = Lasts };
    private readonly PixelPoint? _pointer;
    private bool _closed;

    public HotkeySheetWindow(IReadOnlyList<HotkeySheetRow> rows, PixelPoint? pointer)
    {
        _pointer = pointer;
        Title = "MultiPreview - " + LocalizationService.Str("L.Sheet.Title", "Hotkeys of this profile");
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Content = Build(rows);
        LinuxLooks.Dress(this);
        PointerReleased += (_, _) => Dismiss();
        _timer.Tick += (_, _) => Dismiss();
        Opened += (_, _) => { Place(); _timer.Start(); };
        SizeChanged += (_, _) => Place();
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
    }

    /// <summary>Stops the timer and closes, once.</summary>
    public void Dismiss()
    {
        _timer.Stop();
        if (_closed) return;
        _closed = true;
        Close();
    }

    /// <summary>Centred in the work area of the monitor under the pointer (else the primary one), at most 90 % of it.</summary>
    private void Place()
    {
        var screen = (_pointer is { } at ? Screens.ScreenFromPoint(at) : null) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var area = screen.WorkingArea;
        MaxWidth = area.Width * 0.9 / screen.Scaling;
        MaxHeight = area.Height * 0.9 / screen.Scaling;
        int w = (int)Math.Round(Bounds.Width * screen.Scaling), h = (int)Math.Round(Bounds.Height * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2);
    }

    private static TextBlock Line(string text, string role, double top = 0, bool bold = false)
    {
        var block = LinuxLooks.Text(text, role);
        block.Margin = new Thickness(0, top, 0, 0);
        block.TextWrapping = TextWrapping.Wrap;
        block.MaxWidth = 520;
        if (bold) block.FontWeight = FontWeight.SemiBold;
        return block;
    }

    private static Control Build(IReadOnlyList<HotkeySheetRow> rows)
    {
        var panel = new DockPanel { Margin = new Thickness(16, 12), LastChildFill = true };
        void Add(Control c, Dock side) { DockPanel.SetDock(c, side); panel.Children.Add(c); }
        Add(Line(LocalizationService.Str("L.Sheet.Title", "Hotkeys of this profile"), "Label", bold: true), Dock.Top);
        Add(Line(LocalizationService.Str("L.Sheet.Close", "Click to close"), "Desc", top: 10), Dock.Bottom);
        int shown = Math.Min(rows.Count, HotkeySheet.MaxRows);
        if (rows.Count > HotkeySheet.MaxRows)
            Add(Line(string.Format(LocalizationService.Str("L.Sheet.More", "and {0} more on Clients › Hotkeys & switching"), rows.Count - HotkeySheet.MaxRows), "Desc", top: 8), Dock.Bottom);
        if (shown == 0)
            panel.Children.Add(Line(LocalizationService.Str("L.Sheet.None", "No hotkeys are set. Set them on Clients › Hotkeys & switching."), "Desc", top: 8));
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
            for (int i = 0; i < shown; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var action = Line(rows[i].Action, "Label", top: i == 0 ? 0 : 3);
                var keys = Line(rows[i].Keys, "Desc", top: i == 0 ? 0 : 3);
                keys.FontFamily = new FontFamily("monospace");
                keys.Margin = new Thickness(24, keys.Margin.Top, 0, 0);
                keys.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(action, i);
                Grid.SetRow(keys, i);
                Grid.SetColumn(keys, 1);
                grid.Children.Add(action);
                grid.Children.Add(keys);
            }
            panel.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 0, 0) });
        }
        var card = new Border
        {
            Child = panel, BorderThickness = new Thickness(1), CornerRadius = LinuxLooks.Effective.Radius("Radius.Control"),
        };
        LinuxLooks.Paint(card, Border.BackgroundProperty, "Fx.BgBrush");
        LinuxLooks.Paint(card, Border.BorderBrushProperty, "Fx.ControlEdgeBrush");
        return card;
    }
}
