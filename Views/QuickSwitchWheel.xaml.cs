using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using Color          = System.Windows.Media.Color;
using Cursors        = System.Windows.Input.Cursors;
using FontFamily     = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs   = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Ellipse        = System.Windows.Shapes.Ellipse;
using Panel          = System.Windows.Controls.Panel;
using Point          = System.Windows.Point;
using Size           = System.Windows.Size;

namespace EveMultiPreview.Views;

/// <summary>
/// Quick-switch wheel (Plan 4, the F2 reference): the clients' cards around a centre disc, each with its number key, name
/// and system. Normal mode: click a card or press 1–9; the hotkey again, Esc or clicking elsewhere closes. Edit mode: drag
/// cards to reorder, then Save order. With more clients than a ring fits on the screen, the cards go in rows.
/// </summary>
public partial class QuickSwitchWheel : Window
{
    // ── Events ────────────────────────────────────────────────────────
    public event Action<string>? CharacterSelected;
    public event Action<List<string>>? OrderSaved;

    /// <summary>The disc's border, both sides; the text wraps at the disc's width less this and its own margin.</summary>
    private const double DiscBorder = 2;

    // ── Card state ────────────────────────────────────────────────────
    private readonly List<CardEntry> _cards = new();
    private bool _editMode = false;
    private QuickSwitchLayout _layout = QuickSwitchLayout.For(0, new Size(1, 1));

    // ── Drag state ────────────────────────────────────────────────────
    private CardEntry? _dragCard;
    private Point _dragOffset;       // cursor pos relative to card top-left

    private enum CardLook { Normal, Hover, Edit, Drag }

    private sealed class CardEntry
    {
        public string Name { get; init; } = "";
        public Border Card { get; init; } = null!;
        public Border Number { get; init; } = null!;
        public TextBlock NumberText { get; init; } = null!;
        public TextBlock NameText { get; init; } = null!;
        public TextBlock SystemText { get; init; } = null!;
        public TextBlock? BadgeText { get; init; }   // the alert badge's count, null on a card with none
        public Ellipse? BadgeDisc { get; init; }
        public int Index { get; set; }   // slot in the layout
    }

