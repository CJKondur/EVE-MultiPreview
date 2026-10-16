using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;
using P = System.Windows.Controls.Primitives;
using W = System.Windows.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>How a text style writes its text: as it is, or in capitals by the UI language's rules (spec §6.1).</summary>
public enum TextCase { Normal, Upper }

/// <summary>The window's corners as Windows 11 draws them: 8 px, 4 px or square (spec §8.5).</summary>
public enum WindowCorners { Round, Small, Square }

/// <summary>How a code-drawn menu marks a checked item: an accent bar, a tick, or the row inverted (spec §8.2).</summary>
public enum MenuCheck { Bar, Tick, Invert }

/// <summary>A window with the whole look (Fluent or the kit, the base, the style), or a transparent surface drawn by
/// the app itself (the hub, its picker, a toast, the wheel), which never takes Fluent (plan S1, ruling 7).</summary>
public enum StyledAs { Window, Surface }

public enum TemplateKind { ImplicitStyle, Style, DataTemplate, ControlTemplate }

/// <summary>One template or style of the contract: its name, what it must be, its resource key when that is a type
/// (the 22 standard controls), and the name of the type it targets.</summary>
public sealed record ContractTemplate(string Name, TemplateKind Kind, Type? Key, string Target);

/// <summary>What a style must make resolve (spec §6): 95 tokens (47 colours and 48 values) in the style's own dictionary,
/// and 73 templates and styles from the style, the base or slot 0 (Fluent or the kit). StyleContractTests holds every
/// style to it.</summary>
public static class StyleContract
{
    /// <summary>The 47 colours, as Color values. The loader makes a frozen brush of each (<see cref="BrushKey"/>).</summary>
    public static IReadOnlyList<string> Colours { get; } = new[]
    {
        // Surfaces (10)
        "Bg", "Nav", "CardColor", "CardHover", "Header", "Control", "Flyout", "AccentSoft", "NavHover", "NavSelected",
        // Lines (5)
        "Border", "ControlEdge", "FlyoutBorder", "Divider", "Focus",
        // Text (11). The spec's colour "Value" is ValueText here, and its "Caption" is CaptionColor below: the spec gives
        // the same names to two text styles (Fx.Value, Fx.Caption), and one key cannot be both (plan S1, ruling 19).
        "Text", "TextSecondary", "TextDisabled", "NavText", "NavTextSecondary", "NavSelectedText", "Title", "SectionText", "Link", "ValueText", "Icon",
        // Accent (3)
        "Accent", "OnAccent", "NavMark",
        // Buttons (4)
        "Button", "ButtonHover", "ButtonBorder", "ButtonText",
        // Check and switch (3)
        "CheckFill", "CheckMark", "CheckBorder",
        // Slider (3)
        "Track", "TrackFill", "Thumb",
        // Status (5). The spec's "Info" is InfoColor here: Fx.Info is the info line's template (§6.2); its brush keeps
        // today's name, Fx.InfoBrush (ruling 2).
        "Critical", "Warning", "InfoColor", "AlertBadge", "OnAlertBadge",
        // Title bar (3)
        "CaptionColor", "CaptionText", "WindowBorder",
    }.Select(n => "Fx." + n).ToArray();

