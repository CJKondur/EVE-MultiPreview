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

    public static HubParts BuildHub()
    {
        var icon = new TextBlock
        {
            Text = Glyphs.Bell,
            FontFamily = IconFont,
            FontSize = 18,
            Foreground = FluentPalette.Brush("Fx.Text"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bell = new Border
        {
            Width = BellSize,
            Height = BellSize,
            CornerRadius = new CornerRadius(BellSize / 2),
            Background = FluentPalette.Brush("Fx.Flyout"),
            BorderBrush = FluentPalette.Brush("Fx.FlyoutBorder"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badgeLabel = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = FluentPalette.Brush("Fx.OnAlertBadge"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Over the bell's top-right edge, 4 px outside it, as the reference.
        double inset = (HubWindowSize - BellSize) / 2 - 4;
        var badge = new Border
        {
            MinWidth = 18,
            Height = 18,
            Padding = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(9),
            Background = FluentPalette.Brush("Fx.AlertBadge"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, inset, inset, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = badgeLabel,
        };
        var root = new Grid { Width = HubWindowSize, Height = HubWindowSize };
        root.Children.Add(bell);
        root.Children.Add(badge);
        return new HubParts(root, bell, icon, badge, badgeLabel);
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
    public static Border BuildToast(string character, string alertType, Color severityColor, string? system) =>
        BuildToast(character, alertType, severityColor, system, out _);

    /// <summary>The same toast, and its second line, so a toast that shows an age can change it.</summary>
    public static Border BuildToast(string character, string alertType, Color severityColor, string? system, out TextBlock secondLine)
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
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = FluentPalette.Brush("Fx.Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        secondLine = new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = FluentPalette.Brush("Fx.TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        };
        text.Children.Add(secondLine);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        row.Children.Add(text);
        return new Border
        {
            Width = ToastWidth,
            Height = ToastHeight,
            Background = FluentPalette.Brush("Fx.Flyout"),
            BorderBrush = FluentPalette.Brush("Fx.FlyoutBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
        };
    }

    /// <summary>One button of the direction picker (28 × 28): the current direction in accent, the others on the flyout
    /// surface. <paramref name="round"/> makes the centre close button.</summary>
    public static Border BuildPickerButton(string glyph, bool current, double size = 28, bool round = false) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(round ? size / 2 : 4),
        Background = FluentPalette.Brush(current ? "Fx.Accent" : round ? "Fx.Control" : "Fx.Flyout"),
        BorderBrush = FluentPalette.Brush(current ? "Fx.Accent" : "Fx.FlyoutBorder"),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand,
        Child = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 12,
            Foreground = FluentPalette.Brush(current ? "Fx.OnAccent" : "Fx.Text"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

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

    /// <summary>The CJK font for the current language (spec §7), for the hub's windows.</summary>
    public static FontFamily TextFont => UiFont.For(LocalizationService.CurrentLanguage);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
