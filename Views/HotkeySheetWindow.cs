using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using EveMultiPreview.Interop;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EveMultiPreview.Views;

/// <summary>
/// IDEAS #16: the hotkeys of the current profile on a window over the thumbnails. It never takes the focus from the game
/// (not activated, no-activate and tool-window styles, topmost by WPF's Topmost only), and a click on it or its 30 seconds
/// close it. Built in code, in the shape of the quick-switch wheel's surface, so a look restyles it and the literal scan
/// reads it. The rows' texts are a person's and a card's words: they are shown, never a format.
/// </summary>
public sealed class HotkeySheetWindow : Window
{
    /// <summary>How long it stays when nobody clicks it.</summary>
    public static readonly TimeSpan Lasts = TimeSpan.FromSeconds(30);

    private readonly List<string> _texts = new();
    private readonly DispatcherTimer _timer = new() { Interval = Lasts };
    private bool _closed;

    /// <summary>Every text the window shows, in order (tests).</summary>
    public IReadOnlyList<string> Texts => _texts;

    public HotkeySheetWindow(IReadOnlyList<HotkeySheetRow> rows)
    {
        UiStyles.Attach(this, StyledAs.Surface);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = null;                                   // the border below is the surface
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;   // as the wheel: the title and the foot stay on screen, the rows scroll
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UiFont.Apply(this, LocalizationService.CurrentLanguage);
        Title = Say("L.Sheet.Title", "Hotkeys of this profile", record: false);

        Content = Build(rows);

        SourceInitialized += (_, _) => NeverActivate();
        MouseLeftButtonUp += (_, _) => Dismiss();
        _timer.Tick += (_, _) => Dismiss();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _closed = true; _timer.Stop(); };
    }

    /// <summary>Stops the timer and closes, once.</summary>
    public void Dismiss()
    {
        _timer.Stop();
        if (_closed) return;
        _closed = true;
        Close();
    }

    private UIElement Build(IReadOnlyList<HotkeySheetRow> rows)
    {
        // A dock panel, so the rows get the height the title and the foot leave and scroll in it.
        var panel = new DockPanel { Margin = new Thickness(16, 12, 16, 12), LastChildFill = true };
        void Add(UIElement e, Dock side) { DockPanel.SetDock(e, side); panel.Children.Add(e); }
        Add(Line(Say("L.Sheet.Title", "Hotkeys of this profile"), "Fx.Size.Section", "Fx.TextBrush", bold: true), System.Windows.Controls.Dock.Top);
        var close = LocalizationService.Str("L.Sheet.Close", "Click to close");   // shown last, so recorded last
        Add(Line(close, "Fx.Size.Caption", "Fx.TextSecondaryBrush", top: 10), System.Windows.Controls.Dock.Bottom);

        int shown = Math.Min(rows.Count, HotkeySheet.MaxRows);
        string? moreText = null;
        if (rows.Count > HotkeySheet.MaxRows)
        {
            var more = string.Format(Say("L.Sheet.More", "and {0} more on Clients › Hotkeys & switching", record: false), rows.Count - HotkeySheet.MaxRows);
            moreText = more;
            Add(Line(more, "Fx.Size.Body", "Fx.TextSecondaryBrush", top: 8), System.Windows.Controls.Dock.Bottom);
        }

        if (shown == 0)
        {
            panel.Children.Add(Line(Say("L.Sheet.None", "No hotkeys are set. Set them on Clients › Hotkeys & switching."), "Fx.Size.Body", "Fx.TextSecondaryBrush", top: 8));
        }
        else
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < shown; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var action = Line(rows[i].Action, "Fx.Size.Body", "Fx.TextBrush", top: i == 0 ? 0 : 3);
                var keys = Line(rows[i].Keys, "Fx.Size.Body", "Fx.TextSecondaryBrush", top: i == 0 ? 0 : 3, mono: true);
                keys.Margin = new Thickness(24, keys.Margin.Top, 0, 0);
                keys.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(action, i);
                Grid.SetRow(keys, i);
                Grid.SetColumn(keys, 1);
                grid.Children.Add(action);
                grid.Children.Add(keys);
                Record(rows[i].Action);
                Record(rows[i].Keys);
            }
            // Only the rows scroll (the wheel of the mouse reaches a window that is never active).
            panel.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 0, 0) });
        }

        if (moreText != null) Record(moreText);
        Record(close);

        var card = new Border { Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "Fx.BgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "Fx.FlyoutBorderBrush");
        card.SetResourceReference(Border.BorderThicknessProperty, "Fx.Line.Card");
        card.SetResourceReference(Border.CornerRadiusProperty, "Fx.FlyoutRadius");
        return card;
    }

    /// <summary>A text block in the look's size and colour. With <paramref name="record"/> off the text is not added to
    /// <see cref="Texts"/> (it is recorded where it is shown).</summary>
    private TextBlock Line(string text, string sizeKey, string brushKey, bool bold = false, double top = 0, bool mono = false, bool record = false)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        block.SetResourceReference(TextBlock.FontSizeProperty, sizeKey);
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        if (mono) block.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.MonoFont");
        if (bold) block.FontWeight = FontWeights.SemiBold;
        if (record) Record(text);
        return block;
    }

    /// <summary>The catalog's text, recorded as shown unless <paramref name="record"/> is off.</summary>
    private string Say(string key, string english, bool record = true)
    {
        var text = LocalizationService.Str(key, english);
        if (record) Record(text);
        return text;
    }

    private void Record(string text) => _texts.Add(text);

    private void NeverActivate()
    {
        var handle = new WindowInteropHelper(this).Handle;
        int ex = User32.GetWindowLong(handle, User32.GWL_EXSTYLE);
        User32.SetWindowLong(handle, User32.GWL_EXSTYLE, ex | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW);
    }
}
