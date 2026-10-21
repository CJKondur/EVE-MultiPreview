using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FontFamily = System.Windows.Media.FontFamily;

namespace EveMultiPreview.Views;

/// <summary>The alert hub's look (Plan 4, the F2 reference): a round bell with a count badge, toasts with a severity bar,
/// and the stacking-direction picker. The hub and its toasts are windows built in code, so their parts are built here,
/// where tests can render them without showing anything. Sizes are WPF units.</summary>
public static class AlertHubVisuals
{
    /// <summary>The hub window stays 68 × 68, as before: saved hub positions and the toast stack are measured from it.</summary>
    public const double HubWindowSize = 68;
    public const double BellSize = 44;
    public const double ToastWidth = 300, ToastHeight = 64, ToastGap = 8;

    /// <summary>The name of the crossed bell on a toast whose alert's sound was held (tests look it up by it).</summary>
    public const string SoundHeldMarkName = "SoundHeldMark";

    /// <summary>How many toasts fit between the hub and the edge of its monitor's work area in the stacking direction (0 up,
    /// 1 right, 2 down, 3 left), at least one: the newest always shows (IDEAS P39: a sideways stack ran off the screen
    /// after about six).</summary>
    public static int ToastsThatFit(int direction, Rect hub, Rect workArea)
    {
        double room = direction switch
        {
            0 => hub.Top - workArea.Top,
            1 => workArea.Right - hub.Right,
            2 => workArea.Bottom - hub.Bottom,
            _ => hub.Left - workArea.Left,
        };
        double step = direction is 1 or 3 ? ToastWidth + ToastGap : ToastHeight + ToastGap;
        return Math.Max(1, (int)Math.Floor(room / step));
    }

    /// <summary>Segoe Fluent Icons code points.</summary>
    public static class Glyphs
    {
        public const string Bell = "\U0000EA8F", Pause = "\U0000E769", Close = "\U0000E711",
            Up = "\U0000E70E", Right = "\U0000E76C", Down = "\U0000E70D", Left = "\U0000E76B",
            History = "\U0000E81C", DismissAll = "\U0000EA99";
    }

    /// <summary>One rule for the hub's icon: ✕ while the direction picker is open, the pause sign while hotkeys are paused,
    /// else the bell. (The old icon showed 🚨 from startup until the first click, and a click while paused lost the
    /// pause sign.)</summary>
    public static string HubGlyph(bool pickerOpen, bool suspended) =>
        pickerOpen ? Glyphs.Close : suspended ? Glyphs.Pause : Glyphs.Bell;

    public static string BadgeText(int count) => count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The hub's parts. <see cref="Bell"/> takes the clicks and drags.</summary>
    public sealed record HubParts(Grid Root, Border Bell, TextBlock Icon, Border Badge, TextBlock BadgeLabel);

    private static FontFamily IconFont => new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>The bell's corners for the current look: a circle, or square (Fx.Hub.Round).</summary>
    public static CornerRadius BellCorners => new(UiPalette.Current.HubRound ? BellSize / 2 : 0);

