using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S4: App settings › Look's gallery and About › Fonts, Avalonia ports of the Windows LookGallery and
/// FontLicencesEditor.</summary>
internal sealed partial class SettingsEditors
{
    private const double LookCardWidth = 232;

    /// <summary>The look gallery (the Windows LookGallery, UI styles spec §9.2): one card per look in the Windows order,
    /// Classic first, each with the look's miniature, name and description; the current card outlined in the accent with
    /// "Current" under it. One click picks a look, saves it, and the settings window takes it at once. Every card is the
    /// same size. The miniatures are drawn once, in each look's own colours; nothing moves.</summary>
    internal Control LookGallery(SettingsContext ctx)
    {
        var panel = Titled(EditorIds.LookGallery);
        var cards = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var current = LinuxLooks.Find(ctx.S.UiStyle);
        foreach (var id in LinuxLooks.Ids) cards.Children.Add(LookCard(ctx, LinuxLooks.Find(id), current.Id == id));
        panel.Children.Add(cards);
        return panel;
    }

    private static Button LookCard(SettingsContext ctx, LinuxLook look, bool isCurrent)
    {
        var name = LinuxLooks.Text(SettingsContext.Str("L.S.Style." + look.Id), "Label");
        name.FontWeight = FontWeight.SemiBold;
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(0, 6, 0, 0);
        var descText = SettingsContext.Str("L.S.Style." + look.Id + ".Desc");
        var desc = LinuxLooks.Text(descText, "Desc");
        desc.MaxLines = 2;
        desc.TextTrimming = TextTrimming.CharacterEllipsis;
        desc.Height = Math.Ceiling(2 * 1.35 * desc.FontSize);   // two lines whatever it holds (the Windows LinesHigh)
        var mark = LinuxLooks.Text("✓ " + SettingsContext.Str("L.S.Look.Current"), "Desc");
        LinuxLooks.Paint(mark, TextBlock.ForegroundProperty, "Fx.AccentBrush");
        mark.Opacity = isCurrent ? 1 : 0;   // the row is on every card, shown on one: the cards stay the same size
        var body = new StackPanel { Width = LookCardWidth - 20 };
        body.Children.Add(Miniature(look, LookCardWidth - 20));
        body.Children.Add(name);
        body.Children.Add(desc);
        body.Children.Add(mark);
        var frame = new Border { Child = body, Padding = new Thickness(8), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6) };
        LinuxLooks.Paint(frame, Border.BorderBrushProperty, isCurrent ? "Fx.AccentBrush" : "Fx.BorderBrush");
        var card = new Button { Content = frame, Padding = new Thickness(0), Margin = new Thickness(0, 0, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        card.Classes.Add("quiet");
        ToolTip.SetTip(card, descText);
        card.Click += (_, _) => PickLook(ctx, look);
        return card;
    }

    /// <summary>The Windows LookGallery.Pick: the pick saved, the window in it at once, said in a toast. The window draws
    /// again on the change (LinuxLooks.Changed); while the player follows the desktop the look drawn may not change, so the
    /// page is reloaded for its marks and the note.</summary>
    private static void PickLook(SettingsContext ctx, LinuxLook look)
    {
        ctx.S.UiStyle = look.Id;
        ctx.Applier.Request(ApplyScope.Save);
        LinuxLooks.Set(look.Id);
        if (LinuxLooks.FollowsDesktop) ctx.Host.Reload();   // only the follow-the-desktop note and marks wait on it (as Windows)
        ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.app.look.Toast"), SettingsContext.Str("L.S.Style." + look.Id)));
    }

    /// <summary>A look's miniature (the Windows Fx.Preview over LookSample): two navigation entries, the open one first, a
    /// section heading, a row with its on/off control, a slider, a dropdown and a button, in the look's own colours, type,
    /// corners and row shape. Inert: it has no input.</summary>
    internal static Control Miniature(LinuxLook l, double width)
    {
        double scale = 0.62;
        double body = l.Number("Size.Body") * scale;
        IBrush B(string colour) => l.Brush(colour);
        TextBlock T(string text, string colour, double size, FontWeight weight = FontWeight.Normal, FontFamily? family = null) => new()
        {
            Text = text, Foreground = B(colour), FontSize = Math.Max(7, size), FontWeight = weight, FontFamily = family ?? l.Font("Font"),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The side bar: Alerts › Sounds open, Alerts › Events under it.
        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
        var open = new Border
        {
            Background = B("NavSelected"), BorderBrush = B("NavMark"), BorderThickness = new Thickness(2, 0, 0, 0), CornerRadius = Scaled(l.Radius("Radius.Nav"), scale),
            Padding = new Thickness(5, 3), Margin = new Thickness(4, 0),
            Child = T(LinuxLooks.Cased(SettingsContext.Str("L.S.Page." + AlertsPages.SoundsId), l.Upper("Nav")), "NavSelectedText", l.Number("Size.Nav") * scale, l.Weight("Weight.NavSelected")),
        };
        nav.Children.Add(open);
        nav.Children.Add(new Border
        {
            Padding = new Thickness(7, 3), Margin = new Thickness(4, 0),
            Child = T(LinuxLooks.Cased(SettingsContext.Str("L.S.Page." + AlertsPages.EventsId), l.Upper("Nav")), "NavText", l.Number("Size.Nav") * scale, l.Weight("Weight.Nav")),
        });
        var side = new Border { Background = B("Nav"), Child = nav, Width = width * 0.34 };

        // The page: a section heading, then a row with its on/off control, a slider, a dropdown and a button.
        var page = new StackPanel { Spacing = 5, Margin = new Thickness(8, 8, 8, 0) };
        page.Children.Add(T(LinuxLooks.Cased(SettingsContext.Str("L.S.Sec.alerts.sounds.main"), l.Upper("Section")), "SectionText",
            l.Number("Size.Section") * scale, l.Weight("Weight.Section"), l.Font("TitleFont")));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(T(SettingsContext.Str("L.S.alerts.sounds.enabled"), "Text", body));
        var toggle = MiniToggle(l);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        var (surface, edge) = l.Card switch
        {
            CardShape.Card => ("CardColor", l.Thickness("Line.Card")),
            CardShape.OnPage => ("Bg", l.Thickness("Line.Row")),
            CardShape.InCard => ("CardColor", l.Thickness("Line.Row")),
            _ => (null, l.Thickness("Line.Row")),
        };
        page.Children.Add(new Border
        {
            Background = surface != null ? B(surface) : null, BorderBrush = B(l.Card == CardShape.Card ? "Border" : "Divider"), BorderThickness = edge,
            CornerRadius = l.Card == CardShape.Card ? Scaled(l.Radius("Radius"), scale) : default, Padding = new Thickness(6, 4), Child = row,
        });
        // The slider: a track, its filled part and the thumb.
        var track = new Grid { Height = 10 };
        track.Children.Add(new Border { Height = 3, Background = B("Track"), CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center });
        track.Children.Add(new Border { Height = 3, Background = B("TrackFill"), CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Width = width * 0.3 });
        track.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = B("Thumb"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(width * 0.3 - 5, 0, 0, 0) });
        page.Children.Add(track);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        controls.Children.Add(new Border
        {
            Background = B("Control"), BorderBrush = B("ControlEdge"), BorderThickness = l.Thickness("Line.Control"), CornerRadius = Scaled(l.Radius("Radius.Control"), scale),
            Padding = new Thickness(5, 2), Width = width * 0.3, Child = T("▾", "ValueText", l.Number("Size.Control") * scale),
        });
        controls.Children.Add(new Border
        {
            Background = B("Button"), BorderBrush = B("ButtonBorder"), BorderThickness = l.Thickness("Line.Control"), CornerRadius = Scaled(l.Radius("Radius.Control"), scale),
            Padding = new Thickness(8, 2), Child = T(Ui.NoAccess(SettingsContext.Str("L.S.Shell.Ok")), "ButtonText", l.Number("Size.Control") * scale),
        });
        page.Children.Add(controls);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(side);
        Grid.SetColumn(page, 1);
        grid.Children.Add(page);
        return new Border
        {
            Width = width, Height = Math.Round(width * 0.62), Background = B("Bg"), ClipToBounds = true, Child = grid, IsHitTestVisible = false,
            BorderBrush = B("WindowBorder"), BorderThickness = new Thickness(1),
        };
    }

    /// <summary>The look's on/off control, on, small.</summary>
    private static Control MiniToggle(LinuxLook l)
    {
        if (l.Toggle == ToggleShape.Switch)
        {
            var knob = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = l.Brush("CheckMark"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(2, 0) };
            return new Border { Width = 20, Height = 11, CornerRadius = new CornerRadius(5.5), Background = l.Brush("CheckFill"), Child = knob, VerticalAlignment = VerticalAlignment.Center };
        }
        var tick = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,5 L 4,9 L 12,0"), Stretch = Stretch.Uniform, Margin = new Thickness(2), Stroke = l.Brush("CheckMark"), StrokeThickness = 1.5,
        };
        return new Border
        {
            Width = 11, Height = 11, Background = l.Brush("CheckFill"), BorderBrush = l.Brush("CheckFill"), BorderThickness = new Thickness(1),
            CornerRadius = Scaled(l.Radius("Radius.Check"), 0.62), Child = tick, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static CornerRadius Scaled(CornerRadius r, double by) => new(r.TopLeft * by, r.TopRight * by, r.BottomRight * by, r.BottomLeft * by);

    /// <summary>About › Fonts (the Windows FontLicencesEditor, UI styles spec §11.1): each typeface the looks carry, its
    /// copyright, and its licence (the SIL Open Font License 1.1) as it came with the files, behind a button. Nothing is
    /// written to disk.</summary>
    internal Control FontLicences()
    {
        var panel = Titled(EditorIds.FontLicencesEditor);
        foreach (var family in LinuxFonts.All)
        {
            var licence = new TextBox
            {
                Text = LinuxFonts.Licence(family), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, IsVisible = false,
                Margin = new Thickness(0, 6, 0, 0), FontFamily = LinuxLooks.Effective.Font("MonoFont"),
            };
            var label = new TextBlock { Text = Ui.NoAccess(SettingsContext.Str("L.S.app.about.fonts.Show")) };
            var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) =>
            {
                licence.IsVisible = !licence.IsVisible;
                label.Text = Ui.NoAccess(SettingsContext.Str(licence.IsVisible ? "L.S.app.about.fonts.Hide" : "L.S.app.about.fonts.Show"));
            };
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var name = LinuxLooks.Text(family.Name, "Label");
            text.Children.Add(name);
            text.Children.Add(LinuxLooks.Text(family.Copyright, "Desc"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(text);
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            panel.Children.Add(row);
            panel.Children.Add(licence);
        }
        return panel;
    }
}
