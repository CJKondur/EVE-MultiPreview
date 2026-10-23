using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveMultiPreview.Interop;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.Views;

/// <summary>
/// IDEAS #14: the type-to-switch command palette. A text box and a list of at most <see cref="PaletteMatcher.MaxResults"/>
/// rows (running characters, their labels, groups, profiles) over the monitor under the cursor. Typing filters the list through
/// <see cref="PaletteMatcher"/>; Up and Down move; Enter runs the chosen row's command through the runner it was handed (App's
/// RunCommand, the one the commands from other programs use) and closes; Esc, a click away or the hotkey again closes without
/// running anything. It takes the keyboard the way the quick-switch wheel does (shown and activated, topmost, closed when it is
/// deactivated), so what is typed never reaches the game. When Windows does not give it the foreground on its own (a hotkey whose
/// key-down was hidden from the clients, a mouse button) it asks once through the repo's activation helper (the vk0xE8 bridge, which lands a few ms later) and, if it is
/// still not in front 400 ms on, closes: a topmost box that cannot take the keyboard would leave typing and Esc going to the game. Built in code in the shape of the wheel's surface, so a look restyles
/// it and the literal scan reads it. The rows' texts are a person's names: they are shown, never a format.
/// </summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteEntry> _entries;
    private readonly Action<RemoteCommand> _run;
    private readonly TextBox _input = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _placeholder = new();
    private readonly TextBlock _none = new();
    private IReadOnlyList<PaletteEntry> _shown = Array.Empty<PaletteEntry>();
    private bool _closed;
    private readonly Func<bool> _tryActivate;
    private bool _activationAsked;

    /// <param name="entries">Every row the palette may show (<see cref="CommandPalette.Entries"/>).</param>
    /// <param name="run">What Enter hands the chosen row's command to; App's RunCommand. Called after the palette has closed.</param>
    /// <param name="workAreaDips">The work area it is placed in; default is the monitor under the cursor.</param>
    /// <param name="tryActivate">One attempt to bring the window to the foreground, true when it is in front afterwards or the attempt
    /// is under way; default is <c>User32.ActivateWindow</c>, the vk0xE8 bridge a client switch uses, which lands a few ms later
    /// (tests hand one in).</param>
    public CommandPaletteWindow(IReadOnlyList<PaletteEntry> entries, Action<RemoteCommand> run, Rect? workAreaDips = null, Func<bool>? tryActivate = null)
    {
        _entries = entries;
        _run = run;
        _tryActivate = tryActivate ?? ActivateWithTheRepoHelper;
        var area = workAreaDips ?? CursorMonitorWorkArea();
        UiStyles.Attach(this, StyledAs.Surface);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;                                       // as the wheel: the z-order is WPF's alone
        AllowsTransparency = true;
        Background = null;                                    // the border below is the surface
        SizeToContent = SizeToContent.Height;
        Width = Math.Min(480, area.Width * 0.9);
        MaxHeight = area.Height * 0.9;
        UiFont.Apply(this, LocalizationService.CurrentLanguage);
        Title = LocalizationService.Str("L.Palette.Title", "Command palette");

        Content = Build();
        FocusManager.SetFocusedElement(this, _input);         // the text box has the keyboard from the first moment it is active
        Refresh();

        // Centred in that work area, from the content's own size; the height changes as the list does, the top stays.
        var card = (UIElement)Content;
        card.Measure(new Size(Width, MaxHeight));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - card.DesiredSize.Height) / 2;

        _input.TextChanged += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            if (EnsureActive()) { _input.Focus(); Keyboard.Focus(_input); }
        };
        Deactivated += (_, _) => Dismiss();
        Closing += (_, _) => _closed = true;   // Alt+F4: Deactivated fires inside the close, and Close() then would throw
        Closed += (_, _) => _closed = true;
    }

    /// <summary>What is typed (tests type by setting it).</summary>
    public string Query { get => _input.Text; set => _input.Text = value; }

    /// <summary>The rows now shown, in order: the text and the caption of each (tests).</summary>
    public IReadOnlyList<(string Name, string Caption)> Rows { get; private set; } = Array.Empty<(string, string)>();

    /// <summary>The commands of the rows now shown, in order (tests).</summary>
    public IReadOnlyList<RemoteCommand> Commands => _shown.Select(e => e.Command).ToList();

    /// <summary>The chosen row, or -1 with no row.</summary>
    public int SelectedIndex => _list.SelectedIndex;

    /// <summary>Whether the "nothing matches" note shows (tests).</summary>
    public bool NoneShown => _none.Visibility == Visibility.Visible;

    /// <summary>Whether the hint over the empty text box shows (tests).</summary>
    public bool PlaceholderShown => _placeholder.Visibility == Visibility.Visible;

    /// <summary>Called from Loaded: true when the window is in front. A window that is not gets one attempt through the activation
    /// helper; if the attempt fails (false) it is closed, never left as an unfocusable topmost box.</summary>
    public bool EnsureActive()
    {
        if (_closed) return false;
        if (IsActive || _activationAsked) return true;
        _activationAsked = true;
        if (_tryActivate()) return true;
        Dismiss();
        return false;
    }

    private bool ActivateWithTheRepoHelper()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        User32.ActivateWindow(hwnd);                          // the vk0xE8 bridge, as for a client; never WPF's Window.Activate
        // The bridge is asynchronous (its WM_HOTKEY lands a few ms later), so the foreground cannot be judged here. The request
        // counts as the attempt; one look after it has had time to land closes a palette that is still not in front, so it is
        // never left as an unfocusable topmost box.
        var look = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        look.Tick += (_, _) =>
        {
            look.Stop();
            if (_closed) return;
            if (IsActive || User32.GetForegroundWindow() == hwnd) { _input.Focus(); Keyboard.Focus(_input); }
            else Dismiss();
        };
        look.Start();
        return true;
    }

    /// <summary>Closes, once.</summary>
    public void Dismiss()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }

    /// <summary>Runs the chosen row and closes. With no row (nothing matches) nothing happens and the palette stays.</summary>
    public void Accept() => Accept(_list.SelectedIndex);

    private void Accept(int index)
    {
        if (index < 0 || index >= _shown.Count) return;
        var command = _shown[index].Command;
        Dismiss();                                            // closed first, so the client the command brings forward is the one in front
        _run(command);
    }

    /// <summary>The chosen row moves by <paramref name="step"/>, round the ends.</summary>
    public void Move(int step)
    {
        if (_shown.Count == 0) return;
        _list.SelectedIndex = ((_list.SelectedIndex < 0 ? 0 : _list.SelectedIndex) + step % _shown.Count + _shown.Count) % _shown.Count;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Dismiss(); break;
            case Key.Enter: e.Handled = true; Accept(); break;
            case Key.Down: e.Handled = true; Move(1); break;
            case Key.Up: e.Handled = true; Move(-1); break;
        }
        base.OnPreviewKeyDown(e);
    }

    private void Refresh()
    {
        _shown = PaletteMatcher.Match(_input.Text, _entries);
        var rows = new List<(string, string)>();
        _list.Items.Clear();
        for (int i = 0; i < _shown.Count; i++)
        {
            var entry = _shown[i];
            string caption = Caption(entry);
            rows.Add((entry.Name!, caption));
            int index = i;
            var item = new ListBoxItem { Content = Row(entry.Name!, caption), Focusable = false };   // a click runs the row; the keyboard stays in the text box
            item.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; Accept(index); };
            _list.Items.Add(item);
        }
        Rows = rows;
        _list.SelectedIndex = _shown.Count > 0 ? 0 : -1;
        _list.Visibility = _shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _none.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The small grey text beside a row: what it is, and for a label the character it is on.</summary>
    private static string Caption(PaletteEntry entry) => entry.Kind switch
    {
        PaletteKind.Group => LocalizationService.Str("L.Palette.Kind.Group", "Group"),
        PaletteKind.Profile => LocalizationService.Str("L.Palette.Kind.Profile", "Profile"),
        PaletteKind.Label => string.Format(LocalizationService.Str("L.Palette.Kind.Label", "Label of {0}"),
            StreamerMask.Name(StreamerKind.Character, entry.Command.Argument)),
        _ => LocalizationService.Str("L.Palette.Kind.Character", "Character"),
    };

    private UIElement Build()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };

        var field = new Grid();
        _input.SetResourceReference(TextBox.FontSizeProperty, "Fx.Size.Section");
        _input.MaxLength = PaletteMatcher.MaxQueryLength;
        _input.Padding = new Thickness(6, 4, 6, 4);
        _placeholder.Text = LocalizationService.Str("L.Palette.Placeholder", "Type a character, label, group or profile");
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.Margin = new Thickness(11, 0, 6, 0);
        _placeholder.TextTrimming = TextTrimming.CharacterEllipsis;
        _placeholder.SetResourceReference(TextBlock.FontSizeProperty, "Fx.Size.Body");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        field.Children.Add(_input);
        field.Children.Add(_placeholder);
        panel.Children.Add(field);

        _list.Margin = new Thickness(0, 8, 0, 0);
        _list.Focusable = false;
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        panel.Children.Add(_list);

        _none.Text = LocalizationService.Str("L.Palette.None", "Nothing matches");
        _none.Margin = new Thickness(6, 10, 6, 4);
        _none.SetResourceReference(TextBlock.FontSizeProperty, "Fx.Size.Body");
        _none.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        panel.Children.Add(_none);

        var card = new Border { Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "Fx.BgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "Fx.FlyoutBorderBrush");
        card.SetResourceReference(Border.BorderThicknessProperty, "Fx.Line.Card");
        card.SetResourceReference(Border.CornerRadiusProperty, "Fx.FlyoutRadius");
        return card;
    }

    /// <summary>A row: the name on the left, its caption on the right.</summary>
    private static UIElement Row(string name, string caption)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.FontSizeProperty, "Fx.Size.Body");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextBrush");
        var kind = new TextBlock { Text = caption, Margin = new Thickness(16, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
        kind.SetResourceReference(TextBlock.FontSizeProperty, "Fx.Size.Caption");
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
        Grid.SetColumn(kind, 1);
        grid.Children.Add(text);
        grid.Children.Add(kind);
        return grid;
    }

    /// <summary>The work area, in DIPs, of the monitor under the cursor.</summary>
    private static Rect CursorMonitorWorkArea()
    {
        var wa = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea;
        double scale = DpiHelper.GetScaleFactorForPoint(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
        return new Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
    }
}
