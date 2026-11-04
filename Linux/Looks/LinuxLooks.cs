using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;

namespace EveMultiPreview.Linux;

/// <summary>The registry of the twelve looks on Linux and the look the settings window draws (the Linux side of the
/// Windows UiStyles): the player's pick (Current, AppSettings.UiStyle), and the look drawn (Effective), which is Daylight
/// instead while the player follows the desktop's light style (AppSettings.UiStyleFollowWindows; on Linux the desktop's
/// colour scheme, which Avalonia reads from the freedesktop settings portal). A window takes the look with Dress: the
/// look's tokens and the Simple theme's colours as its resources, and styles for the standard controls; a change of look
/// replaces both in every dressed window at once. Event-driven only: nothing polls, nothing animates.</summary>
internal static class LinuxLooks
{
    /// <summary>What a Windows look draws with templates of its own (see SectionShape, CardShape, ToggleShape).</summary>
    public sealed record Shape(SectionShape Section, CardShape Card, ToggleShape Toggle, bool CheckFirst = false);

    /// <summary>The twelve, in the Windows picker's order (UiStyles.All: Classic first), with what each draws its own way.
    /// LinuxLooksTests derives the card, the toggle and "check first" from the Windows files (Fx.Card in the look's XAML,
    /// the base parts its code-behind picks, Fx.ToggleSwitch's BasedOn) and compares them with this table.</summary>
    public static IReadOnlyList<(string Id, Shape Shape)> Table { get; } = new (string, Shape)[]
    {
        ("classic", new(SectionShape.Group, CardShape.Row, ToggleShape.Check)),
        ("windows11", new(SectionShape.Plain, CardShape.Card, ToggleShape.Switch)),
        ("daylight", new(SectionShape.InCard, CardShape.InCard, ToggleShape.Check, CheckFirst: true)),
        ("dusk", new(SectionShape.InCard, CardShape.InCard, ToggleShape.Check, CheckFirst: true)),
        ("compact", new(SectionShape.Plain, CardShape.OnPage, ToggleShape.Check)),
        ("neon", new(SectionShape.Ruled, CardShape.OnPage, ToggleShape.Switch)),
        ("carbon", new(SectionShape.Slashed, CardShape.OnPage, ToggleShape.Check, CheckFirst: true)),
        ("capsule", new(SectionShape.Panel, CardShape.Row, ToggleShape.Check)),
        ("contrast", new(SectionShape.InCard, CardShape.InCard, ToggleShape.Switch)),
        ("blueprint", new(SectionShape.Numbered, CardShape.OnPage, ToggleShape.Check)),
        ("ink", new(SectionShape.Columns, CardShape.OnPage, ToggleShape.Check)),
        ("terminal", new(SectionShape.Group, CardShape.Row, ToggleShape.Check)),
    };

    public const string DefaultId = "classic";
    public const string DaylightId = "daylight";

    public static IReadOnlyList<string> Ids { get; } = Table.Select(t => t.Id).ToList();

    private static readonly Dictionary<string, LinuxLook> Loaded = new(StringComparer.Ordinal);

    /// <summary>The look with this id; a missing, empty or unknown id gives Classic, the default (as UiStyles.Find).</summary>
    public static LinuxLook Find(string? id)
    {
        var row = Table.FirstOrDefault(t => t.Id == id);
        if (row.Id == null) row = Table[0];
        if (!Loaded.TryGetValue(row.Id, out var look))
            Loaded[row.Id] = look = LinuxLook.Read(row.Id, row.Shape) ?? throw new InvalidOperationException("look file not embedded: " + row.Id);
        return look;
    }

    /// <summary>The player's pick. Classic until the settings say (SharedSettingsWindow sets it from AppSettings.UiStyle).</summary>
    public static LinuxLook Current { get; private set; } = Find(DefaultId);

    /// <summary>The look drawn: Daylight while the player follows the desktop and the desktop is light, else the pick.</summary>
    public static LinuxLook Effective => FollowsDesktop && DesktopIsLight() ? Find(DaylightId) : Current;

