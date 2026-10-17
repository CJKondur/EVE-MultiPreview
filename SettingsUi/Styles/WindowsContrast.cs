using System;
using System.Collections.Generic;
using System.Windows;
using Color = System.Windows.Media.Color;

namespace EveMultiPreview.SettingsUi;

/// <summary>Windows' contrast themes (UI styles spec §12): whether one is on, its colours, and which of them each colour of
/// the contract takes. Read each time they are needed; a test replaces IsOn and Colours (spec §16: "a fake 'Windows says a
/// contrast theme is on'").</summary>
public static class WindowsContrast
{
    /// <summary>Whether Windows has a contrast theme on (Windows Settings › Accessibility › Contrast themes).</summary>
    public static Func<bool> IsOn { get; set; } = () => SystemParameters.HighContrast;

    /// <summary>The theme's colours by the names of <see cref="Names"/>.</summary>
    public static Func<IReadOnlyDictionary<string, Color>> Colours { get; set; } = SystemColours;

    /// <summary>The eight colours of a contrast theme the app draws with.</summary>
    public static IReadOnlyList<string> Names { get; } =
        new[] { "Window", "WindowText", "Highlight", "HighlightText", "HotTrack", "GrayText", "ButtonFace", "ButtonText" };

    /// <summary>Windows' colours now (they are the theme's while a contrast theme is on).</summary>
    public static IReadOnlyDictionary<string, Color> SystemColours() => new Dictionary<string, Color>(StringComparer.Ordinal)
    {
        ["Window"] = System.Windows.SystemColors.WindowColor,
        ["WindowText"] = System.Windows.SystemColors.WindowTextColor,
        ["Highlight"] = System.Windows.SystemColors.HighlightColor,
        ["HighlightText"] = System.Windows.SystemColors.HighlightTextColor,
        ["HotTrack"] = System.Windows.SystemColors.HotTrackColor,
        ["GrayText"] = System.Windows.SystemColors.GrayTextColor,
        ["ButtonFace"] = System.Windows.SystemColors.ControlColor,
        ["ButtonText"] = System.Windows.SystemColors.ControlTextColor,
    };

    /// <summary>Which of the theme's colours each of the 47 colours of the contract takes: spec §12's list, and for the
    /// colours it does not name the colour of their role (a surface the window's, a line or a status the text's, the open
    /// page's mark and the badge the highlight's, text on them the highlight's text).</summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Window: every surface.
        ["Fx.Bg"] = "Window", ["Fx.Nav"] = "Window", ["Fx.CardColor"] = "Window", ["Fx.Control"] = "Window", ["Fx.Flyout"] = "Window",
        ["Fx.CaptionColor"] = "Window", ["Fx.Header"] = "Window", ["Fx.CardHover"] = "Window", ["Fx.AccentSoft"] = "Window", ["Fx.NavHover"] = "Window",
        // WindowText: text, lines and the status colours (severity is told by shape and word).
        ["Fx.Text"] = "WindowText", ["Fx.TextSecondary"] = "WindowText", ["Fx.NavText"] = "WindowText", ["Fx.NavTextSecondary"] = "WindowText",
        ["Fx.Title"] = "WindowText", ["Fx.SectionText"] = "WindowText", ["Fx.ValueText"] = "WindowText", ["Fx.Icon"] = "WindowText",
        ["Fx.CaptionText"] = "WindowText", ["Fx.Border"] = "WindowText", ["Fx.ControlEdge"] = "WindowText", ["Fx.FlyoutBorder"] = "WindowText",
        ["Fx.Divider"] = "WindowText", ["Fx.Focus"] = "WindowText", ["Fx.WindowBorder"] = "WindowText", ["Fx.CheckBorder"] = "WindowText",
        ["Fx.Track"] = "WindowText", ["Fx.Critical"] = "WindowText", ["Fx.Warning"] = "WindowText", ["Fx.InfoColor"] = "WindowText",
        // Highlight: the accent and what is picked.
        ["Fx.Accent"] = "Highlight", ["Fx.NavSelected"] = "Highlight", ["Fx.NavMark"] = "Highlight", ["Fx.CheckFill"] = "Highlight",
        ["Fx.TrackFill"] = "Highlight", ["Fx.Thumb"] = "Highlight", ["Fx.AlertBadge"] = "Highlight",
        // HighlightText: what is drawn on the highlight.
        ["Fx.OnAccent"] = "HighlightText", ["Fx.NavSelectedText"] = "HighlightText", ["Fx.CheckMark"] = "HighlightText", ["Fx.OnAlertBadge"] = "HighlightText",
        // The rest.
        ["Fx.Link"] = "HotTrack", ["Fx.TextDisabled"] = "GrayText",
        ["Fx.Button"] = "ButtonFace", ["Fx.ButtonHover"] = "ButtonFace", ["Fx.ButtonText"] = "ButtonText", ["Fx.ButtonBorder"] = "ButtonText",
    };

    /// <summary>A look dictionary drawn in a theme's colours: each colour of the contract and its frozen brush replaced.</summary>
    public static ResourceDictionary Apply(ResourceDictionary look, IReadOnlyDictionary<string, Color> colours)
    {
        foreach (var (token, name) in Map)
        {
            var colour = colours[name];
            look[token] = colour;
            var brush = new System.Windows.Media.SolidColorBrush(colour);
            brush.Freeze();
            look[StyleContract.BrushKey(token)] = brush;
        }
        return look;
    }
}
