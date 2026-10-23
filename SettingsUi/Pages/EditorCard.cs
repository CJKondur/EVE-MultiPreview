using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using Panel = System.Windows.Controls.Panel;
using Slider = System.Windows.Controls.Slider;
using TextBox = System.Windows.Controls.TextBox;
using ToolTip = System.Windows.Controls.ToolTip;

namespace EveMultiPreview.SettingsUi;

/// <summary>Building blocks for hand-built editors, styled like the generic cards (spec §6, §7).</summary>
public static class EditorCard
{
    /// <summary>The card frame of the generic templates: label "L.S.{id}", optional description
    /// "L.S.{id}.Desc", a control on the right (optional) and content below it (optional).</summary>
    public static Border Build(string id, UIElement? right, UIElement? below = null)
    {
        NameFrom(right, "L.S." + id);
        var label = new TextBlock();
        label.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Label");
        Loc.SetKey(label, "L.S." + id);
        var desc = new TextBlock();
        desc.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Desc");
        Loc.SetKey(desc, "L.S." + id + ".Desc");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(label);
        text.Children.Add(desc);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(text);
        if (right != null)
        {
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        if (below is FrameworkElement b)
        {
            b.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(b, 1);
            Grid.SetColumnSpan(b, 2);
            grid.Children.Add(b);
        }
        return Frame(grid);
    }

    /// <summary>A plain card frame around any content (a table, one group).</summary>
    public static Border Frame(UIElement child)
    {
        var card = new Border { Child = child };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Card");
        return card;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items)
        {
            if (row.Children.Count > 0 && item is FrameworkElement fe) fe.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(item);
        }
        return row;
    }

