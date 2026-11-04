using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>IDEAS #14, the type-to-switch command palette (the Windows CommandPaletteWindow): a text box and at most
/// <see cref="PaletteMatcher.MaxResults"/> rows (running characters, their labels, groups, profiles) in the middle of the
/// screen under the pointer. Typing filters through PaletteMatcher; Up and Down move; Enter runs the chosen row's command and
/// closes; Esc, a click away or the hotkey again closes without running anything. It takes the keyboard while it is open, so
/// what is typed never reaches the game.</summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteEntry> _entries;
    private readonly Action<RemoteCommand> _run;
    private readonly TextBox _input = new() { MaxLength = PaletteMatcher.MaxQueryLength, FontSize = 16 };
    private readonly StackPanel _list = new() { Spacing = 1, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _none = new() { Opacity = 0.6, Margin = new Thickness(6, 10, 6, 4) };
    private IReadOnlyList<PaletteEntry> _shown = Array.Empty<PaletteEntry>();
    private int _chosen;
    private bool _done;

    /// <summary>The rows now shown (tests).</summary>
    public IReadOnlyList<PaletteEntry> Shown => _shown;

    /// <param name="entries">Every row the palette may show (CommandPalette.Entries).</param>
    /// <param name="run">What Enter hands the chosen row's command to (the app's RunCommand), after the palette closed.</param>
    public CommandPaletteWindow(IReadOnlyList<PaletteEntry> entries, Action<RemoteCommand> run)
    {
        _entries = entries;
        _run = run;
        Title = "MultiPreview - " + LocalizationService.Str("L.Palette.Title", "Command palette");
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Background = new SolidColorBrush(Color.Parse("#FF1F1F1F"));

        _input.PlaceholderText = LocalizationService.Str("L.Palette.Placeholder", "Type a character, label, group or profile");
        _none.Text = LocalizationService.Str("L.Palette.None", "Nothing matches");
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(_input);
        panel.Children.Add(_list);
        panel.Children.Add(_none);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#50FFFFFF")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel };

        _input.TextChanged += (_, _) => { _chosen = 0; Filter(); };
        // Tunnel: the keys are seen before the text box uses Up, Down, Enter and Esc for itself.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; Finish(null); break;
                case Key.Enter: e.Handled = true; if (_shown.Count > 0) Finish(_shown[_chosen].Command); break;
                case Key.Down: e.Handled = true; Move(1); break;
                case Key.Up: e.Handled = true; Move(-1); break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => { Place(); Activate(); _input.Focus(); };
        Deactivated += (_, _) => Finish(null);   // a click away
        Closed += (_, _) => _done = true;
        Filter();
    }

    /// <summary>The middle of the screen the pointer is on, a third of the way down.</summary>
    private void Place()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        int width = (int)Math.Round(Bounds.Width * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + area.Height / 3);
    }

    /// <summary>Opens on the screen under <paramref name="pointer"/> (screen pixels), when known.</summary>
    public void ShowAt(PixelPoint? pointer)
    {
        if (pointer is { } p) Position = p;   // only picks the screen: Place centres it once it is open
        Show();
    }

    /// <summary>Closes without running anything (the hotkey again).</summary>
    public void Dismiss() => Finish(null);

    private void Filter()
    {
        _shown = PaletteMatcher.Match(_input.Text, _entries);
        if (_chosen >= _shown.Count) _chosen = 0;
        _list.Children.Clear();
        for (int i = 0; i < _shown.Count; i++) _list.Children.Add(Row(_shown[i], i));
        _list.IsVisible = _shown.Count > 0;
        _none.IsVisible = _shown.Count == 0;
    }

    private void Move(int step)
    {
        if (_shown.Count == 0) return;
        _chosen = ((_chosen + step) % _shown.Count + _shown.Count) % _shown.Count;
        Filter();
    }

    /// <summary>A row: the name (as streamer mode shows it; CommandPalette already masked it) and its caption.</summary>
    private Control Row(PaletteEntry entry, int index)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var caption = new TextBlock { Text = Caption(entry), Opacity = 0.55, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(caption, 1);
        grid.Children.Add(caption);
        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(8, 5),
            CornerRadius = new CornerRadius(4),
            Background = index == _chosen ? new SolidColorBrush(Color.Parse("#FF2E4A6B")) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) Finish(entry.Command); };
        return row;
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

    /// <summary>Closes, once; then runs the command when there is one.</summary>
    private void Finish(RemoteCommand? command)
    {
        if (_done) return;
        _done = true;
        Close();
        if (command != null) _run(command);
    }
}
