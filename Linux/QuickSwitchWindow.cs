using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.Views;

namespace EveMultiPreview.Linux;

/// <summary>S8: the quick-switch wheel (Plan 4; the Windows QuickSwitchWheel, keep in sync): the running characters' cards
/// around a centre disc, each with its number key, name, system and unseen-alert badge, placed by the shared
/// QuickSwitchLayout (rows instead of a ring when the ring would not fit the screen). Click a card or press 1–9 to switch
/// to it; the hotkey again, Esc or a click away closes. Edit order: drag the cards, then Save order (kept in
/// QuickSwitchCardOrder). It takes the keyboard while it is open, so a number pressed never reaches the game.</summary>
public sealed class QuickSwitchWindow : Window
{
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#FF262A30")), CardLit = new SolidColorBrush(Color.Parse("#FF2E4A6B")),
        Edge = new SolidColorBrush(Color.Parse("#50FFFFFF")), EdgeLit = new SolidColorBrush(Color.Parse("#FF4F8CC9")),
        DiscBrush = new SolidColorBrush(Color.Parse("#FF1F1F1F")), Secondary = new SolidColorBrush(Color.Parse("#B0FFFFFF"));

    private sealed class Card
    {
        public required string Name { get; init; }
        public required Border View { get; init; }
        public required Border Number { get; init; }
        public required TextBlock NumberText { get; init; }
        public int Index { get; set; }
    }

