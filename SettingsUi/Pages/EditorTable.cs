using System;
using System.Windows;
using System.Windows.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>The table of a hand-built editor (UI styles spec §5.4). The table (Fx.Table), a border behind each row
/// (Fx.Table.Row; the header row's is Fx.Table.Header) and behind each cell (Fx.Table.Cell) are elements a look can style:
/// lines, a header bar, a grid. The cells themselves stay the editor's elements, direct children of <see cref="Body"/>
/// with their own row and column, and keep the margin and alignment the editor gives them.</summary>
public sealed class EditorTable : Border
{
    public Grid Body { get; } = new();

    public EditorTable()
    {
        Child = Body;
        SetResourceReference(StyleProperty, "Fx.Table");
    }

    /// <summary>Empties the table and gives it these columns.</summary>
    public void Reset(params GridLength[] columns)
    {
        Body.Children.Clear();
        Body.RowDefinitions.Clear();
        Body.ColumnDefinitions.Clear();
        foreach (var width in columns) Body.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
    }

    /// <summary>A new row with its border behind the cells, across every column. Returns the row's index.</summary>
    public int AddRow(bool header = false)
    {
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = Body.RowDefinitions.Count - 1;
        var back = new Border { IsHitTestVisible = false };
        back.SetResourceReference(StyleProperty, header ? "Fx.Table.Header" : "Fx.Table.Row");
        Grid.SetRow(back, row);
        Grid.SetColumnSpan(back, Math.Max(1, Body.ColumnDefinitions.Count));
        Body.Children.Add(back);
        return row;
    }

    /// <summary>Puts <paramref name="element"/> into a cell, over the cell's own border.</summary>
    public void Place(FrameworkElement element, int row, int column, int span = 1)
    {
        var cell = new Border { IsHitTestVisible = false };
        cell.SetResourceReference(StyleProperty, "Fx.Table.Cell");
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        Grid.SetColumnSpan(cell, span);
        Body.Children.Add(cell);
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        if (span > 1) Grid.SetColumnSpan(element, span);
        Body.Children.Add(element);
    }

    /// <summary>A column's heading, in Fx.Caption.</summary>
    public static TextBlock Head(string key)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(StyleProperty, "Fx.Caption");
        Loc.SetKey(text, key);
        return text;
    }
}
