using System;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace EveMultiPreview.SettingsUi;

/// <summary>Builds the same card frame as the generic templates (label "L.S.{id}", optional
/// description "L.S.{id}.Desc") for hand-built editors.</summary>
public static class EditorCard
{
    public static Border Build(string id, UIElement right, UIElement? below = null)
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
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        if (below is FrameworkElement b)
        {
            b.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(b, 1);
            Grid.SetColumnSpan(b, 2);
            grid.Children.Add(b);
        }

        var card = new Border { Child = grid };
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
}
