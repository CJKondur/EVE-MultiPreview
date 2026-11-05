using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveMultiPreview.Models;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>The Linux colour picker (the Windows host's PickColor shows WinForms' colour dialog; Avalonia 12's ColorPicker
/// is a package the Linux app does not take): a row of basic colours, the player's own colours (AppSettings.CustomColorPalette,
/// shared with the Windows dialog's custom colours), red/green/blue sliders and a "#RRGGBB" box, with a preview. The
/// answer is "#RRGGBB", as the Windows dialog's. Static controls: nothing runs while it is closed.</summary>
internal sealed class ColourPicker : StackPanel
{
    /// <summary>The basic colours (the first row of the Windows dialog's, by name).</summary>
    private static readonly Color[] Basic =
    {
        Colors.White, Colors.Silver, Colors.Gray, Colors.Black, Colors.Red, Colors.Maroon, Colors.Orange, Colors.Yellow,
        Colors.Olive, Colors.Lime, Colors.Green, Colors.Cyan, Colors.Teal, Colors.DodgerBlue, Colors.Blue, Colors.Navy,
        Colors.Magenta, Colors.Purple, Colors.HotPink, Colors.Gold,
    };

    private readonly Slider[] _channels = new Slider[3];
    private readonly TextBlock[] _numbers = new TextBlock[3];
    private readonly TextBox _hex = new() { Width = 110, MaxLength = 9 };
    private readonly Border _preview = new() { Width = 64, Height = 40, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
    private bool _setting;

    /// <param name="initial">The stored colour ("#RRGGBB", "0xRRGGBB", a name; AppSettings.ParseColor reads it), or null.</param>
    /// <param name="custom">The player's own colours (Win32 COLORREF, 0x00BBGGRR), or null.</param>
    public ColourPicker(string? initial, int[]? custom)
    {
        Spacing = 8;
        var start = Parse(initial) ?? Colors.White;
        Children.Add(Swatches(Basic));
        var own = (custom ?? Array.Empty<int>()).Select(FromColorRef).Distinct().ToArray();
        if (own.Length > 0) Children.Add(Swatches(own));

        var sliders = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        string[] names = { "R", "G", "B" };
        for (int i = 0; i < 3; i++)
        {
            var label = new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var slider = new Slider { Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 220 };
            var number = new TextBlock { MinWidth = 32, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextAlignment = TextAlignment.Right };
            slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) FromSliders(); };
            Grid.SetRow(label, i); Grid.SetRow(slider, i); Grid.SetRow(number, i);
            Grid.SetColumn(slider, 1); Grid.SetColumn(number, 2);
            sliders.Children.Add(label); sliders.Children.Add(slider); sliders.Children.Add(number);
            _channels[i] = slider;
            _numbers[i] = number;
        }
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        bottom.Children.Add(_preview);
        bottom.Children.Add(_hex);
        _hex.VerticalAlignment = VerticalAlignment.Center;
        _hex.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_setting && Parse(_hex.Text) is { } c) Show(c, fromHex: true); };
        var right = new StackPanel { Spacing = 8 };
        right.Children.Add(sliders);
        right.Children.Add(bottom);
        Children.Add(right);
        Show(start, fromHex: false);
    }

    /// <summary>The colour shown now.</summary>
    public Color Picked { get; private set; }

    /// <summary>The colour shown now as the settings store it.</summary>
    public string Hex => Format(Picked);

    public static string Format(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A stored colour as a colour, or null when it is not one.</summary>
    public static Color? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || !ThumbnailFields.IsPaintable(stored)) return null;
        try
        {
            var c = AppSettings.ParseColor(stored!, System.Windows.Media.Colors.Transparent);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception) { return null; }   // ParseColor throws on rgb(300,0,0) (Setting.IsColor catches it the same way)
    }

    /// <summary>The player's own colours with <paramref name="picked"/> first (the Windows dialog's sixteen slots).</summary>
    public static int[] Remember(int[]? custom, Color picked)
    {
        var value = picked.R | (picked.G << 8) | (picked.B << 16);
        var list = (custom ?? Array.Empty<int>()).Where(c => c != value).ToList();
        list.Insert(0, value);
        return list.Take(16).ToArray();
    }

    private static Color FromColorRef(int c) => Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));

    private Control Swatches(IEnumerable<Color> colours)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 300 };
        foreach (var colour in colours)
        {
            // The colour is the face inside the button: a hover repaints the button's frame, never the colour. The face has its
            // own size: the button centres its content, and a Border with no child and no size measures 0 x 0 (a dot).
            var swatch = new Button
            {
                Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 4),
                Content = new Border { Width = 20, Height = 20, Background = new ImmutableSolidColorBrush(colour), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
            };
            ToolTip.SetTip(swatch, Format(colour));
            swatch.Classes.Add("swatch");
            var c = colour;
            swatch.Click += (_, _) => Show(c, fromHex: false);
            row.Children.Add(swatch);
        }
        return row;
    }

    private void FromSliders()
    {
        if (_setting) return;
        Show(Color.FromRgb((byte)_channels[0].Value, (byte)_channels[1].Value, (byte)_channels[2].Value), fromHex: false);
    }

    private void Show(Color c, bool fromHex)
    {
        _setting = true;
        try
        {
            Picked = c;
            byte[] values = { c.R, c.G, c.B };
            for (int i = 0; i < 3; i++)
            {
                _channels[i].Value = values[i];
                _numbers[i].Text = values[i].ToString(CultureInfo.InvariantCulture);
            }
            if (!fromHex) _hex.Text = Format(c);
            _preview.Background = new ImmutableSolidColorBrush(c);
        }
        finally { _setting = false; }
    }

    /// <summary>A swatch button for a colour card: it shows the colour <paramref name="current"/> gives, and opens the
    /// picker under it; OK hands the pick to <paramref name="picked"/>.</summary>
    public static Button Button(Func<string?> current, Func<int[]?> custom, Action<string, Color> picked, string okText, string cancelText)
    {
        var face = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
        var button = new Button { Content = face, Padding = new Thickness(2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("swatch");
        void Paint() => face.Background = Parse(current()) is { } c ? new ImmutableSolidColorBrush(c) : Brushes.Transparent;
        Paint();
        button.Click += (_, _) =>
        {
            var picker = new ColourPicker(current(), custom());
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            var ok = new Button { Content = okText, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = cancelText, IsCancel = true };
            ok.Click += (_, _) => { flyout.Hide(); picked(picker.Hex, picker.Picked); Paint(); };
            cancel.Click += (_, _) => flyout.Hide();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(picker);
            body.Children.Add(buttons);
            flyout.Content = body;
            flyout.ShowAt(button);
        };
        button.Tag = (Action)Paint;   // the card repaints the face after a typed change
        return button;
    }
}
