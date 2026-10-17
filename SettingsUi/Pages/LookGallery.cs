using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EveMultiPreview.Services;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace EveMultiPreview.SettingsUi;

/// <summary>The look gallery (UI styles spec §9.2): one card per look, in the registry's order (Classic first), each a
/// radio button of the group "Look" with the look's preview, name and description. One click picks a look, and the whole
/// app takes it at once. The current card has an accent outline, a check mark and the word "Current". The gallery knows
/// the looks only through UiStyles.All: a look added later gets its card without a change here. App settings › Look shows
/// it, and so does the setup wizard.</summary>
public sealed class LookGallery : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "app.look.gallery";

    private readonly Func<string?> _saved;
    private readonly Action<UiStyle> _pick;
    private readonly WrapPanel _panel = new();
    private readonly List<RadioButton> _cards = new();
    private readonly LookSample _sample = new();
    private readonly Dictionary<UiStyle, Border> _pictures = new();
    private readonly Queue<UiStyle> _toBuild = new();
    private bool _marking, _listening, _scheduled;

    /// <summary>App settings › Look: the saved look is the settings file's; a pick saves it, switches the app and says so.</summary>
    public LookGallery(SettingsContext ctx) : this(() => ctx.S.UiStyle, style => Pick(ctx, style), 268, framed: true) { }

    /// <param name="saved">The id the settings hold (a missing, empty or unknown one shows the default look's card as
    /// current, UiStyles.Find).</param>
    /// <param name="pick">What a pick does.</param>
    /// <param name="cardWidth">A card's width: four fit in a row of the wizard, more on the page.</param>
    public LookGallery(Func<string?> saved, Action<UiStyle> pick, double cardWidth = 268) : this(saved, pick, cardWidth, framed: false) { }

    private LookGallery(Func<string?> saved, Action<UiStyle> pick, double cardWidth, bool framed)
    {
        _saved = saved;
        _pick = pick;
        foreach (var style in UiStyles.All) _panel.Children.Add(Card(style, cardWidth));
        // The cards are one radio group for the keyboard too (spec §9.2): the arrow keys move from card to card and stay
        // in the gallery, and the gallery is one tab stop, not one per look (Tab comes back to the card it left).
        KeyboardNavigation.SetDirectionalNavigation(_panel, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(_panel, KeyboardNavigationMode.Once);
        // The saved look's card is built with the page, the others one per idle moment once it shows (spec §9.2, cost).
        var first = UiStyles.Find(saved());
        Build(first);
        foreach (var style in UiStyles.All) if (style != first) _toBuild.Enqueue(style);
        Content = framed ? EditorCard.Build(ItemId, null, _panel) : _panel;
        Mark();
        Action changed = () => { if (Dispatcher.CheckAccess()) Mark(); };
        Loaded += (_, _) =>
        {
            if (!_listening) { _listening = true; UiStyles.Changed += changed; }
            BuildLater();
        };
        Unloaded += (_, _) => { if (_listening) { _listening = false; UiStyles.Changed -= changed; } };
    }

    /// <summary>The cards, in the gallery's order.</summary>
    public IReadOnlyList<RadioButton> Cards => _cards;

    /// <summary>The card of a look.</summary>
    public RadioButton CardOf(UiStyle style) => _cards.Single(c => c.Tag == style);

    /// <summary>Whether a look's card shows its miniature yet (until then, its placeholder).</summary>
    public bool IsBuilt(UiStyle style) => _pictures[style].Child is PreviewHost;

    /// <summary>After an import or a reload the file may name another look.</summary>
    public void Refresh() => Mark();

    private RadioButton Card(UiStyle style, double width)
    {
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        name.SetResourceReference(StyleProperty, "Fx.Label");
        Loc.SetKey(name, "L.S.Style." + style.Id);
        var desc = new TextBlock();
        desc.SetResourceReference(StyleProperty, "Fx.Desc");
        Loc.SetKey(desc, "L.S.Style." + style.Id + ".Desc");
        var body = new StackPanel();
        var picture = new Border { Child = Placeholder(style) };
        _pictures[style] = picture;
        body.Children.Add(picture);
        body.Children.Add(name);
        body.Children.Add(desc);
        var card = new RadioButton { GroupName = "Look", Content = body, Tag = style, Width = width };
        card.SetResourceReference(StyleProperty, "Look.Card");
        Loc.SetNameKey(card, "L.S.Style." + style.Id);
        card.SetResourceReference(AutomationProperties.HelpTextProperty, "L.S.Style." + style.Id + ".Desc");
        // Marked again after the pick: a pick that did not take (S3F-m6) leaves the card of the look still shown checked.
        card.Checked += (_, _) => { if (_marking) return; _pick(style); Mark(); };
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (card.IsChecked != true) card.IsChecked = true;   // Space picks, as a radio button does; Enter does too
        };
        _cards.Add(card);
        return card;
    }

    /// <summary>A card's picture before its preview is made: the look's page colour with the look's name in its text
    /// colour (spec §9.2). The look's own dictionary, not the window's: the card shows that look.</summary>
    private static FrameworkElement Placeholder(UiStyle style)
    {
        var look = style.Dictionary;
        var text = new TextBlock
        {
            Foreground = (Brush)look["Fx.TextBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Loc.SetKey(text, "L.S.Style." + style.Id);
        return new Border { Height = 120, Background = (Brush)look["Fx.BgBrush"], Child = text, IsHitTestVisible = false };
    }

    /// <summary>A look's miniature (spec §9.2): Fx.Preview over <paramref name="sample"/>, in that look (UiStyles.Scope),
    /// scaled to the card, inert. Its template is the look's own Fx.Preview or the base's, taken once: a card shows one look
    /// for its life, whatever the window shows. (Taken by a resource reference instead, a gallery made inside a page's
    /// template sets it while that template is being made, which WPF does not survive: measured while writing plan S2.)
    /// Its templates are made and measured here, before it is in a window, inside a try: a template WPF cannot make throws
    /// here, where it is written to the error log and the look's placeholder is shown instead. A trigger that is true when
    /// its template is made and sets a DynamicResource can still make WPF throw in a later layout pass (measured while
    /// writing plan S3); the kit and the looks set none in what the miniature makes (the plan's rule; LookTriggerRuleTests
    /// lists the templates that do, all outside it), and LookMiniatureTests makes every miniature.</summary>
    public static FrameworkElement Miniature(UiStyle style, LookSample sample)
    {
        try
        {
            var miniature = new ContentPresenter { Content = sample };
            UiStyles.Scope(miniature, style);
            miniature.ContentTemplate = (DataTemplate)(style.Dictionary.Contains("Fx.Preview") ? style.Dictionary["Fx.Preview"] : UiStyles.Base["Fx.Preview"]);
            miniature.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            return new PreviewHost { Child = new Viewbox { Child = miniature, Stretch = System.Windows.Media.Stretch.Uniform, StretchDirection = StretchDirection.DownOnly } };
        }
        catch (Exception e)
        {
            ErrorLog.Shared.Record(e, "UI styles", DateTime.Now);
            return Placeholder(style);
        }
    }

    private void Build(UiStyle style) => _pictures[style].Child = Miniature(style, _sample);

    /// <summary>The next card not yet built, at the dispatcher's next idle moment, one per moment, while the gallery is
    /// shown: a gallery taken off the screen stops, and goes on when it shows again. What is built stays built.</summary>
    private void BuildLater()
    {
        if (_scheduled || _toBuild.Count == 0) return;
        _scheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _scheduled = false;
            if (!IsLoaded) return;
            Build(_toBuild.Dequeue());
            BuildLater();
        }));
    }

    /// <summary>Checks the card of the saved look (an unknown id: the default look's), without picking it again.</summary>
    private void Mark()
    {
        var saved = UiStyles.Find(_saved());
        _marking = true;
        try { foreach (var card in _cards) card.IsChecked = card.Tag == saved; }
        finally { _marking = false; }
    }

    /// <summary>A pick on App settings › Look: saved through the usual debounced save, the whole app switched at once, and
    /// a toast. A window that failed to take the look is logged: UiStyles.Set finishes the switch before it throws (the S1
    /// carry), so every other window has it. A switch that did not happen at all (a Changing handler threw, so the look
    /// shown is still the old one) is logged too, and the file keeps naming the look shown: no toast (S3F-m6).</summary>
    private static void Pick(SettingsContext ctx, UiStyle style)
    {
        var before = ctx.S.UiStyle;
        ctx.S.UiStyle = style.Id;
        ctx.Applier.Request(ApplyScope.Save);
        try { UiStyles.Set(style); }
        catch (Exception e) { ErrorLog.Shared.Record(e, "UI styles", DateTime.Now); }
        if (UiStyles.Current != style)
        {
            ctx.S.UiStyle = before;
            ctx.Applier.Request(ApplyScope.Save);
            return;
        }
        ctx.Host.Toast(string.Format(SettingsContext.Str("L.S.app.look.Toast"), SettingsContext.Str("L.S.Style." + style.Id)));
    }
}
