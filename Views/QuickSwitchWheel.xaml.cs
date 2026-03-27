using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using Color          = System.Windows.Media.Color;
using Cursors        = System.Windows.Input.Cursors;
using FontFamily     = System.Windows.Media.FontFamily;
using KeyEventArgs   = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel          = System.Windows.Controls.Panel;
using Point          = System.Windows.Point;

namespace EveMultiPreview.Views;

/// <summary>
/// Quick-switch card grid for fast character selection.
/// Normal mode: click or press 1-9 to switch. Hotkey again closes.
/// Edit mode: drag cards to reorder, then Save.
/// </summary>
public partial class QuickSwitchWheel : Window
{
    // ── Events ────────────────────────────────────────────────────────
    public event Action<string>? CharacterSelected;
    public event Action<List<string>>? OrderSaved;

    // ── Card state ────────────────────────────────────────────────────
    private readonly List<CardEntry> _cards = new();
    private bool _editMode = false;

    // ── Drag state ────────────────────────────────────────────────────
    private CardEntry? _dragCard;
    private Point _dragOffset;       // cursor pos relative to card top-left
    private int _dragOriginalIndex;

    // ── Layout constants ──────────────────────────────────────────────
    private const double CardW = 160;
    private const double CardH = 46;
    private const double CardGap = 7;
    private int _cols;
    private int _rows;

    // ── Colors ────────────────────────────────────────────────────────
    private static readonly SolidColorBrush BgNormal   = new(Color.FromArgb(0xFF, 0x24, 0x24, 0x34));
    private static readonly SolidColorBrush BgHover    = new(Color.FromArgb(0xFF, 0x3a, 0x6a, 0xaa));
    private static readonly SolidColorBrush BgEdit     = new(Color.FromArgb(0xFF, 0x30, 0x20, 0x48));
    private static readonly SolidColorBrush BgDrag     = new(Color.FromArgb(0xCC, 0x55, 0x33, 0xaa));
    private static readonly SolidColorBrush FgNormal   = new(Color.FromRgb(0xfa, 0xc5, 0x7a));
    private static readonly SolidColorBrush FgHover    = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush BorderEdit = new(Color.FromArgb(0xA0, 0xaa, 0x55, 0xff));

    // ── Inner class ───────────────────────────────────────────────────
    private class CardEntry
    {
        public string   Name   { get; set; } = "";
        public Border   Card   { get; set; } = null!;
        public TextBlock Label { get; set; } = null!;
        public int      Index  { get; set; }   // logical grid position
    }

    public QuickSwitchWheel()
    {
        InitializeComponent();
        KeyDown      += OnKeyDown;
        Deactivated  += (_, _) => { if (!_editMode) CloseWheel(); };
    }

    // ─────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────

    public void ShowWheel(List<string> characters)
    {
        _cards.Clear();
        ButtonCanvas.Children.Clear();
        _editMode = false;

        int count = characters.Count;
        TxtCount.Text = $"{count} client{(count != 1 ? "s" : "")}";
        UpdateEditButton();

        _cols = count <= 4 ? 2 : 3;
        _rows = (int)Math.Ceiling((double)count / _cols);

        double gridW = _cols * CardW + (_cols - 1) * CardGap;
        double gridH = _rows * CardH + (_rows - 1) * CardGap;
        ButtonCanvas.Width  = gridW;
        ButtonCanvas.Height = gridH;

        for (int i = 0; i < count; i++)
            CreateCard(characters[i], i);

        PositionAllCards();

        Opacity = 0;
        Show();
        Focus();
        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            .Let(a => BeginAnimation(OpacityProperty, a));
    }

    /// <summary>Force-close without the Deactivated guard blocking.Called by ThumbnailManager toggle.</summary>
    public void ForceClose() => CloseWheel();

    // ─────────────────────────────────────────────────────────────────
    // Card factory
    // ─────────────────────────────────────────────────────────────────

    private void CreateCard(string name, int index)
    {
        var label = new TextBlock
        {
            Text              = NumberPrefix(index, name),
            Foreground        = FgNormal,
            FontSize          = 12.5,
            FontFamily        = new FontFamily("Segoe UI"),
            FontWeight        = FontWeights.SemiBold,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxWidth          = CardW - 22,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Center,
        };

        var card = new Border
        {
            Background      = BgNormal,
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0x8a, 0xba)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(7),
            Width           = CardW,
            Height          = CardH,
            Child           = label,
            Cursor          = Cursors.Hand,
        };

        var entry = new CardEntry { Name = name, Card = card, Label = label, Index = index };
        _cards.Add(entry);

        // Normal-mode interactions
        card.MouseEnter       += (_, _) => { if (!_editMode) SetHoverStyle(entry, true); };
        card.MouseLeave       += (_, _) => { if (!_editMode) SetHoverStyle(entry, false); };
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

    // ─────────────────────────────────────────────────────────────────
    // Layout helpers
    // ─────────────────────────────────────────────────────────────────

    private void PositionAllCards()
    {
        foreach (var e in _cards)
        {
            (double x, double y) = GridPos(e.Index);
            Canvas.SetLeft(e.Card, x);
            Canvas.SetTop(e.Card, y);
            Panel.SetZIndex(e.Card, 0);
        }
    }

    private (double x, double y) GridPos(int index)
    {
        int col = index % _cols;
        int row = index / _cols;
        return (col * (CardW + CardGap), row * (CardH + CardGap));
    }

    private static string NumberPrefix(int index, string name)
        => index < 9 ? $"{index + 1}  {name}" : name;

