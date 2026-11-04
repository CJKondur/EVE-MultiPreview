using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace EveMultiPreview.Linux;

/// <summary>The on/off control of a setting card in the look drawn (Fx.ToggleSwitch): the kit's check box (an edged box,
/// filled with Fx.CheckFill and ticked in Fx.CheckMark when on) or a switch (a track in Fx.Border, Fx.CheckFill when on,
/// its knob in Fx.TextSecondary, Fx.CheckMark when on). A ToggleButton with a template of its own, so the Simple theme's
/// check box does not decide its colours. Static: nothing animates.</summary>
internal sealed class LookToggle : ToggleButton
{
    private readonly LinuxLook _look;
    private Border? _box, _track, _knob;
    private Path? _tick;

    public LookToggle()
    {
        _look = LinuxLooks.Effective;
        Template = new FuncControlTemplate<LookToggle>((_, _) => _look.Toggle == ToggleShape.Switch ? Switch() : Box());
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent;
        Focusable = true;
        IsCheckedChanged += (_, _) => Show();
        PointerEntered += (_, _) => Show();
        PointerExited += (_, _) => Show();
        GotFocus += (_, _) => Show();
        LostFocus += (_, _) => Show();
    }

    private Control Box()
    {
        var size = Math.Max(16, _look.Number("Size.Body") + 4);
        _tick = new Path
        {
            Data = Geometry.Parse("M 0,5 L 4,9 L 12,0"), Stretch = Stretch.Uniform, Margin = new Thickness(3),
            StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        };
        _box = new Border
        {
            Width = size, Height = size, CornerRadius = _look.Radius("Radius.Check"), BorderThickness = Thick(_look.Thickness("Line.Control")),
            Child = _tick,
        };
        Show();
        return new Border { Background = Brushes.Transparent, Padding = new Thickness(2), Child = _box };
    }

    private Control Switch()
    {
        _knob = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center };
        _track = new Border { Width = 40, Height = 20, CornerRadius = new CornerRadius(10), Child = _knob, BorderThickness = new Thickness(1) };
        Show();
        return new Border { Background = Brushes.Transparent, Padding = new Thickness(2), Child = _track };
    }

    /// <summary>A line of 0 still shows the box: the kit draws the check box's edge at least 1 px.</summary>
    private static Thickness Thick(Thickness t) => t.Left <= 0 ? new Thickness(1) : t;

    private void Show()
    {
        bool on = IsChecked == true;
        bool hot = IsPointerOver || IsFocused;
        if (_box != null && _tick != null)
        {
            _box.Background = on ? _look.Brush("CheckFill") : Brushes.Transparent;
            _box.BorderBrush = on ? _look.Brush("CheckFill") : hot ? _look.Brush("Accent") : _look.Brush("CheckBorder");
            _tick.Stroke = _look.Brush("CheckMark");
            _tick.IsVisible = on;
        }
        if (_track != null && _knob != null)
        {
            _track.Background = on ? _look.Brush("CheckFill") : _look.Brush("Border");
            _track.BorderBrush = hot ? _look.Brush("Focus") : on ? _look.Brush("CheckFill") : _look.Brush("Border");
            _knob.Background = on ? _look.Brush("CheckMark") : _look.Brush("TextSecondary");
            _knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }
}