    /// <summary>The 48 values and the type each must have.</summary>
    public static IReadOnlyDictionary<string, Type> Values { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        // Fonts (3)
        ["Fx.Font"] = typeof(FontFamily), ["Fx.MonoFont"] = typeof(FontFamily), ["Fx.TitleFont"] = typeof(FontFamily),
        // Sizes (8)
        ["Fx.Size.Title"] = typeof(double), ["Fx.Size.Section"] = typeof(double), ["Fx.Size.Body"] = typeof(double), ["Fx.Size.Desc"] = typeof(double),
        ["Fx.Size.Nav"] = typeof(double), ["Fx.Size.NavSub"] = typeof(double), ["Fx.Size.Caption"] = typeof(double), ["Fx.Size.Control"] = typeof(double),
        // Weights (4), slant (1), case (4)
        ["Fx.Weight.Title"] = typeof(FontWeight), ["Fx.Weight.Section"] = typeof(FontWeight), ["Fx.Weight.Nav"] = typeof(FontWeight), ["Fx.Weight.NavSelected"] = typeof(FontWeight),
        ["Fx.Style.Title"] = typeof(FontStyle),
        ["Fx.Case.Title"] = typeof(TextCase), ["Fx.Case.Section"] = typeof(TextCase), ["Fx.Case.Nav"] = typeof(TextCase), ["Fx.Case.Caption"] = typeof(TextCase),
        // Radii (5)
        ["Fx.Radius"] = typeof(CornerRadius), ["Fx.Radius.Control"] = typeof(CornerRadius), ["Fx.Radius.Nav"] = typeof(CornerRadius),
        ["Fx.FlyoutRadius"] = typeof(CornerRadius), ["Fx.Radius.Check"] = typeof(CornerRadius),
        // Lines (3)
        ["Fx.Line.Card"] = typeof(Thickness), ["Fx.Line.Control"] = typeof(Thickness), ["Fx.Line.Row"] = typeof(Thickness),
        // Metrics (11)
        ["Fx.Nav.Width"] = typeof(double), ["Fx.Nav.ItemHeight"] = typeof(double), ["Fx.Nav.SubItemHeight"] = typeof(double), ["Fx.Nav.SubIndent"] = typeof(double),
        ["Fx.Row.MinHeight"] = typeof(double), ["Fx.Row.Padding"] = typeof(Thickness), ["Fx.Row.Gap"] = typeof(Thickness), ["Fx.Control.Height"] = typeof(double),
        ["Fx.Section.Gap"] = typeof(Thickness), ["Fx.Page.Padding"] = typeof(Thickness), ["Fx.Label.Width"] = typeof(double),
        // Presence (4)
        ["Fx.Nav.Icons"] = typeof(Visibility), ["Fx.Show.Breadcrumb"] = typeof(Visibility), ["Fx.Scroll.Overlay"] = typeof(bool), ["Fx.Window.Corners"] = typeof(WindowCorners),
        // Around (5): for the surfaces drawn in code
        ["Fx.Menu.RowHeight"] = typeof(double), ["Fx.Menu.Radius"] = typeof(double), ["Fx.Menu.Check"] = typeof(MenuCheck), ["Fx.Menu.Font"] = typeof(string),
        ["Fx.Hub.Round"] = typeof(bool),
    };

    private static ContractTemplate Implicit(Type type) => new(type.Name, TemplateKind.ImplicitStyle, type, type.Name);
    private static ContractTemplate Keyed(string name, string target) => new(name, TemplateKind.Style, null, target);
    private static ContractTemplate Data(string name) => new(name, TemplateKind.DataTemplate, null, "");