    /// <summary>Raised after the look drawn may have changed (a pick, the switch, the desktop's colour scheme).</summary>
    public static event Action? Changed;

    /// <summary>Makes <paramref name="id"/> the pick; the dressed windows redraw when the look drawn changes.</summary>
    public static void Set(string? id)
    {
        var before = Effective;
        Current = Find(id);
        if (Effective != before) Changed?.Invoke();
    }

    /// <summary>The saved look (Windows' App.ApplySavedLook): whether Daylight shows while the desktop is light
    /// (AppSettings.UiStyleFollowWindows), then the pick (AppSettings.UiStyle). The settings window calls it as it opens and
    /// after a reload (an import or a reset may name another look).</summary>
    public static void ApplySaved(EveMultiPreview.Models.AppSettings settings)
    {
        FollowDesktop(settings.UiStyleFollowWindows);
        Set(settings.UiStyle);
    }

    /// <summary>The settings window's size (App › General › Settings window size): AppSettings.SettingsUiFontSize / 12, 9 to
    /// 20 pt, as the Windows shell scales its frame.</summary>
    public static double Scale(EveMultiPreview.Models.AppSettings settings) => Math.Clamp(settings.SettingsUiFontSize, 9, 20) / 12.0;

    // ── Following the desktop's light or dark style (AppSettings.UiStyleFollowWindows on Linux) ──

    /// <summary>Whether the look drawn is Daylight while the desktop asks for a light style.</summary>
    public static bool FollowsDesktop { get; private set; }

    /// <summary>Whether Daylight shows only because the desktop is light (App › Look says why the pick does not show).</summary>
    public static bool ShowsDesktopTheme => FollowsDesktop && Current.Id != DaylightId && DesktopIsLight();

    /// <summary>Test seam: whether the desktop asks for a light style. In the app: Avalonia's platform colour values
    /// (the freedesktop portal's color-scheme on Linux); no reading, or no preference, is dark (the pick shows).</summary>
    public static Func<bool> DesktopIsLight { get; set; } = ReadDesktop;

