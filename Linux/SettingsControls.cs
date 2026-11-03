using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>The small controls the settings pages are made of. Each takes the value to show and a callback for a change;
/// the value is set before the callback is attached, so building a page changes nothing.</summary>
internal static class Ui
{
    public static string T(string key, string english) => LocalizationService.Str(key, english);

    /// <summary>A string without the emoji the Windows app puts before a button's text.</summary>
    public static string Plain(string text) => new(text.SkipWhile(c => !char.IsLetter(c)).ToArray());

    /// <summary>A label without the access key the Windows app marks with "_": "Save _link" and, in Japanese, Korean and
    /// Chinese, "(_L)" or "（_L）" after the text.</summary>
    public static string NoAccess(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"[（(]_[^）)]{1,2}[）)]", "").Replace("_", "");

    public static Control Heading(string text) =>
        new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    public static Control Section(string text) =>
        new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Opacity = 0.85, Margin = new Thickness(0, 18, 0, 4) };

    public static Control Note(string text) =>
        new TextBlock { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    /// <summary>A label on the left, the control on the right, and an optional grey hint under the control.</summary>
    public static Control Row(string label, Control input, string? hint = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), Margin = new Thickness(0, 4) };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
        input.HorizontalAlignment = HorizontalAlignment.Left;
        input.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(input, 1);
        if (hint == null) grid.Children.Add(input);
        else
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(input);
            stack.Children.Add(new TextBlock { Text = hint, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);
        }
        return grid;
    }

    public static CheckBox Check(string label, bool value, Action<bool> set)
    {
        // A TextBlock, not the string: a CheckBox reads an underscore in its text as an access key and drops it (mine_cargo_full).
        var box = new CheckBox { Content = label.Length > 0 ? new TextBlock { Text = label } : null, IsChecked = value };
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return box;
    }

    /// <summary>Runs <paramref name="run"/> with the new value each time <paramref name="property"/> changes.</summary>
    public static void Watch<T>(AvaloniaObject target, AvaloniaProperty<T> property, Action<T> run) =>
        target.PropertyChanged += (_, e) => { if (e.Property == property && e.GetNewValue<T>() is { } v) run(v); };

    /// <summary>A whole-number box with arrows.</summary>
    public static NumericUpDown Number(int min, int max, double value, Action<int> set)
    {
        var box = new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 130, Value = (decimal)Math.Clamp(value, min, max) };
        Watch(box, NumericUpDown.ValueProperty, v => { if (v is { } n) set((int)n); });
        return box;
    }

    /// <summary>A slider with its value written beside it.</summary>
    public static Control Slider(int min, int max, int value, Action<int> set, Func<int, string>? show = null)
    {
        show ??= v => v.ToString();
        var label = new TextBlock { Width = 90, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var slider = new Slider { Minimum = min, Maximum = max, Width = 240, Value = Math.Clamp(value, min, max), TickFrequency = 1, IsSnapToTickEnabled = true };
        label.Text = show((int)slider.Value);
        Watch(slider, Avalonia.Controls.Primitives.RangeBase.ValueProperty, v => { label.Text = show((int)v); set((int)v); });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(label);
        return row;
    }

    /// <summary>A text box that reports every change.</summary>
    public static TextBox Text(string value, Action<string> set, double width = 320)
    {
        var box = new TextBox { Text = value, Width = width };
        Watch(box, TextBox.TextProperty, v => set(v ?? ""));
        return box;
    }

    /// <summary>A "#RRGGBB" box with a swatch. Only a colour the thumbnails can paint is passed on, as "#RRGGBB"; text
    /// that isn't one shows red and changes nothing.</summary>
    public static Control Colour(string value, Action<string> set)
    {
        var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var box = new TextBox { Text = Show(value), Width = 110, MaxLength = 9 };
        swatch.Background = Paint(box.Text);
        Watch(box, TextBox.TextProperty, text =>
        {
            if (!ThumbnailFields.IsPaintable(text))
            {
                box.Foreground = Brushes.IndianRed;
                return;
            }
            box.ClearValue(TextBox.ForegroundProperty);
            swatch.Background = Paint(text);
            set(Hex(text ?? ""));
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(swatch);
        row.Children.Add(box);
        return row;
    }

    /// <summary>"#RRGGBB" for a stored colour ("0xrrggbb", "RRGGBB" and "#RRGGBB" are all kept by the Windows app).</summary>
    public static string Hex(string colour)
    {
        var t = colour.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return "#" + t.TrimStart('#').ToUpperInvariant();
    }

    private static string Show(string stored) => ThumbnailFields.IsPaintable(stored) ? Hex(stored) : stored;

    private static IBrush Paint(string? colour)
    {
        var c = AppSettings.ParseColor(colour ?? "", System.Windows.Media.Color.FromRgb(128, 128, 128));
        return new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    /// <summary>A hotkey box with a small clear button.</summary>
    public static StackPanel Hotkey(string value, Action<string> set, ISettingsHost host)
    {
        var box = new HotkeyBox(value);
        box.Changed += set;
        box.Capturing += host.CaptureHotkey;
        var clear = new Button { Content = "×", Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(clear, T("L.S.Hotkey.Clear", "Clear"));
        clear.Click += (_, _) => { box.Value = ""; set(""); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(box);
        row.Children.Add(clear);
        return row;
    }

    /// <summary>A card: a faint panel the rows of one block sit in.</summary>
    public static Border Card(Control inner) => new()
    {
        Background = new SolidColorBrush(Color.Parse("#14FFFFFF")),
        BorderBrush = new SolidColorBrush(Color.Parse("#22FFFFFF")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 8),
        Margin = new Thickness(0, 4),
        Child = inner,
    };
}
