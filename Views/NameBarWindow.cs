using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Interop;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace EveMultiPreview.Views;

/// <summary>
/// IDEAS #38, the compact name bar: one button per running character, in one row at the top centre of the primary monitor's
/// work area, wrapping into more rows past 90 % of its width. A click calls <c>switchTo</c> with the character's name (App hands
/// it the switch a thumbnail's click makes); resting the pointer on a button for <see cref="HoverDelay"/> shows that client live
/// in the preview below the bar, and leaving takes it away. The window never takes the focus from the game (no-activate, tool
/// window, in the topmost band by SetWindowPos alone and never by WPF's Topmost, re-raised every 250 ms like the hotkey sheet) and it sends the
/// client nothing: its only call into user32 is the z-order's SetWindowPos. It is not dragged and its place is not saved.
/// Built in code with the look's tokens, so a look restyles it and the literal-colour scan reads it. A name is the player's or
/// the game's: it is shown, never a format, never an access key (<c>AccessKeys.Verbatim</c>), and masked in streamer mode.
/// </summary>
public sealed class NameBarWindow : Window
{
    /// <summary>How long the pointer rests on a button before the preview shows.</summary>
    public static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How often the bar re-asserts its place above the thumbnails.</summary>
    public static readonly TimeSpan RaiseEvery = HotkeySheetWindow.RaiseEvery;

    /// <summary>The share of the work area's width a row may take before the buttons wrap.</summary>
    public const double WidthShare = 0.9;

    /// <summary>The size of the preview (DIPs); it fits the client's own shape inside it.</summary>
    public const double PreviewWidth = 360, PreviewHeight = 225;

    /// <summary>The space between the bar and the preview.</summary>
    public const double PreviewGap = 4;

    private readonly Func<IReadOnlyList<NameBarEntry>> _entries;
    private readonly Action<string> _switchTo;
    private readonly Func<string, IntPtr> _sourceOf;
    private readonly IPreviewSurface _preview;
    private readonly Rect _area;
    private readonly Func<IntPtr, IntPtr, uint, bool> _setWindowPos;
    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly Border _root = new();
    private readonly List<string> _texts = new();
    private readonly List<Button> _buttons = new();
    private readonly List<TextBlock> _labels = new();   // parallel to _buttons: the name on each, for the in-place repaint
    private readonly Dictionary<string, Button> _byName = new(StringComparer.OrdinalIgnoreCase);
    private List<(NameBarEntry Entry, string Text)> _shown = new();
    private string? _hovered;
    private double _placedLeft = double.NaN;
    private bool _closed;

    /// <summary>The names the buttons show, in order (masked in streamer mode). Tests.</summary>
    public IReadOnlyList<string> Texts => _texts;

    /// <summary>The buttons, in tab order. Tests.</summary>
    public IReadOnlyList<Button> Buttons => _buttons;

