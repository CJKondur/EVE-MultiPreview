using System;
using System.Collections.Generic;
using System.Linq;
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
        foreach (var back in _backs) System.Windows.Data.BindingOperations.ClearBinding(back, VisibilityProperty);
        _backs.Clear();
        _backRows.Clear();
        _cells.Clear();
        _cellRows.Clear();
        Body.Children.Clear();
        Body.RowDefinitions.Clear();
        Body.ColumnDefinitions.Clear();
        foreach (var width in columns) Body.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
    }

    // Per row: the borders behind it (the row's and its cells'), and the elements placed in it.
    private readonly List<Border> _backs = new();
    private readonly List<int> _backRows = new();
    private readonly List<FrameworkElement> _cells = new();
    private readonly List<int> _cellRows = new();

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
        Behind(back, row);
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
        Behind(cell, row);
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        if (span > 1) Grid.SetColumnSpan(element, span);
        Body.Children.Add(element);
        _cells.Add(element);
        _cellRows.Add(row);
        Follow(row);
    }

    private void Behind(Border back, int row)
    {
        _backs.Add(back);
        _backRows.Add(row);
        Follow(row);
    }

    /// <summary>The borders of a row are shown while one element in it is (spec §5.4; the S1 carry): a row whose cells are
    /// all collapsed is gone whole, its line and its height too, whoever collapsed them. A binding, not an event: a cell
    /// collapsed while the page is hidden is followed as well.</summary>
    private void Follow(int row)
    {
        var shown = new System.Windows.Data.MultiBinding { Converter = AnyShown.One };
        for (int i = 0; i < _cells.Count; i++)
            if (_cellRows[i] == row) shown.Bindings.Add(new System.Windows.Data.Binding(nameof(Visibility)) { Source = _cells[i] });
        for (int i = 0; i < _backs.Count; i++)
            if (_backRows[i] == row)
            {
                if (shown.Bindings.Count == 0) _backs[i].ClearValue(VisibilityProperty);
                else _backs[i].SetBinding(VisibilityProperty, shown);
            }
    }

    private sealed class AnyShown : System.Windows.Data.IMultiValueConverter
    {
        public static readonly AnyShown One = new();
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            values.Any(v => v is Visibility.Visible or Visibility.Hidden) ? Visibility.Visible : Visibility.Collapsed;
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
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
