using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using Color = System.Windows.Media.Color;

namespace EveMultiPreview.SettingsUi;

/// <summary>The current look for the surfaces drawn in code (spec §8.2): an immutable snapshot of the 47 colours (also
/// under their brush keys) and of the values around the window (menus, hub, corners). Replaced as a whole when the look
/// changes, never changed in place, so any thread may read it.</summary>
public sealed class UiPalette
{
    private readonly IReadOnlyDictionary<string, Color> _colours;

    private UiPalette(IReadOnlyDictionary<string, Color> colours, double menuRowHeight, double menuRadius, MenuCheck menuCheck,
        string menuFont, bool hubRound, WindowCorners corners)
    {
        _colours = colours;
        MenuRowHeight = menuRowHeight;
        MenuRadius = menuRadius;
        MenuCheck = menuCheck;
        MenuFont = menuFont;
        HubRound = hubRound;
        Corners = corners;
    }

    public double MenuRowHeight { get; }
    public double MenuRadius { get; }
    public MenuCheck MenuCheck { get; }

    /// <summary>The menus' font family: a font Windows' GDI has (the WinForms menus cannot see a WPF resource font).</summary>
    public string MenuFont { get; }

    public bool HubRound { get; }
    public WindowCorners Corners { get; }

    /// <summary>A colour token ("Fx.Flyout") or a brush key's colour ("Fx.FlyoutBrush"). An unknown key throws, so a typo
    /// fails a test instead of painting black.</summary>
    public Color Colour(string key) => _colours[key];

    /// <summary>The palette of a style dictionary: its colours and its values around the window.</summary>
    public static UiPalette From(ResourceDictionary style)
    {
        var colours = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var name in StyleContract.Colours)
        {
            var c = (Color)style[name];
            colours[name] = c;
            colours[StyleContract.BrushKey(name)] = c;
        }
        return new UiPalette(colours, (double)style["Fx.Menu.RowHeight"], (double)style["Fx.Menu.Radius"], (MenuCheck)style["Fx.Menu.Check"],
            (string)style["Fx.Menu.Font"], (bool)style["Fx.Hub.Round"], (WindowCorners)style["Fx.Window.Corners"]);
    }

    private static UiPalette? _current;

    /// <summary>The palette of the look the app shows: Windows 11's until a change of look replaces it.</summary>
    public static UiPalette Current
    {
        get
        {
            var current = Volatile.Read(ref _current);
            if (current != null) return current;
            Interlocked.CompareExchange(ref _current, From(new Windows11Style()), null);
            return Volatile.Read(ref _current)!;
        }
    }

    /// <summary>Replaces the palette as a whole (UiStyles.Set; tests).</summary>
    public static void Use(UiPalette palette) => Volatile.Write(ref _current, palette);
}