    /// <summary>The 73 templates and styles.</summary>
    public static IReadOnlyList<ContractTemplate> Templates { get; } = new[]
    {
        // Standard controls, by type (22)
        Implicit(typeof(W.Button)), Implicit(typeof(P.ToggleButton)), Implicit(typeof(P.RepeatButton)), Implicit(typeof(W.CheckBox)),
        Implicit(typeof(W.RadioButton)), Implicit(typeof(W.ComboBox)), Implicit(typeof(W.ComboBoxItem)), Implicit(typeof(W.TextBox)),
        Implicit(typeof(W.Slider)), Implicit(typeof(P.ScrollBar)), Implicit(typeof(W.ScrollViewer)), Implicit(typeof(W.ListBox)),
        Implicit(typeof(W.ListBoxItem)), Implicit(typeof(W.ListView)), Implicit(typeof(W.ListViewItem)), Implicit(typeof(W.GridViewColumnHeader)),
        Implicit(typeof(W.ProgressBar)), Implicit(typeof(W.ToolTip)), Implicit(typeof(W.ContextMenu)), Implicit(typeof(W.MenuItem)),
        Implicit(typeof(W.Separator)), Implicit(typeof(W.GroupBox)),
        // Keyed controls (7)
        Keyed("Fx.ToggleSwitch", "CheckBox"), Keyed("Fx.PrimaryButton", "Button"), Keyed("Fx.QuietButton", "Button"),
        Keyed("Fx.IconButton", "Button"), Keyed("Fx.KeyCap", "Button"), Keyed("Fx.Chip", "Border"), Keyed("Fx.Swatch", "Button"),
        // Text (10)
        Keyed("Fx.PageTitle", "TextBlock"), Keyed("Fx.Breadcrumb", "TextBlock"), Keyed("Fx.PageDesc", "TextBlock"),
        Keyed("Fx.SectionTitle", "TextBlock"), Keyed("Fx.Label", "TextBlock"), Keyed("Fx.Desc", "TextBlock"), Keyed("Fx.Error", "TextBlock"),
        Keyed("Fx.Value", "TextBlock"), Keyed("Fx.ValueSecondary", "TextBlock"), Keyed("Fx.Caption", "TextBlock"),
        // Page (21)
        Data("Fx.Section"), Keyed("Fx.CardHost", "ContentControl"), Keyed("Fx.Card", "Border"),
        Data("Fx.Card.Toggle"), Data("Fx.Card.Choice"), Data("Fx.Card.Segmented"), Data("Fx.Card.Slider"), Data("Fx.Card.Number"),
        Data("Fx.Card.Text"), Data("Fx.Card.Folder"), Data("Fx.Card.File"), Data("Fx.Card.Color"), Data("Fx.Card.Hotkey"), Data("Fx.Card.Actions"),
        Data("Fx.OptionText"), Data("Fx.Info"), Data("Fx.Editor"),
        Keyed("Fx.Table", "Border"), Keyed("Fx.Table.Header", "Border"), Keyed("Fx.Table.Row", "Border"), Keyed("Fx.Table.Cell", "Border"),
        // Shell (11)
        new("Fx.Shell", TemplateKind.ControlTemplate, null, "ShellFrame"),
        Data("Fx.Shell.Profile"), Data("Fx.Shell.Mode"), Data("Fx.Shell.Header"), Data("Fx.Shell.Help"), Data("Fx.Shell.Status"),
        Data("Fx.Nav.Group"), Data("Fx.Nav.Page"), Keyed("Fx.Nav.Entry", "Button"), Data("Fx.Toast"), Data("Fx.SaveError"),
        // Other (2)
        Keyed("Fx.Dialog", "Border"), Data("Fx.Preview"),
    };

    /// <summary>Fx names that are allowed outside the 95 and the 73 (plan S1, ruling 12): the icon font, which is not a
    /// token (spec §6.1), and the three converters a binding names statically.</summary>
    public static IReadOnlyList<string> Extra { get; } = new[] { "Fx.IconFont", "Fx.BoolToVis", "Fx.NotNullToVis", "Fx.ColorBrush" };

    /// <summary>The brush the loader makes of a colour: Fx.X → Fx.XBrush, and Fx.CardColor → Fx.CardBrush, today's name
    /// (plan S1, ruling 2); so Fx.CaptionColor → Fx.CaptionBrush.</summary>
    public static string BrushKey(string colour)
    {
        var name = colour.EndsWith("Color", StringComparison.Ordinal) ? colour[..^"Color".Length] : colour;
        return name + "Brush";
    }

    public static bool IsToken(string name) => Colours.Contains(name) || Values.ContainsKey(name);

    /// <summary>Every name of the contract: colours, their brushes, values, the keyed templates, and the extra names.</summary>
    public static IReadOnlySet<string> AllNames { get; } = Colours.Concat(Colours.Select(BrushKey)).Concat(Values.Keys)
        .Concat(Templates.Where(t => t.Key == null).Select(t => t.Name)).Concat(Extra).ToHashSet(StringComparer.Ordinal);
}