    public static HubParts BuildHub()
    {
        var icon = new TextBlock
        {
            Text = Glyphs.Bell,
            FontFamily = IconFont,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextBrush");
        var bell = new Border
        {
            Width = BellSize,
            Height = BellSize,
            CornerRadius = BellCorners,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bell.SetResourceReference(Border.BackgroundProperty, "Fx.FlyoutBrush");
        bell.SetResourceReference(Border.BorderBrushProperty, "Fx.FlyoutBorderBrush");
        var badgeLabel = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeLabel.SetResourceReference(TextBlock.ForegroundProperty, "Fx.OnAlertBadgeBrush");
        // Over the bell's top-right edge, 4 px outside it, as the reference.
        double inset = (HubWindowSize - BellSize) / 2 - 4;
        var badge = new Border
        {
            MinWidth = 18,
            Height = 18,
            Padding = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, inset, inset, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = badgeLabel,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Fx.AlertBadgeBrush");
        var root = new Grid { Width = HubWindowSize, Height = HubWindowSize };
        root.Children.Add(bell);
        root.Children.Add(badge);
        return new HubParts(root, bell, icon, badge, badgeLabel);
    }

    /// <summary>The colour the badge pulses to after an alert (AHK: badge colour to white and back, 4 x 200 ms): white where
    /// white shows against the look's badge (2.5:1 or more), else the badge's own text colour (Fx.OnAlertBadge), which the
    /// look makes readable on it. White on a pale badge (Dusk, Compact) was no pulse at all (S3F-m16).</summary>
    public static Color PulseTo(Color badge, Color onBadge) => Contrast(badge, Colors.White) >= 2.5 ? Colors.White : onBadge;

    /// <summary>WCAG's contrast ratio of two colours, 1 to 21.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v) { double c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Shows the unread count on the badge, or hides it at 0.</summary>
    public static void SetBadge(HubParts hub, int count)
    {
        hub.BadgeLabel.Text = BadgeText(count);
        hub.Badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A toast says how old its alert is only when it stays up longer than an alert reads "now" (IDEAS P34).
    /// Toasts last 6 seconds unless the player changed it, and those never show an age.</summary>
    public static bool ToastShowsAge(int toastSeconds) => toastSeconds > AlertAge.NowSeconds;

    /// <summary>"Logi · Under attack" over "Jita · click to switch" (or "Click to switch" when the system isn't known).
    /// With an <paramref name="age"/> that no longer reads "now": "Jita · 40 s · click to switch".</summary>
    public static (string Title, string Subtitle) ToastText(string character, string alertType, string? system, TimeSpan? age = null)
    {
        // Streamer mode (IDEAS #19): only the drawn text is masked; the toast keeps the real name for its menu and its click.
        character = StreamerMask.Name(StreamerKind.Character, character);
        system = StreamerMask.Name(StreamerKind.System, system);
        string title = $"{character} · {AlertHub.EventDisplayName(alertType)}";
        bool noSystem = string.IsNullOrWhiteSpace(system);
        if (age is { } old && old.TotalSeconds >= AlertAge.NowSeconds)
            return (title, noSystem
                ? string.Format(LocalizationService.Str("L.Hub.AgeClickFmt", "{0} · click to switch"), AlertAge.Text(old))
                : string.Format(LocalizationService.Str("L.Hub.SystemAgeClickFmt", "{0} · {1} · click to switch"), system, AlertAge.Text(old)));
        return (title, noSystem
            ? LocalizationService.Str("L.Hub.ClickToSwitch", "Click to switch")
            : string.Format(LocalizationService.Str("L.Hub.SystemClickFmt", "{0} · click to switch"), system));
    }

    /// <summary>A toast: the flyout surface with a bar in the severity's colour (the player's colours, Alerts › Look &amp;
    /// severity), the character and event, and where it happened. Long names end in "…"; the toast never grows.</summary>
    public static Border BuildToast(string character, string alertType, Color severityColor, string? system, bool soundHeld = false) =>
        BuildToast(character, alertType, severityColor, system, out _, soundHeld);

    /// <summary>The same toast, and its second line, so a toast that shows an age can change it. With
    /// <paramref name="soundHeld"/> (do-not-disturb held the alert's sound, P81) a crossed bell stands at the toast's
    /// right edge (<see cref="SoundHeldMarkName"/>); the text keeps the room that is left.</summary>
    public static Border BuildToast(string character, string alertType, Color severityColor, string? system, out TextBlock secondLine,
        bool soundHeld = false)
    {
        var (title, subtitle) = ToastText(character, alertType, system);
        var bar = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = Frozen(severityColor),
            Margin = new Thickness(0, 0, 12, 0),
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var firstLine = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        firstLine.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextBrush");
        text.Children.Add(firstLine);
        secondLine = new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        };
        secondLine.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        text.Children.Add(secondLine);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        if (soundHeld)
        {
            // The same crossed bell the tray's tip uses where it has no room for words. Docked before the text, which
            // fills what is left, so a long name still ends in an ellipsis and the mark is never cut.
            var held = LocalizationService.Str("L.Hist.SoundHeld", "Sound held");
            var mark = new TextBlock
            {
                Name = SoundHeldMarkName,
                Text = TrayMenu.DoNotDisturbMark,
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = held,
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
            System.Windows.Automation.AutomationProperties.SetName(mark, held);
            DockPanel.SetDock(mark, Dock.Right);
            row.Children.Add(mark);
        }
        row.Children.Add(text);
        var toast = new Border
        {
            Width = ToastWidth,
            Height = ToastHeight,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
        };
        toast.SetResourceReference(Border.BackgroundProperty, "Fx.FlyoutBrush");
        toast.SetResourceReference(Border.BorderBrushProperty, "Fx.FlyoutBorderBrush");
        toast.SetResourceReference(Border.CornerRadiusProperty, "Fx.FlyoutRadius");
        return toast;
    }

    /// <summary>One button of the direction picker (28 × 28): the current direction in accent, the others on the flyout
    /// surface. <paramref name="round"/> makes the centre close button.</summary>
    public static Border BuildPickerButton(string glyph, bool current, double size = 28, bool round = false)
    {
        var mark = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, current ? "Fx.OnAccentBrush" : "Fx.TextBrush");
        var button = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(round ? size / 2 : 4),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = mark,
        };
        button.SetResourceReference(Border.BackgroundProperty, current ? "Fx.AccentBrush" : round ? "Fx.ControlBrush" : "Fx.FlyoutBrush");
        button.SetResourceReference(Border.BorderBrushProperty, current ? "Fx.AccentBrush" : "Fx.FlyoutBorderBrush");
        return button;
    }

    /// <summary>A round button of the hub's menu (the picker's corners): a glyph, with what it does as its tooltip and
    /// its screen-reader name.</summary>
    public static Border BuildPickerAction(string glyph, string what)
    {
        var button = BuildPickerButton(glyph, current: false, size: PickerActionSize, round: true);
        button.ToolTip = what;
        System.Windows.Automation.AutomationProperties.SetName(button, what);
        return button;
    }

    public const double PickerActionSize = 26;

    /// <summary>The font of the current language and look (spec §7; UI styles plan S2), for the hub's windows: read when
    /// a toast or the picker is made.</summary>
    public static FontFamily TextFont => UiFont.Of(LocalizationService.CurrentLanguage);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
