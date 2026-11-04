using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace EveMultiPreview.Linux;

/// <summary>A page's sections and rows the way the look draws them (the Linux side of the Windows looks' Fx.Section and
/// Fx.Card, SectionShape and CardShape): the shape from the look's table, every colour by its brush key (it follows a
/// change of look), every metric from the look's tokens.</summary>
internal static class LookSections
{
    /// <summary>What a row's edge goes back to after the search's mark.</summary>
    private sealed record Edge(string Key, Thickness Thickness);

    /// <summary>One row (a card of a setting, an action, a note, an editor).</summary>
    public static Border Row(LinuxLook look, Control inner)
    {
        var (surface, edgeKey, edge, radius) = look.Card switch
        {
            CardShape.Card => ("Fx.CardBrush", "Fx.BorderBrush", look.Thickness("Line.Card"), look.Radius("Radius")),
            CardShape.OnPage => ("Fx.BgBrush", "Fx.DividerBrush", look.Thickness("Line.Row"), default),
            CardShape.InCard => ("Fx.CardBrush", "Fx.DividerBrush", look.Thickness("Line.Row"), default),
            _ => (null, "Fx.DividerBrush", look.Thickness("Line.Row"), default(CornerRadius)),
        };
        var row = new Border
        {
            Child = inner, BorderThickness = edge, CornerRadius = radius, Padding = Inside(look), Margin = look.Thickness("Row.Gap"),
            MinHeight = look.Number("Row.MinHeight"), Tag = new Edge(edgeKey, edge),
        };
        LinuxLooks.Paint(row, Border.BorderBrushProperty, edgeKey);
        if (surface != null)
        {
            LinuxLooks.Paint(row, Border.BackgroundProperty, surface);
            row.PointerEntered += (_, _) => LinuxLooks.Paint(row, Border.BackgroundProperty, "Fx.CardHoverBrush");
            row.PointerExited += (_, _) => LinuxLooks.Paint(row, Border.BackgroundProperty, surface);
        }
        return row;
    }

    /// <summary>A row's padding: the look's (Fx.Row.Padding), with room at the sides where the row has an edge or a surface
    /// of its own so the text never touches it.</summary>
    private static Thickness Inside(LinuxLook look)
    {
        var p = look.Thickness("Row.Padding");
        double side = look.Card == CardShape.Card ? Math.Max(p.Left, 12) : look.Card == CardShape.InCard && look.Section != SectionShape.InCard ? Math.Max(p.Left, 10) : p.Left;
        return new Thickness(side, Math.Max(p.Top, 2), Math.Max(p.Right, side), Math.Max(p.Bottom, 2));
    }

    /// <summary>The search's mark on a row: an edge in the look's focus colour.</summary>
    public static void Mark(Border row)
    {
        LinuxLooks.Paint(row, Border.BorderBrushProperty, "Fx.FocusBrush");
        row.BorderThickness = new Thickness(2);
    }

    public static void Unmark(Border row)
    {
        if (row.Tag is not Edge edge) return;
        LinuxLooks.Paint(row, Border.BorderBrushProperty, edge.Key);
        row.BorderThickness = edge.Thickness;
    }