    /// <summary>Like <see cref="Row"/>, but the items wrap onto the next line when the page is too narrow for them (a long
    /// translation, the window's smallest width) instead of running past the edge.</summary>
    public static WrapPanel WrapRow(params UIElement[] items)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items)
        {
            if (item is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 8, 2);
            row.Children.Add(item);
        }
        return row;
    }

    public static Button Button(string key, Action click, bool destructive = false)
    {
        var text = new TextBlock();
        Loc.SetKey(text, key);
        if (destructive) text.SetResourceReference(TextBlock.ForegroundProperty, "Fx.CriticalBrush");
        var button = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4) };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Secondary text from a resource key: notes, empty states, column headers.</summary>
    public static TextBlock Note(string key, bool strong = false)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Desc");
        Loc.SetKey(t, key);
        if (strong) t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    /// <summary>Literal text (a character or group name, a system, a key): never translated.</summary>
    public static TextBlock Value(string text, bool strong = false, bool secondary = false)
    {
        // Fx.Value and Fx.ValueSecondary: the sizes of Fx.Label and Fx.Desc, without collapsing empty text as Fx.Desc does.
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, secondary ? "Fx.ValueSecondary" : "Fx.Value");
        if (strong) t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    /// <summary>A label (resource key) before its control, for rows inside a card.</summary>
    public static StackPanel Field(string key, UIElement control)
    {
        NameFrom(control, key);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        label.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Label");
        Loc.SetKey(label, key);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 24, 8) };
        row.Children.Add(label);
        row.Children.Add(control);
        return row;
    }

    /// <summary>A list of names the player chose (the groups): no wider than <paramref name="maxWidth"/>, whatever the
    /// longest name is (a name may be 200 characters). A longer name ends in an ellipsis, in the closed list and in
    /// the open one, and the tooltip carries the whole name (IDEAS P88), wrapped within <see cref="TipWidth"/>: a
    /// name of 200 characters is not one line across the screen. The items are the names themselves.</summary>
    public static void ClipNames(ComboBox list, double maxWidth = 360)
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        name.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding { Converter = WrappedTip.Instance });
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(FrameworkElement.MaxWidthProperty, maxWidth - 32);   // 32: the list's arrow and its edges
        list.MaxWidth = maxWidth;
        list.ItemTemplate = new DataTemplate { VisualTree = name };
    }

    /// <summary>The widest a name's tooltip is; a longer name wraps.</summary>
    public const double TipWidth = 480;

    /// <summary>A name as a tooltip whose text wraps (a plain string tooltip is one line however long it is).</summary>
    private sealed class WrappedTip : System.Windows.Data.IValueConverter
    {
        public static readonly WrappedTip Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is string text
                ? new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = TipWidth } }
                : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>Gives a control that can't name itself (a box, list, slider, switch or swatch) the label's screen-reader
    /// name, and does the same for each such control in a panel. A control the code already named, or a button with
    /// text, is left alone (Plan 3d).</summary>
    public static void NameFrom(UIElement? element, string key)
    {
        switch (element)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children) NameFrom(child, key);
                break;
            case Control c when NeedsName(c):
                Loc.SetNameKey(c, key);
                break;
        }
    }

    private static bool NeedsName(Control c)
    {
        if (!string.IsNullOrEmpty(AutomationProperties.GetName(c)) || Loc.GetNameKey(c) != null) return false;
        return c switch
        {
            TextBox or ComboBox or Slider or ListBox => true,
            // A switch has no text of its own and a swatch shows only a colour; a button with text names itself.
            ButtonBase b => b.Content is null or Border && Loc.GetKey(b) == null,
            _ => false,
        };
    }

    /// <summary>"Show: Logi": a table cell's column name and the row's character, for screen readers.</summary>
    public static string ForName(string labelKey, string name) =>
        string.Format(SettingsContext.Str("L.S.A11y.ForName"), SettingsContext.Str(labelKey), name);

    /// <summary>The Fx toggle switch. The initial state is set before the handlers, so building
    /// one never reports a change.</summary>
    public static CheckBox Toggle(bool on, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        box.SetResourceReference(FrameworkElement.StyleProperty, "Fx.ToggleSwitch");
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    /// <summary>A key shown like the hotkey cards ("Ctrl + F1", or "Not set"); click to capture.</summary>
    public static Button HotkeyButton(string ahk, Action click)
    {
        var text = new TextBlock();
        text.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.MonoFont");
        var display = HotkeyText.ToDisplay(ahk);
        if (display.Length > 0) text.Text = display;
        else Loc.SetKey(text, "L.S.Hotkey.None");
        var button = new Button { Content = text };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Fx.KeyCap");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A colour swatch button like the colour cards'; click to pick.</summary>
    public static Button Swatch(string? color, Action click)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22,
            Background = Setting.IsColor(color) ? new SolidColorBrush(AppSettings.ParseColor(color!)) : Brushes.Transparent,
        };
        swatch.SetResourceReference(Border.CornerRadiusProperty, "Fx.Radius.Control");
        swatch.SetResourceReference(Border.BorderThicknessProperty, "Fx.Line.Control");
        swatch.SetResourceReference(Border.BorderBrushProperty, "Fx.BorderBrush");
        var button = new Button { Content = swatch };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Swatch");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A name chip ("Logi ✕"). With <paramref name="remove"/>, the ✕ removes it.</summary>
    public static Border Chip(string text, Action? remove)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Value(text));
        if (remove != null)
        {
            var x = new Button
            {
                Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            };
            x.SetResourceReference(Control.ForegroundProperty, "Fx.TextSecondaryBrush");
            // Without a border a look's focus (a border colour) cannot show: WPF's focus adorner draws a ring (fix round C).
            x.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "Base.FocusRing");
            AutomationProperties.SetName(x, SettingsContext.Str("L.S.Chip.Remove") + " " + text);
            x.Click += (_, _) => remove();
            row.Children.Add(x);
        }
        var chip = new Border { Child = row };
        chip.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Chip");
        return chip;
    }

    /// <summary>The "›" button that opens a page (a character's). Screen readers read the name.</summary>
    public static Button Open(string accessibleName, Action click)
    {
        var button = new Button { Content = "›", ToolTip = accessibleName };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Fx.IconButton");
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) => click();
        return button;
    }
}