    private static bool ReadDesktop()
    {
        try { return Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static IPlatformSettings? _watched;

    /// <summary>The Look page's switch (UiStyles.FollowWindowsTheme on Linux): follow the desktop or not. While it is on,
    /// the platform's colour-change event redraws; nothing is asked while it is off.</summary>
    public static void FollowDesktop(bool follow)
    {
        var before = Effective;
        FollowsDesktop = follow;
        var settings = follow ? Application.Current?.PlatformSettings : null;
        if (settings != _watched)
        {
            if (_watched != null) _watched.ColorValuesChanged -= OnDesktopChanged;
            _watched = settings;
            if (_watched != null) _watched.ColorValuesChanged += OnDesktopChanged;
        }
        if (Effective != before) Changed?.Invoke();
    }

    private static void OnDesktopChanged(object? sender, PlatformColorValues e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());

    // ── Dressing a window ──

    /// <summary>The Simple theme's colour keys and the look colour each takes (Avalonia.Themes.Simple draws the standard
    /// controls with them). The Linux side of the Windows kit: Linux keeps the Simple theme's control templates and paints
    /// them in the look's colours.</summary>
    private static readonly (string Simple, string Look, byte Alpha)[] SimpleColours =
    {
        ("ThemeBackground", "Bg", 0xFF), ("ThemeForeground", "Text", 0xFF), ("ThemeForegroundLow", "TextDisabled", 0xFF),
        ("ThemeForegroundMid", "TextSecondary", 0xFF),
        ("ThemeBorderLow", "ControlEdge", 0xFF), ("ThemeBorderMid", "ControlEdge", 0xFF), ("ThemeBorderHigh", "Focus", 0xFF),
        ("ThemeControlLow", "Control", 0xFF), ("ThemeControlMid", "Control", 0xFF), ("ThemeControlMidHigh", "CardHover", 0xFF),
        ("ThemeControlHigh", "CardHover", 0xFF), ("ThemeControlVeryHigh", "Border", 0xFF),
        ("ThemeControlHighlightLow", "NavHover", 0xFF), ("ThemeControlHighlightMid", "CardHover", 0xFF), ("ThemeControlHighlightHigh", "AccentSoft", 0xFF),
        ("ThemeAccent", "Accent", 0xFF), ("ThemeAccent2", "Accent", 0xCC), ("ThemeAccent3", "Accent", 0x99), ("ThemeAccent4", "Accent", 0x66),
        ("Highlight", "Accent", 0xFF), ("Highlight2", "AccentSoft", 0xFF), ("HighlightForeground", "OnAccent", 0xFF),
        ("Error", "Critical", 0xFF), ("ErrorLow", "Critical", 0x40),
    };

    /// <summary>Gives <paramref name="window"/> the look drawn, and every later change of it, until the window closes.</summary>
    public static void Dress(Window window)
    {
        var resources = new ResourceDictionary();
        window.Resources.MergedDictionaries.Add(resources);
        Styles? styles = null;
        void Apply()
        {
            var look = Effective;
            Fill(resources, look);
            if (styles != null) window.Styles.Remove(styles);
            window.Styles.Add(styles = StylesFor(look));
            window.RequestedThemeVariant = IsLight(look) ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        Apply();
        window.Bind(TemplatedControl.BackgroundProperty, window.GetResourceObservable("Fx.BgBrush"));
        window.Bind(TemplatedControl.ForegroundProperty, window.GetResourceObservable("Fx.TextBrush"));
        Action changed = Apply;
        Changed += changed;
        window.Closed += (_, _) => Changed -= changed;
    }

    /// <summary>Whether a look's page is light (its window then takes the Simple theme's light variant for the parts no key
    /// reaches: the scroll bars' arrows, the text-selection handles).</summary>
    public static bool IsLight(LinuxLook look)
    {
        var c = look.Colour("Bg");
        return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 128;
    }

    /// <summary>The look's tokens under their Windows keys (a colour as "Fx.Text" and its brush as "Fx.TextBrush", Fx.CardColor's
    /// brush as "Fx.CardBrush" as on Windows), and the Simple theme's colour keys.</summary>
    public static void Fill(IResourceDictionary into, LinuxLook look)
    {
        foreach (var key in look.Keys)
        {
            var name = key["Fx.".Length..];
            object? value = look.TypeOf(key) switch
            {
                "Color" => look.Colour(name),
                "Double" => look.Number(name),
                "Thickness" => look.Thickness(name),
                "CornerRadius" => look.Radius(name),
                "FontFamily" => look.Font(name),
                "FontWeight" => look.Weight(name),
                "FontStyle" => look.Slant(name),
                "Boolean" => look.Flag(name),
                _ => look.Word(name),
            };
            into[key] = value;
            if (value is Color) into[BrushKey(key)] = look.Brush(name);
        }
        foreach (var (simple, colour, alpha) in SimpleColours)
        {
            var c = look.Colour(colour);
            if (alpha != 0xFF) c = Color.FromArgb((byte)(c.A * alpha / 255), c.R, c.G, c.B);
            into[simple + "Color"] = c;
            into[simple + "Brush"] = new ImmutableSolidColorBrush(c);
        }
    }

    /// <summary>The brush key of a colour token, as on Windows (StyleContract.BrushKey): Fx.X → Fx.XBrush, Fx.CardColor → Fx.CardBrush.</summary>
    public static string BrushKey(string colour) =>
        (colour.EndsWith("Color", StringComparison.Ordinal) ? colour[..^"Color".Length] : colour) + "Brush";

    /// <summary>The standard controls in the look: its buttons, boxes and lists, their corners, lines and text sizes; the
    /// navigation entries ("nav", "nav selected") and quiet buttons ("quiet"). Literal values, made again for each look
    /// (a change of look replaces the whole set).</summary>
    private static Styles StylesFor(LinuxLook l)
    {
        var styles = new Styles();
        void Add(Func<Selector?, Selector> select, params (AvaloniaProperty Property, object? Value)[] setters)
        {
            var style = new Style(select);
            foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
            styles.Add(style);
        }
        void Add1(Func<Selector?, Selector> select, AvaloniaProperty property, object? value) => Add(select, (property, value));
        Selector Presenter(Selector s) => s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");

        var control = l.Number("Size.Control");
        var height = l.Number("Control.Height");
        Add(x => x.OfType<Button>(),
            (TemplatedControl.BackgroundProperty, l.Brush("Button")), (TemplatedControl.ForegroundProperty, l.Brush("ButtonText")),
            (TemplatedControl.BorderBrushProperty, l.Brush("ButtonBorder")), (TemplatedControl.BorderThicknessProperty, l.Thickness("Line.Control")),
            (TemplatedControl.CornerRadiusProperty, l.Radius("Radius.Control")), (Layoutable.MinHeightProperty, height),
            (TemplatedControl.FontSizeProperty, control), (TemplatedControl.PaddingProperty, new Thickness(12, 2)),
            (Layoutable.VerticalAlignmentProperty, Avalonia.Layout.VerticalAlignment.Center));
        Add1(x => Presenter(x.OfType<Button>().Class(":pointerover")), ContentPresenter.BackgroundProperty, l.Brush("ButtonHover"));
        Add1(x => Presenter(x.OfType<Button>().Class(":pressed")), ContentPresenter.BackgroundProperty, l.Brush("ButtonHover"));
        Add(x => x.OfType<ToggleButton>(),
            (TemplatedControl.BackgroundProperty, l.Brush("Button")), (TemplatedControl.ForegroundProperty, l.Brush("ButtonText")),
            (TemplatedControl.BorderBrushProperty, l.Brush("ButtonBorder")), (TemplatedControl.BorderThicknessProperty, l.Thickness("Line.Control")),
            (TemplatedControl.CornerRadiusProperty, l.Radius("Radius.Control")), (Layoutable.MinHeightProperty, height),
            (TemplatedControl.FontSizeProperty, control), (TemplatedControl.PaddingProperty, new Thickness(12, 2)));
        Add1(x => Presenter(x.OfType<ToggleButton>().Class(":pointerover")), ContentPresenter.BackgroundProperty, l.Brush("ButtonHover"));
        Add(x => Presenter(x.OfType<ToggleButton>().Class(":checked")),
            (ContentPresenter.BackgroundProperty, l.Brush("Accent")), (ContentPresenter.ForegroundProperty, l.Brush("OnAccent")));

        // Quiet buttons: a link in the text colour, no surface (the breadcrumb, the help column's close, the × of a key).
        Add(x => x.OfType<Button>().Class("quiet"),
            (TemplatedControl.BackgroundProperty, Brushes.Transparent), (TemplatedControl.ForegroundProperty, l.Brush("TextSecondary")),
            (TemplatedControl.BorderThicknessProperty, new Thickness(0)), (TemplatedControl.PaddingProperty, new Thickness(6, 0)),
            (Layoutable.MinHeightProperty, 0.0));
        Add(x => Presenter(x.OfType<Button>().Class("quiet").Class(":pointerover")),
            (ContentPresenter.BackgroundProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, l.Brush("Link")));
        Add1(x => Presenter(x.OfType<Button>().Class("quiet").Class(":pressed")), ContentPresenter.BackgroundProperty, Brushes.Transparent);

        // The navigation (Base.Nav.Entry): the entry in the side bar's colours, the open page marked.
        Add(x => x.OfType<Button>().Class("nav"),
            (TemplatedControl.BackgroundProperty, Brushes.Transparent), (TemplatedControl.ForegroundProperty, l.Brush("NavText")),
            (TemplatedControl.BorderThicknessProperty, new Thickness(0)), (TemplatedControl.CornerRadiusProperty, l.Radius("Radius.Nav")),
            (Layoutable.MinHeightProperty, l.Number("Nav.ItemHeight")), (TemplatedControl.FontSizeProperty, l.Number("Size.Nav")),
            (TemplatedControl.PaddingProperty, new Thickness(10, 2)), (Layoutable.MarginProperty, new Thickness(0, 1)));
        Add(x => x.OfType<Button>().Class("nav").Class("sub"),
            (Layoutable.MinHeightProperty, l.Number("Nav.SubItemHeight")), (TemplatedControl.FontSizeProperty, l.Number("Size.NavSub")));
        Add(x => Presenter(x.OfType<Button>().Class("nav").Class(":pointerover")), (ContentPresenter.BackgroundProperty, l.Brush("NavHover")),
            (ContentPresenter.ForegroundProperty, l.Brush("NavText")));
        Add1(x => Presenter(x.OfType<Button>().Class("nav").Class(":pressed")), ContentPresenter.BackgroundProperty, l.Brush("NavHover"));
        Add(x => x.OfType<Button>().Class("nav").Class("selected"),
            (TemplatedControl.BackgroundProperty, l.Brush("NavSelected")), (TemplatedControl.ForegroundProperty, l.Brush("NavSelectedText")),
            (TemplatedControl.BorderBrushProperty, l.Brush("NavMark")), (TemplatedControl.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
        Add(x => Presenter(x.OfType<Button>().Class("nav").Class("selected").Class(":pointerover")),
            (ContentPresenter.BackgroundProperty, l.Brush("NavSelected")), (ContentPresenter.ForegroundProperty, l.Brush("NavSelectedText")),
            (ContentPresenter.BorderBrushProperty, l.Brush("NavMark")));   // the Simple theme's hover edge would replace the mark

        // Boxes and lists.
        Add(x => x.OfType<TextBox>(),
            (TemplatedControl.BackgroundProperty, l.Brush("Control")), (TemplatedControl.ForegroundProperty, l.Brush("Text")),
            (TemplatedControl.BorderBrushProperty, l.Brush("ControlEdge")), (TemplatedControl.BorderThicknessProperty, l.Thickness("Line.Control")),
            (TemplatedControl.CornerRadiusProperty, l.Radius("Radius.Control")), (Layoutable.MinHeightProperty, height),
            (TemplatedControl.FontSizeProperty, control), (TextBox.CaretBrushProperty, l.Brush("Text")),
            (TextBox.SelectionBrushProperty, l.Brush("Accent")), (TextBox.SelectionForegroundBrushProperty, l.Brush("OnAccent")));
        Add1(x => x.OfType<TextBox>().Class(":focus"), TemplatedControl.BorderBrushProperty, l.Brush("Focus"));
        Add(x => x.OfType<ComboBox>(),
            (TemplatedControl.BackgroundProperty, l.Brush("Control")), (TemplatedControl.ForegroundProperty, l.Brush("Text")),
            (TemplatedControl.BorderBrushProperty, l.Brush("ControlEdge")), (TemplatedControl.BorderThicknessProperty, l.Thickness("Line.Control")),
            (TemplatedControl.CornerRadiusProperty, l.Radius("Radius.Control")), (Layoutable.MinHeightProperty, height),
            (TemplatedControl.FontSizeProperty, control));
        Add(x => x.OfType<ComboBoxItem>(), (TemplatedControl.ForegroundProperty, l.Brush("Text")), (TemplatedControl.FontSizeProperty, control));
        Add(x => x.OfType<ListBox>(), (TemplatedControl.BackgroundProperty, l.Brush("Flyout")), (TemplatedControl.BorderBrushProperty, l.Brush("FlyoutBorder")),
            (TemplatedControl.ForegroundProperty, l.Brush("Text")));
        Add(x => x.OfType<CheckBox>(), (TemplatedControl.ForegroundProperty, l.Brush("Text")), (TemplatedControl.BorderBrushProperty, l.Brush("CheckBorder")));
        Add(x => x.OfType<RadioButton>(), (TemplatedControl.ForegroundProperty, l.Brush("Text")), (TemplatedControl.BorderBrushProperty, l.Brush("CheckBorder")));
        Add(x => x.OfType<FlyoutPresenter>(),
            (TemplatedControl.BackgroundProperty, l.Brush("Flyout")), (TemplatedControl.BorderBrushProperty, l.Brush("FlyoutBorder")),
            (TemplatedControl.CornerRadiusProperty, l.Radius("FlyoutRadius")), (TemplatedControl.ForegroundProperty, l.Brush("Text")));
        Add(x => x.OfType<ToolTip>(),
            (TemplatedControl.BackgroundProperty, l.Brush("Flyout")), (TemplatedControl.BorderBrushProperty, l.Brush("FlyoutBorder")),
            (TemplatedControl.ForegroundProperty, l.Brush("Text")));
        Add(x => x.OfType<ContextMenu>(),
            (TemplatedControl.BackgroundProperty, l.Brush("Flyout")), (TemplatedControl.BorderBrushProperty, l.Brush("FlyoutBorder")));
        Add1(x => x.OfType<MenuItem>(), TemplatedControl.ForegroundProperty, l.Brush("Text"));
        Add(x => x.OfType<Slider>(), (TemplatedControl.ForegroundProperty, l.Brush("TrackFill")), (TemplatedControl.BackgroundProperty, l.Brush("Track")));
        Add1(x => x.OfType<ScrollBar>(), TemplatedControl.BackgroundProperty, Brushes.Transparent);
        return styles;
    }

    // ── Text in the look, for the code that builds a page ──

    /// <summary>A text in a text style of the look: its colour by the look's brush key (it follows a change of look), its
    /// size, weight, family and case from the look drawn now. <paramref name="what"/> is the Windows style's name: Title,
    /// Section, Label, Desc, Caption, Nav.</summary>
    public static TextBlock Text(string text, string what)
    {
        var l = Effective;
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        string brush;
        switch (what)
        {
            case "Title":
                block.FontFamily = l.Font("TitleFont");
                block.FontSize = l.Number("Size.Title");
                block.FontWeight = l.Weight("Weight.Title");
                block.FontStyle = Cjk ? FontStyle.Normal : l.Slant("Style.Title");   // O28: no simulated slant in CJK
                text = Cased(text, l.Upper("Title"));
                brush = "Fx.TitleBrush";
                break;
            case "Section":
                block.FontFamily = l.Font("TitleFont");
                block.FontSize = l.Number("Size.Section");
                block.FontWeight = l.Weight("Weight.Section");
                text = Cased(text, l.Upper("Section"));
                brush = "Fx.SectionTextBrush";
                break;
            case "Desc":
                block.FontSize = l.Number("Size.Desc");
                brush = "Fx.TextSecondaryBrush";
                break;
            case "Caption":
                block.FontSize = l.Number("Size.Caption");
                block.FontWeight = l.Weight("Weight.Section");
                text = Cased(text, l.Upper("Caption"));
                brush = "Fx.TextSecondaryBrush";
                break;
            default:
                block.FontSize = l.Number("Size.Body");
                brush = "Fx.TextBrush";
                break;
        }
        block.Text = text;
        Paint(block, TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>Whether the UI language is Chinese, Japanese or Korean.</summary>
    private static bool Cjk => EveMultiPreview.Services.LocalizationService.CurrentLanguage is "zh" or "ja" or "ko";

    /// <summary>The text in capitals by the UI language's rules, when the look writes it so.</summary>
    public static string Cased(string text, bool upper)
    {
        if (!upper) return text;
        System.Globalization.CultureInfo culture;
        try { culture = System.Globalization.CultureInfo.GetCultureInfo(EveMultiPreview.Services.LocalizationService.CurrentLanguage); }
        catch (System.Globalization.CultureNotFoundException) { culture = System.Globalization.CultureInfo.InvariantCulture; }
        return text.ToUpper(culture);
    }

    /// <summary>Binds a brush property to a look brush key (DynamicResource): it follows a change of look. Every window that
    /// shows these controls is dressed (Dress), so the key is found.</summary>
    public static T Paint<T>(T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key, v => v is IBrush ? v : AvaloniaProperty.UnsetValue));
        return control;
    }
}
