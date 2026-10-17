using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace EveMultiPreview.SettingsUi;

/// <summary>The dictionary of the look Terminal (UI styles spec §7.5): its 95 tokens, its picks from the kit, its parts, the
/// shared parts it takes, a frozen brush of each colour, and its metrics laid on a grid of character cells measured from
/// its font when it loads.</summary>
public partial class TerminalStyle : ResourceDictionary
{
    public TerminalStyle()
    {
        UiStyles.ReadLook(this, InitializeComponent, UiStyles.FlatParts.Concat(new[]
        {
            // A row on a grid of columns: the box (4 cells), the label (50 cells), the control. Its card (no surface: its
            // section's box is on the page), section, navigation, slider and number cards are its own (Terminal.xaml).
            ("Fx.CardHost", "Base.Card.Columns"), ("Fx.Card.Toggle", "Base.Card.ColumnsToggle"),
        }).ToArray());
        Cells(this, CellOf((FontFamily)this["Fx.Font"]));
    }

    /// <summary>The look's one text size (spec §7.5).</summary>
    public const double TextSize = 16;

    /// <summary>The width of one character cell (spec §7.5, "measured when the style loads"): the advance of "0" at 16 in the
    /// first family of the chain that Windows has (Cascadia Mono, else Cascadia Code, else Consolas); 0.6 em if it has none.</summary>
    public static double CellOf(FontFamily chain)
    {
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in chain.Source.Split(',').Select(n => n.Trim()).Where(installed.Contains))
        {
            var typeface = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.CharacterToGlyphMap.TryGetValue('0', out var zero))
                return glyphs.AdvanceWidths[zero] * TextSize;
        }
        return 0.6 * TextSize;
    }

    /// <summary>Lays the look's metrics on a grid of cells <paramref name="cell"/> wide (spec §7.5): the pane 28 cells, a
    /// page's indent 4, the label column 50 with 4 for the box before it, the page one cell in from its edges; and the look's
    /// own: the slider 20 cells, the box's padding, a tag's padding, the find field 26 cells.</summary>
    public static void Cells(ResourceDictionary look, double cell)
    {
        look["Fx.Nav.Width"] = 28 * cell;
        look["Fx.Nav.SubIndent"] = 4 * cell;
        look["Fx.Label.Width"] = 50 * cell;
        look["Fx.Page.Padding"] = new Thickness(cell, 6, cell, 0);
        look["Look.BoxColumn"] = 4 * cell;
        look["Terminal.Cell"] = cell;
        look["Terminal.SliderWidth"] = 20 * cell;
        look["Terminal.BoxPad"] = new Thickness(cell, 0, 0, 0);
        look["Terminal.TagPad"] = new Thickness(cell / 2, 0, cell / 2, 0);
        look["Terminal.FindWidth"] = 26 * cell;
    }
}
