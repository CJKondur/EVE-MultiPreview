using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.SettingsUi;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace EveMultiPreview.Views;

/// <summary>The settings window's colour tokens (SettingsUi/Theme/SettingsTheme.xaml) for the surfaces around it (Plan 4):
/// the WinForms menus read them as System.Drawing colours, the code-built hub, toasts and wheel cards as frozen WPF
/// brushes. Read once, as colour values, so any thread may use them; a token changes in SettingsTheme.xaml only.</summary>
public static class FluentPalette
{
    private static readonly Lazy<IReadOnlyDictionary<string, Color>> Tokens = new(() =>
    {
        var style = new Windows11Style();
        var colours = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var name in StyleContract.Colours)
        {
            var c = (Color)style[name];
            colours[name] = c;
            colours[StyleContract.BrushKey(name)] = c;
        }
        return colours;
    });

    /// <summary>A colour token ("Fx.Flyout") or a brush token's colour ("Fx.FlyoutBrush"). An unknown key throws, so a
    /// typo fails a test instead of painting black.</summary>
    public static Color Color(string key) => Tokens.Value[key];

    /// <summary>A new frozen brush of a token; frozen brushes can be shared across threads and windows.</summary>
    public static SolidColorBrush Brush(string key)
    {
        var brush = new SolidColorBrush(Color(key));
        brush.Freeze();
        return brush;
    }

    /// <summary>The token as a System.Drawing colour, for the WinForms menus.</summary>
    public static System.Drawing.Color Drawing(string key)
    {
        var c = Color(key);
        return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }
}
