using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace EveMultiPreview.Linux;

/// <summary>How a look draws a section of a page (the Windows look's Fx.Section): its title over the rows (Plain); a box
/// with the title in its top line (Group: Classic, Terminal); the rows in one card under the title (InCard); a line under
/// the title (Ruled); "// " and a slanted title over a line (Slashed: Carbon); a numbered circle, the title and a line to
/// the right edge (Numbered: Blueprint); a framed panel with a header bar (Panel: Capsule); the number and title in a
/// column left of the rows (Columns: Ink).</summary>
internal enum SectionShape { Plain, Group, InCard, Ruled, Slashed, Numbered, Panel, Columns }

/// <summary>How a look draws one row (the Windows look's Fx.Card): a card of its own (Card: the base's), no surface and
/// no edge (Row), on the page colour with a divider (OnPage: Base.Card.OnPage), on the card colour with a divider
/// (InCard: Base.Card.InCard).</summary>
internal enum CardShape { Card, Row, OnPage, InCard }

/// <summary>The on/off control of a setting: a check box (the kit's checks) or a switch (Windows 11's Fluent switch,
/// Neon's Kit.Check.Switch, High contrast's own).</summary>
internal enum ToggleShape { Check, Switch }

/// <summary>One of the Windows app's twelve looks, read on Linux from the same XAML file Windows compiles
/// (SettingsUi/Styles/&lt;Name&gt;.xaml, embedded into the Linux app as "Look.&lt;Name&gt;.xaml"): its 95 tokens (47 colours
/// and 48 values), each written there once as a literal. Nothing is copied by hand, so the Linux look cannot drift from
/// the Windows one. What the Windows look draws with templates of its own (its section, row and on/off control) is the
/// small table in LinuxLooks, which LinuxLooksTests checks against the Windows sources.</summary>
internal sealed class LinuxLook
{
    private const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The element names a token is written as in the look files (WPF's type names, as the files spell them).</summary>
    private static readonly HashSet<string> TokenElements = new(StringComparer.Ordinal)
    {
        "Color", "Double", "CornerRadius", "Thickness", "FontFamily", "FontWeight", "FontStyle", "TextCase", "Visibility",
        "Boolean", "WindowCorners", "MenuCheck", "String",
    };

    private readonly Dictionary<string, (string Type, string Text)> _tokens;
    private readonly Dictionary<string, IBrush> _brushes = new(StringComparer.Ordinal);

    private LinuxLook(string id, Dictionary<string, (string Type, string Text)> tokens, LinuxLooks.Shape shape)
    {
        Id = id;
        _tokens = tokens;
        Section = shape.Section;
        Card = shape.Card;
        Toggle = shape.Toggle;
        CheckFirst = shape.CheckFirst;
    }

    /// <summary>The look's id, as AppSettings.UiStyle saves it ("classic", "windows11"…).</summary>
    public string Id { get; }

    public SectionShape Section { get; }
    public CardShape Card { get; }
    public ToggleShape Toggle { get; }

    /// <summary>The on/off control stands before its label (Base.Card.CheckFirst: Daylight, Dusk, Carbon).</summary>
    public bool CheckFirst { get; }

    /// <summary>Every token the file holds, by its key ("Fx.Bg", "Fx.Size.Body"…).</summary>
    public IReadOnlyCollection<string> Keys => _tokens.Keys;

    /// <summary>The element name a token is written as ("Color", "Double"…).</summary>
    public string TypeOf(string key) => Token(key).Type;

    /// <summary>The token as written in the Windows file.</summary>
    public string Text(string key) => Token(key).Text;

    // ── Typed reads. A key the file lacks throws (KeyNotFoundException), so a typo fails a test instead of painting black. ──

    /// <summary>A colour: <paramref name="name"/> without "Fx." ("Bg", "CardColor", "Text").</summary>
    public Color Colour(string name) => Color.Parse(Token("Fx." + name).Text);

    /// <summary>A frozen-style brush of a colour, one per look and colour.</summary>
    public IBrush Brush(string name)
    {
        if (!_brushes.TryGetValue(name, out var brush))
            _brushes[name] = brush = new ImmutableSolidColorBrush(Colour(name));
        return brush;
    }

    public double Number(string key) => double.Parse(Token("Fx." + key).Text, NumberStyles.Float, CultureInfo.InvariantCulture);
    public Thickness Thickness(string key) => Avalonia.Thickness.Parse(Token("Fx." + key).Text);
    public CornerRadius Radius(string key) => CornerRadius.Parse(Token("Fx." + key).Text);
    public FontWeight Weight(string key) => Enum.Parse<FontWeight>(Token("Fx." + key).Text, ignoreCase: true);
    public FontStyle Slant(string key) => Enum.Parse<FontStyle>(Token("Fx." + key).Text, ignoreCase: true);
    public bool Flag(string key) => bool.Parse(Token("Fx." + key).Text);
    public string Word(string key) => Token("Fx." + key).Text.Trim();

    /// <summary>Whether a text style is written in capitals (Fx.Case.Title, .Section, .Nav, .Caption).</summary>
    public bool Upper(string what) => Word("Case." + what) == "Upper";

    /// <summary>Whether a Visibility token shows its part (Fx.Show.Breadcrumb, Fx.Nav.Icons).</summary>
    public bool Shows(string key) => Word(key) == "Visible";

    /// <summary>A font token (Fx.Font, Fx.TitleFont, Fx.MonoFont) as Avalonia draws it on Linux (LinuxFonts.FamilyOf).</summary>
    public FontFamily Font(string key) => LinuxFonts.FamilyOf(Token("Fx." + key).Text);

    private (string Type, string Text) Token(string key) =>
        _tokens.TryGetValue(key, out var token) ? token : throw new KeyNotFoundException($"look {Id} has no token {key}");

    // ── Reading a look file ──

    /// <summary>The look's file name as Windows names it ("Classic", "Windows11"), from its id.</summary>
    public static string FileOf(string id) => id == "windows11" ? "Windows11" : char.ToUpperInvariant(id[0]) + id[1..];

    /// <summary>Reads the look <paramref name="id"/> from the embedded Windows file. Null when the file is not embedded.</summary>
    public static LinuxLook? Read(string id, LinuxLooks.Shape shape, Assembly? from = null)
    {
        from ??= typeof(LinuxLook).Assembly;
        using var stream = from.GetManifestResourceStream("Look." + FileOf(id) + ".xaml");
        return stream == null ? null : Parse(id, XDocument.Load(stream), shape);
    }

    /// <summary>The tokens of a look file: each element directly under the dictionary whose x:Key starts "Fx." and whose
    /// type is one a token is written as, with its text. Templates, styles and converters are not tokens.</summary>
    public static LinuxLook Parse(string id, XDocument file, LinuxLooks.Shape shape)
    {
        var tokens = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var element in file.Root!.Elements())
        {
            var key = (string?)element.Attribute(XName.Get("Key", XamlNs));
            if (key == null || !key.StartsWith("Fx.", StringComparison.Ordinal) || !TokenElements.Contains(element.Name.LocalName)) continue;
            if (element.HasElements) continue;
            tokens[key] = (element.Name.LocalName, element.Value.Trim());
        }
        return new LinuxLook(id, tokens, shape);
    }
}