    private readonly List<Card> _cards = new();
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _disc;
    private readonly TextBlock _hint = new() { FontSize = 12, Foreground = Secondary, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Button _edit = new() { FontSize = 12, Padding = new Thickness(10, 2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _editText = new();
    private QuickSwitchLayout _layout = QuickSwitchLayout.For(0, new System.Windows.Size(1, 1));
    private bool _editMode, _done;
    private Card? _drag;
    private Point _dragOffset;

    /// <summary>A card was picked (the real name, also in streamer mode): the app switches to that client.</summary>
    public event Action<string>? CharacterSelected;

    /// <summary>Save order was pressed: the names in slot order.</summary>
    public event Action<List<string>>? OrderSaved;

    /// <summary>The cards' names in slot order (tests).</summary>
    public IReadOnlyList<string> CardOrder => _cards.OrderBy(c => c.Index).Select(c => c.Name).ToList();

    /// <param name="characters">The running characters, in the order the cards take (QuickSwitchOrder).</param>
    /// <param name="systemOf">The system the logs last saw the character in, or null.</param>
    /// <param name="alertOf">How many alerts the character has not looked at, and the top severity (AlertAttention.For).</param>
    /// <param name="colourOf">The colour a severity flashes in (AlertColours.Resolve, parsed).</param>
    /// <param name="maxCanvas">The most room the cards may take (the work area of the screen it opens on, less a tenth).</param>
    public QuickSwitchWindow(IReadOnlyList<string> characters, Func<string, string?> systemOf, Func<string, (int Count, string Severity)?> alertOf,
        Func<string, Color> colourOf, System.Windows.Size maxCanvas)
    {
        Title = "MultiPreview - " + LocalizationService.Str("L.Qs.Title", "QuickSwitch");
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        // Almost clear, not clear: a click between two cards lands on the wheel, never on the game behind it.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        _edit.Content = _editText;
        _edit.Click += (_, _) =>
        {
            if (_editMode) OrderSaved?.Invoke(CardOrder.ToList());
            ToggleEditMode();
        };
        var discContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 8) };
        discContent.Children.Add(new TextBlock { Text = LocalizationService.Str("L.Qs.SwitchChar", "Switch to…"), FontSize = 13, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        discContent.Children.Add(_hint);
        discContent.Children.Add(_edit);
        _disc = new Border { Background = DiscBrush, BorderBrush = Edge, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = discContent };

        UpdateHeader(characters.Count);
        _layout = QuickSwitchLayout.For(characters.Count, maxCanvas, DiscFor(discContent, characters.Count));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(_canvas, 1);
        root.Children.Add(_canvas);
        root.Children.Add(_disc);
        _canvas.Width = _layout.Canvas.Width;
        _canvas.Height = _layout.Canvas.Height;
        if (_layout.Ring)
        {
            Grid.SetRow(_disc, 1);
            _disc.Width = _disc.Height = _layout.Disc;
            _disc.CornerRadius = new CornerRadius(_layout.Disc / 2);
            _disc.Margin = new Thickness(_layout.Centre.X - _layout.Disc / 2, _layout.Centre.Y - _layout.Disc / 2, 0, 0);
        }
        else
        {
            Grid.SetRow(_disc, 0);
            _disc.Width = _layout.Canvas.Width;
            _disc.CornerRadius = new CornerRadius(8);
            _disc.Margin = new Thickness(0, 0, 0, QuickSwitchLayout.Gap);
        }
        Content = root;

        for (int i = 0; i < characters.Count; i++) AddCard(characters[i], i, systemOf(characters[i]), alertOf(characters[i]), colourOf);
        PlaceCards();

        // Tunnel: the number keys and Esc are seen before the Edit button takes them.
        AddHandler(KeyDownEvent, (_, e) => OnKey(e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => { Place(); Activate(); Focus(); };
        Deactivated += (_, _) => { if (!_editMode) Finish(); };   // a click away
        Closed += (_, _) => _done = true;
    }

    /// <summary>QuickSwitchWheel.MeasureDisc: 150 px, or the diagonal of the disc's text where a language needs more (both the
    /// normal and the edit-mode text are measured, the larger wins); grown until the text fits at the disc's own width.</summary>
    private double DiscFor(StackPanel content, int count)
    {
        double disc = QuickSwitchLayout.DiscSize;
        for (int pass = 0; pass < 12; pass++)
        {
            double diagonal = 0;
            foreach (bool edit in new[] { false, true })
            {
                _editMode = edit;
                UpdateHeader(count);
                content.InvalidateMeasure();
                foreach (var child in content.Children) child.InvalidateMeasure();
                content.Measure(new Size(disc - 2, double.PositiveInfinity));
                var d = content.DesiredSize;
                // The button has no template yet while the window is not open: its text plus its padding and frame.
                double w = Math.Max(d.Width, _editText.DesiredSize.Width + 26), h = d.Height + 8;
                diagonal = Math.Max(diagonal, Math.Sqrt(w * w + h * h));
            }
            double needed = Math.Ceiling(diagonal) + 4;
            if (needed <= disc) break;
            disc = needed;
        }
        _editMode = false;
        UpdateHeader(count);
        return disc;
    }

    /// <summary>The middle of the screen it opens on (the Windows wheel's CenterScreen).</summary>
    private void Place()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        int width = (int)Math.Round(Bounds.Width * screen.Scaling), height = (int)Math.Round(Bounds.Height * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
    }

    /// <summary>Opens on the screen under <paramref name="pointer"/> (screen pixels), when known.</summary>
    public void ShowAt(PixelPoint? pointer)
    {
        if (pointer is { } p) Position = p;   // only picks the screen: Place centres it once it is open
        Show();
    }

    /// <summary>Closes without picking (the hotkey again).</summary>
    public void Dismiss() => Finish();

    private void Finish()
    {
        if (_done) return;
        _done = true;
        Close();
    }

    private void UpdateHeader(int count)
    {
        _hint.Text = _editMode ? LocalizationService.Str("L.Qs.Reorder", "Drag to reorder")
            : count <= 1 ? LocalizationService.Str("L.Qs.HintOne", "Press 1 or click")
            : string.Format(LocalizationService.Str("L.Qs.HintFmt", "Press 1–{0} or click"), Math.Min(count, 9));
        _editText.Text = _editMode ? LocalizationService.Str("L.Qs.Save", "Save order") : LocalizationService.Str("L.Qs.Edit", "Edit order");
        ToolTip.SetTip(_edit, _editMode ? LocalizationService.Str("L.Qs.SaveTt", "Save card order") : LocalizationService.Str("L.Qs.EditTt", "Edit card order"));
    }

    private void AddCard(string name, int index, string? system, (int Count, string Severity)? alerts, Func<string, Color> colourOf)
    {
        var numberText = new TextBlock { FontSize = 11, FontFamily = new FontFamily("DejaVu Sans Mono, Liberation Mono, monospace") };
        var number = new Border
        {
            BorderThickness = new Thickness(1), BorderBrush = Edge, Background = DiscBrush, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1), HorizontalAlignment = HorizontalAlignment.Left, Child = numberText,
        };
        // Streamer mode (IDEAS #19): the card draws the masked name and system; the pick is raised with the real name.
        string shownSystem = StreamerMask.Name(StreamerKind.System, system);
        var nameText = new TextBlock { Text = StreamerMask.Name(StreamerKind.Character, name), FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
        var systemText = new TextBlock { Text = shownSystem, FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = !string.IsNullOrWhiteSpace(shownSystem) };
        var numberRow = new Grid();
        numberRow.Children.Add(number);
        if (alerts is { Count: > 0 } a) numberRow.Children.Add(Badge(a, colourOf(a.Severity)));
        var stack = new StackPanel();
        stack.Children.Add(numberRow);
        stack.Children.Add(nameText);
        stack.Children.Add(systemText);
        var view = new Border
        {
            Width = QuickSwitchLayout.CardWidth, Height = QuickSwitchLayout.CardHeight, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 8), Child = stack,
        };
        var card = new Card { Name = name, View = view, Number = number, NumberText = numberText, Index = index };
        Look(card, lit: false);
        _cards.Add(card);

        view.PointerEntered += (_, _) => { if (!_editMode) Look(card, lit: true); };
        view.PointerExited += (_, _) => { if (!_editMode) Look(card, lit: false); };
        view.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (_editMode) BeginDrag(card, e);
            else Pick(card);
        };
        view.PointerMoved += (_, e) => { if (_drag == card) DragTo(e.GetPosition(_canvas)); };
        view.PointerReleased += (_, e) => { if (_drag == card) EndDrag(e); };
        _canvas.Children.Add(view);
    }

    /// <summary>QuickSwitchWheel.CreateBadge: the count ("9+" over nine) on a disc in the severity's colour, with black or white
    /// ink, whichever reads better (WCAG: black above a relative luminance of 0.179). A number, so nothing to mask.</summary>
    private static Control Badge((int Count, string Severity) alerts, Color fill)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        bool light = 0.2126 * Lin(fill.R) + 0.7152 * Lin(fill.G) + 0.0722 * Lin(fill.B) > 0.179;
        var badge = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        badge.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Fill = new SolidColorBrush(fill) });
        badge.Children.Add(new TextBlock
        {
            Text = alerts.Count > 9 ? "9+" : alerts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 10, FontWeight = FontWeight.Bold, Foreground = light ? Brushes.Black : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return badge;
    }

    private void Look(Card card, bool lit)
    {
        card.View.Background = lit ? CardLit : CardBrush;
        card.View.BorderBrush = lit || _editMode ? EdgeLit : Edge;
        card.View.Opacity = _drag == card ? 0.85 : 1.0;
        card.View.Cursor = new Cursor(_editMode ? StandardCursorType.SizeAll : StandardCursorType.Hand);
    }

    private void PlaceCards()
    {
        foreach (var c in _cards)
        {
            Canvas.SetLeft(c.View, _layout.Slots[c.Index].X);
            Canvas.SetTop(c.View, _layout.Slots[c.Index].Y);
            c.View.ZIndex = 0;
        }
        RefreshNumbers();
    }

    /// <summary>Keys 1–9 pick the first nine slots; later cards have no number and are clicked.</summary>
    private void RefreshNumbers()
    {
        foreach (var c in _cards)
        {
            c.NumberText.Text = (c.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            c.Number.IsVisible = c.Index < 9;
        }
    }

    private void ToggleEditMode()
    {
        _editMode = !_editMode;
        foreach (var c in _cards) Look(c, lit: false);
        UpdateHeader(_cards.Count);
    }

    private void Pick(Card card)
    {
        Finish();
        CharacterSelected?.Invoke(card.Name);
    }

    // ── Drag to reorder (edit mode) ──

    private void BeginDrag(Card card, PointerPressedEventArgs e)
    {
        _drag = card;
        _dragOffset = e.GetPosition(card.View);
        card.View.ZIndex = 99;
        Look(card, lit: true);
        e.Pointer.Capture(card.View);
    }

    private void DragTo(Point pos)
    {
        if (_drag == null) return;
        double left = Math.Clamp(pos.X - _dragOffset.X, 0, Math.Max(0, _canvas.Width - QuickSwitchLayout.CardWidth));
        double top = Math.Clamp(pos.Y - _dragOffset.Y, 0, Math.Max(0, _canvas.Height - QuickSwitchLayout.CardHeight));
        Canvas.SetLeft(_drag.View, left);
        Canvas.SetTop(_drag.View, top);
        int over = _layout.SlotAt(new System.Windows.Point(pos.X, pos.Y));
        if (over < 0 || over == _drag.Index) return;
        // The card in that slot takes the dragged card's slot.
        var displaced = _cards.FirstOrDefault(c => c.Index == over && c != _drag);
        if (displaced == null) return;
        displaced.Index = _drag.Index;
        _drag.Index = over;
        Canvas.SetLeft(displaced.View, _layout.Slots[displaced.Index].X);
        Canvas.SetTop(displaced.View, _layout.Slots[displaced.Index].Y);
        RefreshNumbers();
    }

    private void EndDrag(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_drag == null) return;
        var card = _drag;
        _drag = null;
        Canvas.SetLeft(card.View, _layout.Slots[card.Index].X);
        Canvas.SetTop(card.View, _layout.Slots[card.Index].Y);
        card.View.ZIndex = 0;
        Look(card, lit: false);
    }

    // ── Keys ──

    private void OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_editMode) ToggleEditMode();   // Esc cancels the edit
            else Finish();
            return;
        }
        if (_editMode) return;   // no switching while reordering
        int slot = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            _ => -1,
        };
        if (slot < 0) return;
        e.Handled = true;
        if (_cards.FirstOrDefault(c => c.Index == slot) is { } card) Pick(card);
    }

    /// <summary>ThumbnailManager.ShowQuickSwitch's order (keep in sync): the saved order's characters that run, then every
    /// other running one by name.</summary>
    public static List<string> Order(IReadOnlyList<string> running, IReadOnlyList<string> saved)
    {
        var characters = running.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        var ordered = saved.Where(n => characters.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        ordered.AddRange(characters.Where(n => !saved.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n));
        return ordered;
    }
}
