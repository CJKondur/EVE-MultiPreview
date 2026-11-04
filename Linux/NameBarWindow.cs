using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>IDEAS #38, the compact name bar (the Windows NameBarWindow): one button per running character in a row at the top
/// centre of the primary screen's work area, wrapping past 90 % of its width. A click switches to that client; resting the
/// pointer on a button for <see cref="HoverDelay"/> shows that client live below the bar (a thumbnail window of its own), and
/// leaving takes it away. The bar never takes the focus from the game, is not dragged and its place is not saved.</summary>
public sealed class NameBarWindow : Window
{
    public static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(150);
    public const double WidthShare = 0.9;
    public const int PreviewWidth = 360, PreviewHeight = 225, PreviewGap = 6;

    private readonly Func<IReadOnlyList<NameBarEntry>> _entries;
    private readonly Action<string> _switchTo;
    private readonly Func<string, EveClient?> _clientOf;
    private readonly WrapPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = HoverDelay };
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<NameBarEntry> _shown = Array.Empty<NameBarEntry>();
    private string? _hovered;

    /// <summary>The live picture of the client under the pointer, or null. The app draws it at each tick.</summary>
    public ThumbnailWindow? Preview { get; private set; }

    /// <param name="entries">The buttons, asked at each <see cref="Refresh"/> (NameBar.Entries).</param>
    /// <param name="switchTo">A click: the switch a thumbnail's click makes, by name.</param>
    /// <param name="clientOf">The character's client, for the preview, or null.</param>
    public NameBarWindow(Func<IReadOnlyList<NameBarEntry>> entries, Action<string> switchTo, Func<string, EveClient?> clientOf)
    {
        _entries = entries;
        _switchTo = switchTo;
        _clientOf = clientOf;
        Title = "MultiPreview - " + LocalizationService.Str("L.NameBar.Title", "Character name bar");
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Background = new SolidColorBrush(Color.Parse("#E01E1E1E"));
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            Padding = new Thickness(4),
            Child = _row,
        };
        _hoverTimer.Tick += (_, _) => HoverElapsed();
        Opened += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        Closed += (_, _) => HoverOff();
        Refresh();
    }

    private Avalonia.Platform.Screen? Main => Screens.Primary ?? Screens.All.FirstOrDefault();

    /// <summary>The top centre of the primary screen's work area, from the bar's own size.</summary>
    private void Place()
    {
        if (Main is not { } screen) return;
        var area = screen.WorkingArea;
        MaxWidth = area.Width * WidthShare / screen.Scaling;
        int width = (int)Math.Round(Bounds.Width * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y);
    }

    /// <summary>Asks for the buttons again. The same names in the same order with the same stripes: only the paint changes
    /// (an alert, the client in front), so the pointer's hover and its preview stay. Otherwise the buttons are made again.</summary>
    public void Refresh()
    {
        var now = _entries();
        bool sameButtons = now.Count == _shown.Count && now.Zip(_shown).All(p =>
            p.First.Name == p.Second.Name && p.First.GroupColour == p.Second.GroupColour
            && StreamerMask.Name(StreamerKind.Character, p.First.Name) == (_buttons.TryGetValue(p.Second.Name, out var b) ? (string?)b.Tag : null));
        _shown = now;
        if (sameButtons)
        {
            foreach (var entry in now) Paint(_buttons[entry.Name], entry, entry.Name == _hovered);
            return;
        }
        _row.Children.Clear();
        _buttons.Clear();
        foreach (var entry in now)
        {
            var button = Make(entry);
            _buttons[entry.Name] = button;
            _row.Children.Add(button);
        }
        if (_hovered != null && !_buttons.ContainsKey(_hovered)) HoverOff();   // its client is gone
    }

    private Button Make(NameBarEntry entry)
    {
        var shown = StreamerMask.Name(StreamerKind.Character, entry.Name);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (entry.GroupColour is { Length: > 0 } colour)
        {
            var c = Models.AppSettings.ParseColor(colour, System.Windows.Media.Color.FromRgb(128, 128, 128));
            content.Children.Add(new Border { Width = 4, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) });
        }
        content.Children.Add(new TextBlock { Text = shown, VerticalAlignment = VerticalAlignment.Center });   // a TextBlock: "_" is no access key
        var button = new Button { Content = content, Margin = new Thickness(2), Padding = new Thickness(10, 5, 12, 5), Tag = shown, Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand) };
        var name = entry.Name;
        button.Click += (_, _) => { HoverOff(); _switchTo(name); };
        button.PointerEntered += (_, _) => { HoverOn(name); Paint(button, Current(name), true); };
        button.PointerExited += (_, _) => { HoverOff(); Paint(button, Current(name), false); };
        Paint(button, entry, false);
        return button;
    }

    private NameBarEntry Current(string name) => _shown.FirstOrDefault(e => e.Name == name) ?? new NameBarEntry(name, null, null, false);

    /// <summary>The border says the worst unseen alert (critical, warning, info); the fill says the client in front or the pointer.</summary>
    private static void Paint(Button button, NameBarEntry entry, bool hovered)
    {
        button.BorderBrush = entry.Severity?.ToLowerInvariant() switch
        {
            "critical" => Brushes.IndianRed,
            "warning" => Brushes.Orange,
            "info" => Brushes.LightSkyBlue,
            _ => new SolidColorBrush(Color.Parse("#40FFFFFF")),
        };
        button.BorderThickness = new Thickness(entry.Severity != null ? 2 : 1);
        button.Background = new SolidColorBrush(Color.Parse(entry.Active || hovered ? "#FF2E4A6B" : "#FF2B2B2B"));
    }

    private void HoverOn(string name)
    {
        HoverOff();
        _hovered = name;
        _hoverTimer.Start();
    }

    private void HoverOff()
    {
        _hoverTimer.Stop();
        _hovered = null;
        Preview?.Close();
        Preview = null;
    }

    /// <summary>The wait is over: the live picture of the client under the pointer, centred under its button, below the bar,
    /// in the client's own shape.</summary>
    private void HoverElapsed()
    {
        _hoverTimer.Stop();
        if (_hovered is not { } name || _clientOf(name) is not { } client || !_buttons.TryGetValue(name, out var button) || Main is not { } screen) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, 0), this) ?? new Point(Bounds.Width / 2, 0);
        int centre = Position.X + (int)Math.Round(at.X * scale);
        int width = PreviewWidth, height = PreviewHeight;
        int left = Math.Clamp(centre - width / 2, area.X, Math.Max(area.X, area.Right - width));
        int top = Math.Min(Position.Y + (int)Math.Round(Bounds.Height * scale) + PreviewGap, Math.Max(area.Y, area.Bottom - height));
        Preview = new ThumbnailWindow(client, new PixelRect(left, top, width, height), second: true) { CanDrag = false, IsHitTestVisible = false };
        Preview.Show();
    }
}