    public QuickSwitchWheel()
    {
        UiStyles.Attach(this, StyledAs.Surface);
        InitializeComponent();
        UiFont.Apply(this, LocalizationService.CurrentLanguage);
        KeyDown += OnKeyDown;
        Deactivated += (_, _) => { if (!_editMode) CloseWheel(); };
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Builds the cards, in this order, without showing the window (ShowWheel shows it; tests call this).
    /// <paramref name="alertOf"/> says how many alerts a character has not looked at and the top severity among them
    /// (null: none); a card with some shows that as a badge in the severity's colour (<paramref name="settings"/> holds
    /// the player's colours; the defaults when not handed in).</summary>
    public void Populate(List<string> characters, Func<string, string?>? systemOf = null,
        Func<string, (int Count, string Severity)?>? alertOf = null, AppSettings? settings = null)
    {
        _cards.Clear();
        ButtonCanvas.Children.Clear();
        _editMode = false;
        UpdateHeader(characters.Count);

        var area = SystemParameters.WorkArea;
        _layout = QuickSwitchLayout.For(characters.Count, new Size(area.Width * 0.9, area.Height * 0.9), MeasureDisc(characters.Count));
        ApplyLayout();

        for (int i = 0; i < characters.Count; i++)
            CreateCard(characters[i], i, systemOf?.Invoke(characters[i]), alertOf?.Invoke(characters[i]), settings);
        PositionAllCards();
    }

    public void ShowWheel(List<string> characters, Func<string, string?>? systemOf = null,
        Func<string, (int Count, string Severity)?>? alertOf = null, AppSettings? settings = null)
    {
        Populate(characters, systemOf, alertOf, settings);
        Opacity = 0;
        Show();
        Focus();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>Force-close without the Deactivated guard blocking. Called by ThumbnailManager toggle.</summary>
    public void ForceClose() => CloseWheel();

    /// <summary>The cards' names in slot order (tests).</summary>
    public IReadOnlyList<string> CardOrder => _cards.OrderBy(c => c.Index).Select(c => c.Name).ToList();

    /// <summary>The name and the system each card draws, in slot order, "" where a card draws none (tests).</summary>
    public IReadOnlyList<(string Name, string System)> CardTexts =>
        _cards.OrderBy(c => c.Index).Select(c => (c.NameText.Text, c.SystemText.Visibility == Visibility.Visible ? c.SystemText.Text : "")).ToList();

    /// <summary>The alert badge each card draws in slot order: its text, fill colour and ink, "" and clear colours where a card has none (tests).</summary>
    public IReadOnlyList<(string Text, Color Colour, Color Ink)> CardBadges =>
        _cards.OrderBy(c => c.Index).Select(c => c.BadgeText == null || c.BadgeDisc?.Fill is not SolidColorBrush fill || c.BadgeText.Foreground is not SolidColorBrush ink
            ? ("", default(Color), default(Color)) : (c.BadgeText.Text, fill.Color, ink.Color)).ToList();

    /// <summary>The height each card's content asks for in slot order (tests): it has to fit the card's height inside its padding and edge.</summary>
    public IReadOnlyList<double> CardContentHeights =>
        _cards.OrderBy(c => c.Index).Select(c => ((UIElement)c.Card.Child).DesiredSize.Height).ToList();

    /// <summary>Where the cards are (tests).</summary>
    public QuickSwitchLayout CardLayout => _layout;

    /// <summary>The number key shown on each card in slot order, "" where none (tests).</summary>
    public IReadOnlyList<string> CardNumbers =>
        _cards.OrderBy(c => c.Index).Select(c => c.Number.Visibility == Visibility.Visible ? c.NumberText.Text : "").ToList();

    // ─────────────────────────────────────────────────────────────────
    // Layout
    // ─────────────────────────────────────────────────────────────────

    /// <summary>The disc is 150 px, or the diagonal of its text where a language needs more. Edit mode swaps in other
    /// (possibly longer) text, so both states are measured and the larger wins. The text wraps at the disc's own inner
    /// width, so a bigger disc wraps it wider: grow until the text fits (it stops quickly, the text only gets shorter).</summary>
    private double MeasureDisc(int count)
    {
        bool editMode = _editMode;
        double disc = QuickSwitchLayout.DiscSize;
        for (int pass = 0; pass < 12; pass++)
        {
            double diagonal = 0;
            foreach (bool edit in new[] { false, true })
            {
                _editMode = edit;
                UpdateHeader(count);
                InvalidateTree(DiscContent);   // a direct Measure with an unchanged constraint would return the cached size
                DiscContent.Measure(new Size(disc - DiscBorder, double.PositiveInfinity));
                var d = DiscContent.DesiredSize;
                diagonal = Math.Max(diagonal, Math.Sqrt(d.Width * d.Width + d.Height * d.Height));
            }
            double needed = Math.Ceiling(diagonal) + 4;
            if (needed <= disc) break;
            disc = needed;
        }
        _editMode = editMode;
        UpdateHeader(count);
        return disc;
    }

    private static void InvalidateTree(DependencyObject node)
    {
        (node as UIElement)?.InvalidateMeasure();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            InvalidateTree(VisualTreeHelper.GetChild(node, i));
    }

    private void ApplyLayout()
    {
        var l = _layout;
        ButtonCanvas.Width = l.Canvas.Width;
        ButtonCanvas.Height = l.Canvas.Height;
        if (l.Ring)
        {
            Grid.SetRow(Disc, 1);
            Disc.Width = Disc.Height = l.Disc;
            Disc.CornerRadius = new CornerRadius(l.Disc / 2);
            Disc.Margin = new Thickness(l.Centre.X - l.Disc / 2, l.Centre.Y - l.Disc / 2, 0, 0);
        }
        else
        {
            Grid.SetRow(Disc, 0);
            Disc.Width = l.Canvas.Width;
            Disc.Height = double.NaN;
            Disc.SetResourceReference(Border.CornerRadiusProperty, "Fx.FlyoutRadius");
            Disc.Margin = new Thickness(0, 0, 0, QuickSwitchLayout.Gap);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Card factory
    // ─────────────────────────────────────────────────────────────────

    private void CreateCard(string name, int index, string? system, (int Count, string Severity)? alerts, AppSettings? settings)
    {
        var numberText = new TextBlock { FontSize = 11 };
        numberText.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.MonoFont");
        numberText.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextBrush");
        var number = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = numberText,
        };
        number.SetResourceReference(Border.BackgroundProperty, "Fx.ControlBrush");
        number.SetResourceReference(Border.BorderBrushProperty, "Fx.BorderBrush");
        // Streamer mode (IDEAS #19): the card draws the masked name and system; entry.Name stays the real one, the key the pick is raised with.
        string shownSystem = StreamerMask.Name(StreamerKind.System, system);
        var nameText = new TextBlock
        {
            Text = StreamerMask.Name(StreamerKind.Character, name),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextBrush");
        var systemText = new TextBlock
        {
            Text = shownSystem,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = string.IsNullOrWhiteSpace(shownSystem) ? Visibility.Collapsed : Visibility.Visible,
        };
        systemText.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        // IDEAS P20: the alert badge sits at the right end of the number's row (the row is there on a card without a number too)
        var numberRow = new Grid { Children = { number } };
        var (badge, badgeText, badgeDisc) = alerts is { Count: > 0 } a ? CreateBadge(a, settings ?? new AppSettings(), numberText.FontSize) : (null, null, null);
        if (badge != null) numberRow.Children.Add(badge);
        var card = new PressBorder
        {
            Width = QuickSwitchLayout.CardWidth,
            Height = QuickSwitchLayout.CardHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new StackPanel { Children = { numberRow, nameText, systemText } },
        };

        // The name the card draws (masked in streamer mode) is what a screen reader reads (IDEAS #51).
        System.Windows.Automation.AutomationProperties.SetName(card, nameText.Text);
        var entry = new CardEntry { Name = name, Card = card, Number = number, NumberText = numberText, NameText = nameText, SystemText = systemText, BadgeText = badgeText, BadgeDisc = badgeDisc, Index = index };
        SetLook(entry, CardLook.Normal);
        _cards.Add(entry);

        // Normal-mode interactions
        card.MouseEnter += (_, _) => { if (!_editMode) SetLook(entry, CardLook.Hover); };
        card.MouseLeave += (_, _) => { if (!_editMode) SetLook(entry, CardLook.Normal); };
        void Select()
        {
            CharacterSelected?.Invoke(entry.Name);
            CloseWheel();
        }
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (_editMode)
                BeginDrag(entry, e);
            else
            {
                e.Handled = true;
                Select();
            }
        };
        card.Press = () => { if (!_editMode) Select(); };   // a screen reader's click: not while reordering, where a click begins a drag

        ButtonCanvas.Children.Add(card);
    }

    /// <summary>The badge: the count ("9+" over nine) on a disc in the colour the thumbnail would flash for that severity
    /// (AlertColours.Resolve, the severity's own: only the count and the top severity are known here). It holds a
    /// number and no name, so streamer mode has nothing to mask in it. The ink is black or white, whichever has the
    /// better contrast on the player's colour. It takes no clicks: a click on it is a click on the card.</summary>
    private static (Grid Badge, TextBlock Text, Ellipse Disc) CreateBadge((int Count, string Severity) alerts, AppSettings settings, double numberSize)
    {
        var fill = ThumbnailManager.ParseColor(AlertColours.Resolve(settings, "", alerts.Severity));
        // Black where it reads better than white on the player's colour: by WCAG contrast, black wins above a relative luminance of 0.179.
        bool lightFill = RelativeLuminance(fill) > 0.179;
        var disc = new Ellipse { Fill = new SolidColorBrush(fill) };
        var text = new TextBlock
        {
            Text = alerts.Count > 9 ? "9+" : alerts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = numberSize - 1,
            FontWeight = FontWeights.Bold,
            Foreground = lightFill ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Grid
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { disc, text },
        };
        return (badge, text, disc);
    }

    /// <summary>WCAG's relative luminance of a colour (0 black, 1 white).</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static void SetLook(CardEntry e, CardLook look)
    {
        bool lit = look is CardLook.Hover or CardLook.Drag;
        e.Card.SetResourceReference(Border.BackgroundProperty, lit ? "Fx.AccentSoftBrush" : "Fx.CardBrush");
        e.Card.SetResourceReference(Border.BorderBrushProperty, look == CardLook.Normal ? "Fx.FlyoutBorderBrush" : "Fx.AccentBrush");
        e.Card.Opacity = look == CardLook.Drag ? 0.85 : 1.0;
        e.Card.Cursor = look is CardLook.Edit or CardLook.Drag ? Cursors.SizeAll : Cursors.Hand;
    }

    private void PositionAllCards()
    {
        foreach (var e in _cards)
        {
            Canvas.SetLeft(e.Card, _layout.Slots[e.Index].X);
            Canvas.SetTop(e.Card, _layout.Slots[e.Index].Y);
            Panel.SetZIndex(e.Card, 0);
        }
        RefreshNumbers();
    }

    /// <summary>Keys 1–9 pick the first nine slots; later cards have no number and are clicked.</summary>
    private void RefreshNumbers()
    {
        foreach (var e in _cards)
        {
            e.NumberText.Text = (e.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            e.Number.Visibility = e.Index < 9 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateHeader(int count)
    {
        TxtCount.Text = _editMode ? Str("L.Qs.Reorder", "Drag to reorder")
            : count <= 1 ? Str("L.Qs.HintOne", "Press 1 or click")
            : string.Format(Str("L.Qs.HintFmt", "Press 1–{0} or click"), Math.Min(count, 9));
        BtnEdit.Content = _editMode ? Str("L.Qs.Save", "Save order") : Str("L.Qs.Edit", "Edit order");
        BtnEdit.ToolTip = _editMode ? Str("L.Qs.SaveTt", "Save card order") : Str("L.Qs.EditTt", "Edit card order");
    }

    private static string Str(string key, string english) => LocalizationService.Str(key, english);

    // ─────────────────────────────────────────────────────────────────
    // Edit mode
    // ─────────────────────────────────────────────────────────────────

    private void ToggleEditMode()
    {
        _editMode = !_editMode;
        foreach (var e in _cards) SetLook(e, _editMode ? CardLook.Edit : CardLook.Normal);
        UpdateHeader(_cards.Count);
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode)
        {
            // Save: emit order
            OrderSaved?.Invoke(CardOrder.ToList());
        }
        ToggleEditMode();
    }

    // ─────────────────────────────────────────────────────────────────
    // Drag-to-reorder
    // ─────────────────────────────────────────────────────────────────

    private void BeginDrag(CardEntry entry, MouseButtonEventArgs e)
    {
        _dragCard   = entry;
        _dragOffset = e.GetPosition(entry.Card);

        // Lift card above others
        Panel.SetZIndex(entry.Card, 99);
        SetLook(entry, CardLook.Drag);

        ButtonCanvas.MouseMove         += OnDragMove;
        ButtonCanvas.MouseLeftButtonUp += OnDragEnd;
        ButtonCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragCard == null) return;

        var pos = e.GetPosition(ButtonCanvas);
        double left = Math.Max(0, Math.Min(pos.X - _dragOffset.X, ButtonCanvas.Width - QuickSwitchLayout.CardWidth));
        double top  = Math.Max(0, Math.Min(pos.Y - _dragOffset.Y, ButtonCanvas.Height - QuickSwitchLayout.CardHeight));
        Canvas.SetLeft(_dragCard.Card, left);
        Canvas.SetTop(_dragCard.Card, top);

        // Find which slot we're hovering over
        int hoverIndex = _layout.SlotAt(pos);
        if (hoverIndex >= 0 && hoverIndex != _dragCard.Index)
            SwapCards(_dragCard, hoverIndex);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        ButtonCanvas.MouseMove         -= OnDragMove;
        ButtonCanvas.MouseLeftButtonUp -= OnDragEnd;
        ButtonCanvas.ReleaseMouseCapture();

        if (_dragCard == null) return;

        // Snap card to its final slot
        Canvas.SetLeft(_dragCard.Card, _layout.Slots[_dragCard.Index].X);
        Canvas.SetTop(_dragCard.Card, _layout.Slots[_dragCard.Index].Y);
        Panel.SetZIndex(_dragCard.Card, 0);
        SetLook(_dragCard, CardLook.Edit);
        _dragCard = null;
    }

    /// <summary>Swap the dragged card into targetIndex, moving the card that was there to the dragged card's slot.</summary>
    private void SwapCards(CardEntry drag, int targetIndex)
    {
        var displaced = _cards.FirstOrDefault(c => c.Index == targetIndex && c != drag);
        if (displaced == null) return;

        displaced.Index = drag.Index;
        drag.Index      = targetIndex;

        Canvas.SetLeft(displaced.Card, _layout.Slots[displaced.Index].X);
        Canvas.SetTop(displaced.Card, _layout.Slots[displaced.Index].Y);

        RefreshNumbers();
    }

    // ─────────────────────────────────────────────────────────────────
    // Keyboard
    // ─────────────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_editMode) ToggleEditMode(); // cancel edit
            else CloseWheel();
            return;
        }

        if (_editMode) return; // don't switch character while editing

        int num = e.Key switch
        {
            Key.D1 => 0, Key.D2 => 1, Key.D3 => 2,
            Key.D4 => 3, Key.D5 => 4, Key.D6 => 5,
            Key.D7 => 6, Key.D8 => 7, Key.D9 => 8,
            Key.NumPad1 => 0, Key.NumPad2 => 1, Key.NumPad3 => 2,
            Key.NumPad4 => 3, Key.NumPad5 => 4, Key.NumPad6 => 5,
            Key.NumPad7 => 6, Key.NumPad8 => 7, Key.NumPad9 => 8,
            _ => -1
        };

        if (num < 0) return;
        var target = _cards.FirstOrDefault(c => c.Index == num);
        if (target != null)
        {
            CharacterSelected?.Invoke(target.Name);
            CloseWheel();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Close
    // ─────────────────────────────────────────────────────────────────

    private void CloseWheel()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
