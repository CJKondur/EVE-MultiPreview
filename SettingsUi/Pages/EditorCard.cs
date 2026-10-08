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

namespace EveMultiPreview.SettingsUi;

/// <summary>Building blocks for hand-built editors, styled like the generic cards (spec §6, §7).</summary>
public static class EditorCard
{
    /// <summary>The card frame of the generic templates: label "L.S.{id}", optional description
    /// "L.S.{id}.Desc", a control on the right (optional) and content below it (optional).</summary>
    public static Border Build(string id, UIElement? right, UIElement? below = null)
    {
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
        // The Fx.Label / Fx.Desc sizes, without their styles (Fx.Desc collapses empty text).
        var t = new TextBlock { Text = text, FontSize = secondary ? 12 : 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "Fx.TextSecondaryBrush" : "Fx.TextBrush");
        if (strong) t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    /// <summary>A label (resource key) before its control, for rows inside a card.</summary>
    public static StackPanel Field(string key, UIElement control)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        label.SetResourceReference(FrameworkElement.StyleProperty, "Fx.Label");
        Loc.SetKey(label, key);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 24, 8) };
        row.Children.Add(label);
        row.Children.Add(control);
        return row;
    }

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
        var button = new Button { Content = text, MinWidth = 120, Padding = new Thickness(12, 4, 12, 4) };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A colour swatch button like the colour cards'; click to pick.</summary>
    public static Button Swatch(string? color, Action click)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = Setting.IsColor(color) ? new SolidColorBrush(AppSettings.ParseColor(color!)) : Brushes.Transparent,
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Fx.BorderBrush");
        var button = new Button { Content = swatch, Width = 32, Height = 32, Padding = new Thickness(0) };
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
            AutomationProperties.SetName(x, SettingsContext.Str("L.S.Chip.Remove") + " " + text);
            x.Click += (_, _) => remove();
            row.Children.Add(x);
        }
        var chip = new Border
        {
            Child = row, Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Fx.ControlBrush");
        chip.SetResourceReference(Border.BorderBrushProperty, "Fx.BorderBrush");
        return chip;
    }

    /// <summary>The "›" button that opens a page (a character's). Screen readers read the name.</summary>
    public static Button Open(string accessibleName, Action click)
    {
        var button = new Button { Content = "›", Padding = new Thickness(10, 2, 10, 2), ToolTip = accessibleName };
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) => click();
        return button;
    }
}
