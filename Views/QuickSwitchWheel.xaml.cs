using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using Cursors        = System.Windows.Input.Cursors;
using FontFamily     = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs   = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
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

    /// <summary>Width the disc's text wraps at; the disc grows past 150 px when its text needs more room.</summary>
    private const double DiscTextWidth = 126;

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
        public int Index { get; set; }   // slot in the layout
    }

    public QuickSwitchWheel()
    {
        InitializeComponent();
        FontFamily = UiFont.For(LocalizationService.CurrentLanguage);
        KeyDown += OnKeyDown;
        Deactivated += (_, _) => { if (!_editMode) CloseWheel(); };
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Builds the cards, in this order, without showing the window (ShowWheel shows it; tests call this).</summary>
    public void Populate(List<string> characters, Func<string, string?>? systemOf = null)
    {
        _cards.Clear();
        ButtonCanvas.Children.Clear();
        _editMode = false;
        UpdateHeader(characters.Count);

        var area = SystemParameters.WorkArea;
        _layout = QuickSwitchLayout.For(characters.Count, new Size(area.Width * 0.9, area.Height * 0.9), MeasureDisc());
        ApplyLayout();

        for (int i = 0; i < characters.Count; i++)
            CreateCard(characters[i], i, systemOf?.Invoke(characters[i]));
        PositionAllCards();
    }

    public void ShowWheel(List<string> characters, Func<string, string?>? systemOf = null)
    {
        Populate(characters, systemOf);
        Opacity = 0;
        Show();
        Focus();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    /// <summary>Force-close without the Deactivated guard blocking. Called by ThumbnailManager toggle.</summary>
    public void ForceClose() => CloseWheel();

    /// <summary>The cards' names in slot order (tests).</summary>
    public IReadOnlyList<string> CardOrder => _cards.OrderBy(c => c.Index).Select(c => c.Name).ToList();

    /// <summary>Where the cards are (tests).</summary>
    public QuickSwitchLayout CardLayout => _layout;

    /// <summary>The number key shown on each card in slot order, "" where none (tests).</summary>
    public IReadOnlyList<string> CardNumbers =>
        _cards.OrderBy(c => c.Index).Select(c => c.Number.Visibility == Visibility.Visible ? c.NumberText.Text : "").ToList();

    // ─────────────────────────────────────────────────────────────────
    // Layout
    // ─────────────────────────────────────────────────────────────────

    /// <summary>The disc is 150 px, or the diagonal of its text where a language needs more.</summary>
    private double MeasureDisc()
    {
        DiscContent.Measure(new Size(DiscTextWidth + DiscContent.Margin.Left + DiscContent.Margin.Right, double.PositiveInfinity));
        var d = DiscContent.DesiredSize;
        return Math.Max(QuickSwitchLayout.DiscSize, Math.Ceiling(Math.Sqrt(d.Width * d.Width + d.Height * d.Height)) + 4);
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
            Disc.CornerRadius = (CornerRadius)FindResource("Fx.FlyoutRadius");
            Disc.Margin = new Thickness(0, 0, 0, QuickSwitchLayout.Gap);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Card factory
    // ─────────────────────────────────────────────────────────────────

    private void CreateCard(string name, int index, string? system)
    {
        var numberText = new TextBlock
        {
            FontFamily = TryFindResource("Fx.MonoFont") as FontFamily ?? new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = FluentPalette.Brush("Fx.Text"),
        };
        var number = new Border
        {
            Background = FluentPalette.Brush("Fx.Control"),
            BorderBrush = FluentPalette.Brush("Fx.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = numberText,
        };
        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = FluentPalette.Brush("Fx.Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var systemText = new TextBlock
        {
            Text = system ?? "",
            FontSize = 12,
            Foreground = FluentPalette.Brush("Fx.TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = string.IsNullOrWhiteSpace(system) ? Visibility.Collapsed : Visibility.Visible,
        };
        var card = new Border
        {
            Width = QuickSwitchLayout.CardWidth,
            Height = QuickSwitchLayout.CardHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new StackPanel { Children = { number, nameText, systemText } },
        };

        var entry = new CardEntry { Name = name, Card = card, Number = number, NumberText = numberText, Index = index };
        SetLook(entry, CardLook.Normal);
        _cards.Add(entry);

        // Normal-mode interactions
        card.MouseEnter += (_, _) => { if (!_editMode) SetLook(entry, CardLook.Hover); };
        card.MouseLeave += (_, _) => { if (!_editMode) SetLook(entry, CardLook.Normal); };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (_editMode)
                BeginDrag(entry, e);
            else
            {
                e.Handled = true;
                CharacterSelected?.Invoke(entry.Name);
                CloseWheel();
            }
        };

        ButtonCanvas.Children.Add(card);
    }

    private static void SetLook(CardEntry e, CardLook look)
    {
        bool lit = look is CardLook.Hover or CardLook.Drag;
        e.Card.Background = FluentPalette.Brush(lit ? "Fx.AccentSoft" : "Fx.CardColor");
        e.Card.BorderBrush = FluentPalette.Brush(look == CardLook.Normal ? "Fx.FlyoutBorder" : "Fx.Accent");
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