    /// <summary>The button of a character, or null. Tests.</summary>
    public Button? ButtonOf(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The wait before the preview: runs only while the pointer rests on a button (tests call
    /// <see cref="HoverElapsed"/> instead of waiting).</summary>
    public DispatcherTimer HoverTimer { get; } = new() { Interval = HoverDelay };

    /// <summary>The re-raise timer: runs only while the bar shows (tests invoke <see cref="ReRaise"/> instead of waiting).</summary>
    public DispatcherTimer RaiseTimer { get; } = new() { Interval = RaiseEvery };

    /// <summary>Whether the preview is showing now. Tests.</summary>
    public bool PreviewShowing { get; private set; }

    /// <param name="entries">The buttons, asked at each <see cref="Refresh"/>.</param>
    /// <param name="switchTo">What a click does: the switch to that character's client, by name.</param>
    /// <param name="sourceOf">The client window of a character (0: none), for the preview.</param>
    /// <param name="preview">Where the preview is drawn; the bar disposes it when it closes.</param>
    /// <param name="workAreaDips">The work area it is placed and sized in; default is the primary monitor's.</param>
    /// <param name="setWindowPos">The z-order call (tests record it); default is user32's SetWindowPos.</param>
    public NameBarWindow(Func<IReadOnlyList<NameBarEntry>> entries, Action<string> switchTo, Func<string, IntPtr> sourceOf,
        IPreviewSurface preview, Rect? workAreaDips = null, Func<IntPtr, IntPtr, uint, bool>? setWindowPos = null)
    {
        _entries = entries;
        _switchTo = switchTo;
        _sourceOf = sourceOf;
        _preview = preview;
        _setWindowPos = setWindowPos ?? ((h, after, flags) => User32.SetWindowPos(h, after, 0, 0, 0, 0, flags));
        _area = workAreaDips ?? PrimaryWorkArea();

        UiStyles.Attach(this, StyledAs.Surface);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = _area.Width * WidthShare;
        UiFont.Apply(this, LocalizationService.CurrentLanguage);
        Title = LocalizationService.Str("L.NameBar.Title", "Character name bar");
        SetResourceReference(BackgroundProperty, "Fx.BgBrush");

        _panel.Margin = new Thickness(4);
        _root.Child = _panel;
        _root.SetResourceReference(Border.BackgroundProperty, "Fx.BgBrush");
        _root.SetResourceReference(Border.BorderBrushProperty, "Fx.FlyoutBorderBrush");
        _root.BorderThickness = new Thickness(1);
        Content = _root;
        KeyboardNavigation.SetTabNavigation(_panel, KeyboardNavigationMode.Continue);

        Refresh();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Top = _area.Top;
        Place();

        NoFocusStyle.Apply(this);
        SizeChanged += (_, _) => { if (Math.Abs(Left - _placedLeft) < 0.5) Place(); };   // only while nobody moved it
        HoverTimer.Tick += (_, _) => HoverElapsed();
        RaiseTimer.Tick += (_, _) => ReRaise();
        Loaded += (_, _) => { RaiseTimer.Start(); ReRaise(); };
        Closed += (_, _) =>
        {
            _closed = true;
            HoverTimer.Stop();
            RaiseTimer.Stop();
            PreviewShowing = false;
            _preview.Dispose();
        };
    }

    /// <summary>Asks for the buttons again. Nothing changed: nothing is touched. Only an alert's severity or the client in front
    /// changed: the same buttons are repainted in place, so the keyboard focus and the pointer's hover (and the preview under it)
    /// stay. A name, its streamer mask or a group colour changed (the list or a stripe differs): the buttons are rebuilt. The
    /// window's thread.</summary>
    public void Refresh()
    {
        var now = _entries().Select(e => (Entry: e, Text: StreamerMask.Name(StreamerKind.Character, e.Name))).ToList();
        if (now.SequenceEqual(_shown) && _buttons.Count == now.Count) return;
        if (SameButtons(now))
        {
            _shown = now;
            for (int i = 0; i < now.Count; i++) Repaint(_buttons[i], _labels[i], now[i].Entry);
            return;
        }
        _shown = now;
        _panel.Children.Clear();
        _buttons.Clear();
        _labels.Clear();
        _byName.Clear();
        _texts.Clear();
        var template = ButtonTemplate();
        for (int i = 0; i < now.Count; i++)
        {
            var button = MakeButton(now[i].Entry, now[i].Text, template, i);
            _buttons.Add(button);
            _byName[now[i].Entry.Name] = button;
            _texts.Add(now[i].Text);
            _panel.Children.Add(button);
        }
        _root.Visibility = now.Count == 0 ? Visibility.Collapsed : Visibility.Visible;   // no client: no strip
        if (_hovered != null && !_byName.ContainsKey(_hovered)) HoverOff();            // its client is gone
    }

    /// <summary>Whether the buttons now drawn are the buttons <paramref name="now"/> asks for: the same names in the same order,
    /// shown the same way (a streamer mask) and with the same group stripe. Severity and the front client may differ.</summary>
    private bool SameButtons(List<(NameBarEntry Entry, string Text)> now) =>
        now.Count > 0 && now.Count == _shown.Count && _buttons.Count == now.Count
        && now.Zip(_shown).All(p => p.First.Text == p.Second.Text && p.First.Entry.Name == p.Second.Entry.Name
                                    && p.First.Entry.GroupColour == p.Second.Entry.GroupColour);

    /// <summary>Gives a button its entry's alert border, fill and weight without making it again.</summary>
    private void Repaint(Button button, TextBlock label, NameBarEntry entry)
    {
        if (entry.Active) label.FontWeight = FontWeights.SemiBold; else label.ClearValue(TextBlock.FontWeightProperty);
        Paint(button, entry, hovered: string.Equals(_hovered, entry.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Puts the bar back at the top of the topmost band without activating it.</summary>
    public void ReRaise()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) _setWindowPos(handle, User32.HWND_TOPMOST, HotkeySheetWindow.RaiseFlags);
    }

    /// <summary>A click on a character's button, or a screen reader's: the preview goes, then the switch is asked for.</summary>
    public void Click(string name)
    {
        HoverOff();
        _switchTo(name);
    }

    /// <summary>The pointer rests on a character's button: the preview follows after <see cref="HoverDelay"/>.</summary>
    public void HoverOn(string name)
    {
        if (_closed) return;
        HoverOff();
        _hovered = name;
        HoverTimer.Start();
    }

    /// <summary>The pointer left the button: no preview, and none is coming.</summary>
    public void HoverOff()
    {
        HoverTimer.Stop();
        _hovered = null;
        if (!PreviewShowing) return;
        PreviewShowing = false;
        _preview.Hide();
    }

    /// <summary>What the wait's end does: shows the live picture of the client the pointer is on, if that client still has a
    /// window. The timer's tick; tests call it instead of waiting.</summary>
    public void HoverElapsed()
    {
        HoverTimer.Stop();
        if (_closed || _hovered is not { } name) return;
        var source = _sourceOf(name);
        if (source == IntPtr.Zero) return;
        _preview.Show(source, PreviewRect(name));
        PreviewShowing = true;
    }

    /// <summary>Where the preview goes: centred under the button, below the bar, inside the work area.</summary>
    private Rect PreviewRect(string name)
    {
        double centre = Left + ActualWidth / 2;
        if (_byName.TryGetValue(name, out var button) && button.IsLoaded)
        {
            try { centre = Left + button.TransformToAncestor(this).Transform(new Point(button.ActualWidth / 2, 0)).X; }
            catch (InvalidOperationException) { /* not in the visual tree yet: the bar's centre */ }
        }
        double left = Math.Clamp(centre - PreviewWidth / 2, _area.Left, Math.Max(_area.Left, _area.Right - PreviewWidth));
        double top = Math.Min(Top + ActualHeight + PreviewGap, Math.Max(_area.Top, _area.Bottom - PreviewHeight));
        return new Rect(left, top, PreviewWidth, PreviewHeight);
    }

    /// <summary>The bar's place: the top centre of the work area, from its own size.</summary>
    private void Place()
    {
        _root.Measure(new Size(MaxWidth, double.PositiveInfinity));
        double width = ActualWidth > 0 ? ActualWidth : _root.DesiredSize.Width;
        _placedLeft = _area.Left + (_area.Width - width) / 2;
        Left = _placedLeft;
    }

    private static Rect PrimaryWorkArea()
    {
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        double scale = DpiHelper.GetScaleFactorForPoint(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
        return new Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
    }

    // ── the buttons ──

    /// <summary>The button's skin: the frame it is given (background, border, padding) around its content. One per refresh.</summary>
    private static ControlTemplate ButtonTemplate()
    {
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        frame.SetResourceReference(Border.CornerRadiusProperty, "Fx.Radius.Control");
        frame.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        return new ControlTemplate(typeof(Button)) { VisualTree = frame };
    }

    private Button MakeButton(NameBarEntry entry, string shownName, ControlTemplate template, int index)
    {
        var label = AccessKeys.Verbatim(shownName);
        label.TextWrapping = TextWrapping.NoWrap;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextBrush");
        label.SetResourceReference(TextBlock.FontSizeProperty, "Fx.Size.Body");
        if (entry.Active) label.FontWeight = FontWeights.SemiBold;

        // The stripe is the player's own group colour (a colour of theirs, not the look's).
        var content = new DockPanel { LastChildFill = true };
        if (entry.GroupColour is { } colour)
        {
            var stripe = new Border { Width = 4, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ThumbnailManager.ParseColor(colour)) };
            DockPanel.SetDock(stripe, System.Windows.Controls.Dock.Left);
            content.Children.Add(stripe);
        }
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            Template = template,
            Margin = new Thickness(2),
            Padding = new Thickness(10, 5, 12, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            IsTabStop = true,
            TabIndex = index,
        };
        AutomationProperties.SetName(button, shownName);   // the name as shown, "_" and all: it is not a label with an access key
        Paint(button, entry, hovered: false);
        _labels.Add(label);
        // The entry can move on (a new severity, a new front client) while the button stays: paint from the current one.
        button.Click += (_, _) => Click(entry.Name);
        button.MouseEnter += (_, _) => { Paint(button, _shown[index].Entry, hovered: true); HoverOn(entry.Name); };
        button.MouseLeave += (_, _) => { Paint(button, _shown[index].Entry, hovered: false); HoverOff(); };
        return button;
    }

    /// <summary>The look's colours for a button: the border says the worst unseen alert (critical, warning, info: the look's
    /// status colours), the fill says the client in front or the pointer.</summary>
    private static void Paint(Button button, NameBarEntry entry, bool hovered)
    {
        string border = entry.Severity?.ToLowerInvariant() switch
        {
            "critical" => "Fx.CriticalBrush",
            "warning" => "Fx.WarningBrush",
            "info" => "Fx.InfoBrush",
            _ => "Fx.FlyoutBorderBrush",   // the client in front and the pointer are told by the fill alone: an accent border is the info colour in some looks
        };
        button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, entry.Active || hovered ? "Fx.AccentSoftBrush" : "Fx.CardBrush");
        button.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, border);
        button.BorderThickness = new Thickness(entry.Severity != null ? 2 : 1);
    }
}