    private void RefreshLabels()
    {
        foreach (var e in _cards)
            e.Label.Text = NumberPrefix(e.Index, e.Name);
    }

    // ─────────────────────────────────────────────────────────────────
    // Hover style
    // ─────────────────────────────────────────────────────────────────

    private static void SetHoverStyle(CardEntry e, bool hovered)
    {
        e.Card.Background    = hovered ? BgHover : BgNormal;
        e.Label.Foreground   = hovered ? FgHover : FgNormal;
    }

    // ─────────────────────────────────────────────────────────────────
    // Edit mode
    // ─────────────────────────────────────────────────────────────────

    private void ToggleEditMode()
    {
        _editMode = !_editMode;
        UpdateEditButton();

        if (_editMode)
        {
            // Visual cue: tint cards purple, add dashed border
            foreach (var e in _cards)
            {
                e.Card.Background    = BgEdit;
                e.Card.BorderBrush   = BorderEdit;
                e.Card.BorderThickness = new Thickness(1.5);
                e.Card.Cursor        = Cursors.SizeAll;
            }
            TxtCount.Text = "Drag to reorder — then Save";
        }
        else
        {
            // Restore normal style
            var normalBorder = new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0x8a, 0xba));
            foreach (var e in _cards)
            {
                e.Card.Background    = BgNormal;
                e.Card.BorderBrush   = normalBorder;
                e.Card.BorderThickness = new Thickness(1);
                e.Card.Cursor        = Cursors.Hand;
            }
            TxtCount.Text = $"{_cards.Count} client{(_cards.Count != 1 ? "s" : "")}";
        }
    }

    private void UpdateEditButton()
    {
        if (BtnEdit == null) return;
        BtnEdit.Content = _editMode ? "💾 Save" : "✏ Edit";
        BtnEdit.ToolTip = _editMode ? "Save card order" : "Edit card order";
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_editMode)
        {
            // Save: emit order
            var order = _cards.OrderBy(c => c.Index).Select(c => c.Name).ToList();
            OrderSaved?.Invoke(order);
            ToggleEditMode();
        }
        else
        {
            ToggleEditMode();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Drag-to-reorder
    // ─────────────────────────────────────────────────────────────────

    private void BeginDrag(CardEntry entry, MouseButtonEventArgs e)
    {
        _dragCard         = entry;
        _dragOriginalIndex = entry.Index;
        _dragOffset       = e.GetPosition(entry.Card);

        // Lift card above others
        Panel.SetZIndex(entry.Card, 99);
        entry.Card.Background = BgDrag;
        entry.Card.Opacity    = 0.85;

        ButtonCanvas.MouseMove       += OnDragMove;
        ButtonCanvas.MouseLeftButtonUp += OnDragEnd;
        ButtonCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragCard == null) return;

        var pos = e.GetPosition(ButtonCanvas);
        double left = pos.X - _dragOffset.X;
        double top  = pos.Y - _dragOffset.Y;

        // Clamp within canvas
        left = Math.Max(0, Math.Min(left, ButtonCanvas.Width  - CardW));
        top  = Math.Max(0, Math.Min(top,  ButtonCanvas.Height - CardH));

        Canvas.SetLeft(_dragCard.Card, left);
        Canvas.SetTop(_dragCard.Card, top);

        // Find which slot we're hovering over
        int hoverIndex = HitTestSlot(pos);
        if (hoverIndex >= 0 && hoverIndex != _dragCard.Index)
            SwapCards(_dragCard, hoverIndex);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        ButtonCanvas.MouseMove        -= OnDragMove;
        ButtonCanvas.MouseLeftButtonUp -= OnDragEnd;
        ButtonCanvas.ReleaseMouseCapture();

        if (_dragCard == null) return;

        // Snap card to its final grid position
        (double x, double y) = GridPos(_dragCard.Index);
        Canvas.SetLeft(_dragCard.Card, x);
        Canvas.SetTop(_dragCard.Card, y);
        Panel.SetZIndex(_dragCard.Card, 0);
        _dragCard.Card.Background = BgEdit;
        _dragCard.Card.Opacity    = 1.0;
        _dragCard = null;
    }

    /// <summary>Return the grid slot index nearest to the given canvas point, or -1.</summary>
    private int HitTestSlot(Point canvasPos)
    {
        for (int i = 0; i < _cols * _rows; i++)
        {
            (double sx, double sy) = GridPos(i);
            if (canvasPos.X >= sx && canvasPos.X < sx + CardW &&
                canvasPos.Y >= sy && canvasPos.Y < sy + CardH)
                return i;
        }
        return -1;
    }

    /// <summary>Swap the dragged card into targetIndex, shifting the card currently there.</summary>
    private void SwapCards(CardEntry drag, int targetIndex)
    {
        // Find the card currently at targetIndex
        var displaced = _cards.FirstOrDefault(c => c.Index == targetIndex && c != drag);
        if (displaced == null) return;

        // Swap indices
        displaced.Index = drag.Index;
        drag.Index      = targetIndex;

        // Animate displaced card to its new position
        (double dx, double dy) = GridPos(displaced.Index);
        Canvas.SetLeft(displaced.Card, dx);
        Canvas.SetTop(displaced.Card, dy);

        // Update number labels
        RefreshLabels();
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

// ── Extension helper ─────────────────────────────────────────────────
file static class AnimExt
{
    public static T Let<T>(this T self, Action<T> action) { action(self); return self; }
}