    /// <summary>A section: its title (and number, where the look numbers them) and its rows, in the look's shape.</summary>
    public static Control Build(LinuxLook look, string title, int ordinal, Control rows)
    {
        var gap = look.Thickness("Section.Gap");
        TextBlock Title(Thickness margin)
        {
            var text = LinuxLooks.Text(title, "Section");
            text.Margin = margin;
            return text;
        }
        var number = ordinal.ToString("00", CultureInfo.InvariantCulture);
        switch (look.Section)
        {
            case SectionShape.Group:
            {
                // The kit's group box: a box with the title set into its top line, on the page colour.
                var box = new Border
                {
                    Margin = new Thickness(0, 9, 0, 0), BorderThickness = AtLeastOne(look.Thickness("Line.Card")), CornerRadius = look.Radius("Radius"),
                    Padding = new Thickness(10, 14, 10, 6), Child = rows,
                };
                LinuxLooks.Paint(box, Border.BorderBrushProperty, "Fx.BorderBrush");
                var label = Title(new Thickness(0));
                var tab = new Border { Child = label, Padding = new Thickness(4, 0), Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                LinuxLooks.Paint(tab, Border.BackgroundProperty, "Fx.BgBrush");
                var grid = new Grid { Margin = gap };
                grid.Children.Add(box);
                grid.Children.Add(tab);
                return grid;
            }
            case SectionShape.InCard:
            {
                // The title, then the rows in one card (Base.Section.InCard; High contrast's own box).
                var card = new Border
                {
                    BorderThickness = look.Thickness("Line.Card"), CornerRadius = look.Radius("Radius"), ClipToBounds = true,
                    Padding = look.Id == "contrast" ? new Thickness(0) : new Thickness(14, 0), Child = rows,
                };
                LinuxLooks.Paint(card, Border.BackgroundProperty, "Fx.CardBrush");
                LinuxLooks.Paint(card, Border.BorderBrushProperty, "Fx.BorderBrush");
                return Stack(Title(gap), card);
            }
            case SectionShape.Ruled:
            {
                var rule = new Border { BorderThickness = look.Thickness("Line.Row"), Padding = new Thickness(0, 0, 0, 6), Child = Title(gap) };
                LinuxLooks.Paint(rule, Border.BorderBrushProperty, "Fx.BorderBrush");
                return Stack(rule, rows);
            }
            case SectionShape.Slashed:
            {
                // "// " in the link colour before the title, both slanted, a line under them (Carbon).
                var slash = LinuxLooks.Text("// ", "Section");
                LinuxLooks.Paint(slash, TextBlock.ForegroundProperty, "Fx.LinkBrush");
                var text = Title(new Thickness(0));
                slash.FontStyle = text.FontStyle = Cjk ? FontStyle.Normal : look.Slant("Style.Title");   // O28: upright in CJK
                var line = new DockPanel { Margin = gap };
                DockPanel.SetDock(slash, Dock.Left);
                line.Children.Add(slash);
                line.Children.Add(text);
                var rule = new Border { BorderThickness = look.Thickness("Line.Row"), Padding = new Thickness(0, 0, 0, 3), Child = line };
                LinuxLooks.Paint(rule, Border.BorderBrushProperty, "Fx.ControlEdgeBrush");
                return Stack(rule, rows);
            }
            case SectionShape.Numbered:
            {
                // Its number in a 22 px circle, its title, a line to the right edge (Blueprint).
                var circle = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                circle.Children.Add(LinuxLooks.Paint(new Ellipse { StrokeThickness = 1 }, Shape.StrokeProperty, "Fx.TextBrush"));
                var digits = new TextBlock
                {
                    Text = ordinal.ToString(CultureInfo.InvariantCulture), FontFamily = look.Font("MonoFont"), FontSize = look.Number("Size.Caption"),
                    FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                circle.Children.Add(LinuxLooks.Paint(digits, TextBlock.ForegroundProperty, "Fx.TextBrush"));
                var text = Title(new Thickness(0, 0, 10, 0));
                text.VerticalAlignment = VerticalAlignment.Center;
                text.TextWrapping = TextWrapping.NoWrap;
                var line = LinuxLooks.Paint(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center }, Border.BackgroundProperty, "Fx.BorderBrush");
                var head = new DockPanel { Margin = gap };
                DockPanel.SetDock(circle, Dock.Left);
                DockPanel.SetDock(text, Dock.Left);
                head.Children.Add(circle);
                head.Children.Add(text);
                head.Children.Add(line);
                return Stack(head, rows);
            }
            case SectionShape.Panel:
            {
                // A framed panel on the card colour, its header bar on top (Capsule's fold, always open here).
                var header = new Border { Padding = new Thickness(10, 6), Child = Title(new Thickness(0)) };
                LinuxLooks.Paint(header, Border.BackgroundProperty, "Fx.HeaderBrush");
                rows.Margin = new Thickness(10, 6, 10, 8);
                var panel = new Border { Margin = gap, BorderThickness = look.Thickness("Line.Card"), Child = Stack(header, rows) };
                LinuxLooks.Paint(panel, Border.BackgroundProperty, "Fx.CardBrush");
                LinuxLooks.Paint(panel, Border.BorderBrushProperty, "Fx.BorderBrush");
                return panel;
            }
            case SectionShape.Columns:
            {
                // Two columns: the number, small, over the title in bold; the rows beside them; a rule under the section (Ink).
                var left = new StackPanel { Margin = new Thickness(0, 10, 16, 0) };
                var count = LinuxLooks.Paint(new TextBlock { Text = number, FontSize = look.Number("Size.Caption") }, TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
                left.Children.Add(count);
                left.Children.Add(Title(new Thickness(0)));
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*") };
                grid.Children.Add(left);
                Grid.SetColumn(rows, 1);
                grid.Children.Add(rows);
                var rule = new Border { BorderThickness = look.Thickness("Line.Row"), Padding = gap, Child = grid };
                LinuxLooks.Paint(rule, Border.BorderBrushProperty, "Fx.BorderBrush");
                return rule;
            }
            default:
                return Stack(Title(gap), rows);
        }
    }

    private static bool Cjk => EveMultiPreview.Services.LocalizationService.CurrentLanguage is "zh" or "ja" or "ko";

    private static Thickness AtLeastOne(Thickness t) => t.Left <= 0 && t.Top <= 0 ? new Thickness(1) : t;

    private static StackPanel Stack(params Control[] parts)
    {
        var stack = new StackPanel();
        foreach (var part in parts) stack.Children.Add(part);
        return stack;
    }
}
