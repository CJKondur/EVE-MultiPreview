using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;
using SharedHost = EveMultiPreview.SettingsUi.ISettingsHost;

namespace EveMultiPreview.Linux;

/// <summary>The settings window drawn from the Windows page definitions (SettingsRegistry): Home and Simple/Advanced,
/// the navigation groups, pages, sections and cards, search and the help pane, in the Windows app's strings. The state
/// is the Windows view-models' (ShellVM, PageVM, SettingVM…), so a card here stores, records and applies a change the way
/// the Windows card does. Drawn in code, without bindings: each card follows its view-model's PropertyChanged, and a page is
/// built anew each time it shows. An editor Linux has (LinuxEditors) is the hand-made Linux one; an editor it lacks shows a
/// card that says so. An action or a change a Linux stand-in refuses (PlatformNotSupportedException) is said in its card and
/// as a toast; the window stays open.</summary>
public sealed class SharedSettingsWindow : Window, SharedHost
{
    private const double HelpWidth = 320;
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4C9BE8"));
    private static readonly IBrush CardBack = new SolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IBrush CardEdge = new SolidColorBrush(Color.Parse("#22FFFFFF"));
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#334C9BE8"));

    private readonly ISettingsHost _app;
    private readonly SettingsContext _ctx;
    private readonly SettingsPages _editors;
    private readonly ShellVM _vm;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    /// <summary>Unsubscribes the page shown last from its view-models (a page is built anew each time it shows).</summary>
    private readonly List<Action> _detach = new();
    private readonly Dictionary<string, Border> _cards = new();
    private readonly Dictionary<string, Control> _topics = new();
    private bool _renderQueued;
    private IReadOnlyList<SearchEntry>? _index;
    private string? _indexKey;
    private string? _helpItem;
    private string? _shownPageId;
    private Border? _highlighted;
    private readonly TextBlock _helpTitle = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly Button _helpClose = new() { Content = "×", Padding = new Thickness(8, 2) };

    private readonly StackPanel _nav = new() { Spacing = 1 };
    private readonly StackPanel _navBottom = new() { Spacing = 1 };
    private readonly StackPanel _header = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _sections = new() { Spacing = 4, Margin = new Thickness(0, 0, 16, 24) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly StackPanel _helpBody = new() { Spacing = 0, Margin = new Thickness(0, 0, 8, 24) };
    private readonly ScrollViewer _helpScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Border _help = new() { Width = HelpWidth, BorderBrush = CardEdge, BorderThickness = new Thickness(1, 0, 0, 0) };
    private readonly TextBlock _toast = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes.LightGreen };
    private readonly Border _saveBar = new() { IsVisible = false, Background = new SolidColorBrush(Color.Parse("#40CD5C5C")), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _search = new();
    private readonly ListBox _results = new() { IsVisible = false, MaxHeight = 360 };

    public SharedSettingsWindow(ISettingsHost app, string? pageId = null)
    {
        _app = app;
        RequestedThemeVariant = ThemeVariant.Dark;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Icon.ico"))
            if (icon != null) Icon = new WindowIcon(icon);

        var service = app.Settings;
        // The Windows shell's applier: a change waits a second, then the file is saved and the app is told, as Windows'
        // App does on SettingsApplied (keys bound again, alert profiles and log folders applied, the tray rebuilt). The
        // Linux thumbnails read their settings at every tick, so nothing is re-applied to them.
        var applier = new SettingsApplier(service.Save, () => { }, () => { }, () => { }, new DispatcherDebounceTimer(TimeSpan.FromSeconds(1)));
        applier.Applied += () => { app.HotkeysChanged(); app.AlertsChanged(); app.TrayChanged(); };
        var services = new AppServices
        {
            OpenAlertHistory = app.OpenAlertHistory,
            Snoozes = app.Snoozes,
            Speaker = app.Speaker,
            VoiceAnswered = app.VoiceAnswered,
            TestPush = app.TestPush,
            PushStatus = () => app.PushStatus,
        };
        _ctx = new SettingsContext(service, applier, services, this);
        _editors = new SettingsPages(app, this, () => QueueRender(), Reload);
        RestoreSize();

        _vm = new ShellVM(_ctx, SettingsRegistry.Pages, a => Dispatcher.UIThread.Post(a), SettingsRegistry.Dynamic);
        _vm.PropertyChanged += OnShellChanged;
        _vm.Reloaded += () => { _index = null; QueueRender(); };
        if (pageId != null) _vm.Navigate(pageId);

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.IsVisible = false; };
        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); ClearHighlight(); };
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new RelayCommand(_ => { _search.Focus(); _search.SelectAll(); }) });

        Content = Layout();
        Render();

        Closing += (_, _) => { SaveSize(); _vm.OnClosing(); };
        Closed += (_, _) =>
        {
            _toastTimer.Stop();
            _highlightTimer.Stop();
            Detach();
            _vm.PropertyChanged -= OnShellChanged;
            _vm.Dispose();
        };
    }

    private AppSettings S => _ctx.S;
    private static string Str(string key) => SettingsContext.Str(key);
    /// <summary>The text of a key, or null when the catalog has none (a card without a description).</summary>
    private static string? Optional(string? key) => key != null && LocalizationService.Str(key, "") is { Length: > 0 } text ? text : null;

    /// <summary>Shows a page by its id (the tray, a thumbnail's menu, EVEMP_OPEN_SETTINGS). An Advanced page leaves Simple mode.</summary>
    public void ShowPage(string pageId) => Navigate(pageId);

    // ── The window's parts ──

    private Control Layout()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*,Auto") };

        _search.PlaceholderText = Str("L.S.Search.Placeholder");
        _search.Margin = new Thickness(0, 0, 0, 4);
        _search.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Search(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _search.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Enter) { OpenResult(_results.SelectedItem as ListBoxItem ?? _results.Items.OfType<ListBoxItem>().FirstOrDefault()); e.Handled = true; }
            else if (e.Key == Key.Down && _results.ItemCount > 0) { _results.SelectedIndex = 0; (_results.ContainerFromIndex(0) as Control)?.Focus(); e.Handled = true; }
        };
        _results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenResult(_results.SelectedItem as ListBoxItem); e.Handled = true; }
            else if (e.Key == Key.Escape) { _results.IsVisible = false; _search.Focus(); e.Handled = true; }
        };

        var left = new DockPanel { Margin = new Thickness(10, 12, 6, 10) };
        var top = new StackPanel();
        top.Children.Add(_search);
        top.Children.Add(_results);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        DockPanel.SetDock(_navBottom, Dock.Bottom);
        left.Children.Add(_navBottom);
        left.Children.Add(new ScrollViewer { Content = _nav, Margin = new Thickness(0, 6), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        root.Children.Add(left);

        var middle = new DockPanel { Margin = new Thickness(18, 12, 0, 8) };
        DockPanel.SetDock(_header, Dock.Top);
        middle.Children.Add(_header);
        DockPanel.SetDock(_saveBar, Dock.Top);
        middle.Children.Add(_saveBar);
        DockPanel.SetDock(_toast, Dock.Bottom);
        middle.Children.Add(_toast);
        _scroll.Content = _sections;
        middle.Children.Add(_scroll);
        Grid.SetColumn(middle, 1);
        root.Children.Add(middle);

        _helpScroll.Content = _helpBody;
        var helpTop = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _helpClose.Click += (_, _) => _vm.HelpOpen = false;
        DockPanel.SetDock(_helpClose, Dock.Right);
        helpTop.Children.Add(_helpClose);
        helpTop.Children.Add(_helpTitle);
        var helpColumn = new DockPanel { Margin = new Thickness(16, 16, 8, 0) };
        DockPanel.SetDock(helpTop, Dock.Top);
        helpColumn.Children.Add(helpTop);
        helpColumn.Children.Add(_helpScroll);
        _help.Child = helpColumn;
        Grid.SetColumn(_help, 2);
        root.Children.Add(_help);
        return root;
    }

    // ── Following the shell ──

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellVM.SaveErrorKey) or nameof(ShellVM.SaveErrorLine) or nameof(ShellVM.HasSaveErrorLine)
                or nameof(ShellVM.HasSaveError) or nameof(ShellVM.CanRetrySave):
                SaveBar();
                break;
            case nameof(ShellVM.HelpOpen):
                Nav();
                Help();
                break;
            default:   // "" (a page shown, Simple/Advanced, a reload) or anything else: draw the window again
                QueueRender();
                break;
        }
    }

    /// <summary>Draws the window again once the current event is done: a control is never taken out of the window while
    /// its own click or change runs.</summary>
    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() => { _renderQueued = false; Render(); });
    }

    private void Render()
    {
        Title = Str("L.S.Shell.Title");
        _search.PlaceholderText = Str("L.S.Search.Placeholder");
        Nav();
        Page();
        Help();
        SaveBar();
    }

    // ── Navigation ──

    private void Nav()
    {
        _nav.Children.Clear();
        _navBottom.Children.Clear();
        var current = _vm.Current?.Page;
        if (_vm.Home is { } home) _nav.Children.Add(NavButton(Str("L.S.Nav.Home"), current == home, bold: true, () => Navigate(home.Id)));
        if (_vm.IsSimple) _nav.Children.Add(NavButton(Str("L.S.Nav.AllSettings"), false, bold: true, _vm.AllSettings));
        foreach (var group in _vm.VisibleGroups) AddGroup(_nav, group);
        if (_vm.AppGroup is { } app) AddGroup(_navBottom, app);

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(6, 10, 0, 4) };
        var simple = new RadioButton { Content = new TextBlock { Text = Str("L.S.Shell.Simple") }, GroupName = "mode", IsChecked = _vm.IsSimple };
        var all = new RadioButton { Content = new TextBlock { Text = Str("L.S.Nav.AllSettings") }, GroupName = "mode", IsChecked = !_vm.IsSimple };
        simple.IsCheckedChanged += (_, _) => { if (simple.IsChecked == true && !_vm.IsSimple) _vm.IsSimple = true; };
        all.IsCheckedChanged += (_, _) => { if (all.IsChecked == true && _vm.IsSimple) _vm.AllSettings(); };
        modes.Children.Add(simple);
        modes.Children.Add(all);
        _navBottom.Children.Add(modes);

        var help = new ToggleButton { Content = Str("L.S.Shell.Help"), IsChecked = _vm.HelpOpen, Margin = new Thickness(6, 4, 0, 0) };
        help.IsCheckedChanged += (_, _) => { if ((help.IsChecked == true) != _vm.HelpOpen) _vm.HelpOpen = help.IsChecked == true; };
        _navBottom.Children.Add(help);
    }

    private void AddGroup(StackPanel into, NavGroupVM group)
    {
        into.Children.Add(NavButton(Str(group.TitleKey), group.IsCurrent && !group.IsExpanded, bold: true, () => Navigate(group.Pages[0].Page.Id)));
        if (!group.IsExpanded) return;
        foreach (var page in group.Pages)
        {
            var b = NavButton(Str(page.TitleKey), page.IsSelected, bold: false, () => Navigate(page.Page.Id));
            b.Margin = new Thickness(14, 0, 0, 0);
            into.Children.Add(b);
        }
    }

    private static Button NavButton(string text, bool selected, bool bold, Action open)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 5),
            Background = selected ? Selected : Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        b.Click += (_, _) => open();
        return b;
    }

    // ── Search ──

    private void Search()
    {
        var hits = SearchIndex.Query(SearchEntries(), _search.Text ?? "");
        _results.Items.Clear();
        foreach (var hit in hits)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = hit.Text, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = hit.Path, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            var item = new ListBoxItem { Content = text, Tag = hit, Padding = new Thickness(6, 3) };
            item.Tapped += (_, _) => OpenResult(item);
            _results.Items.Add(item);
        }
        _results.IsVisible = hits.Count > 0;
    }

    private void OpenResult(ListBoxItem? item)
    {
        if (item?.Tag is not SearchEntry entry) return;
        _results.IsVisible = false;
        Navigate(entry.PageId, entry.ItemId);
    }

    /// <summary>The Windows shell's index (SettingsShell.SearchEntries): the pages, the help, the known characters and the
    /// alert events, built again after a reload and whenever the known characters change. The player's own alert rules are
    /// left out: Linux has no editor for them yet.</summary>
    private IReadOnlyList<SearchEntry> SearchEntries()
    {
        var names = CharacterDirectory.Known(S, _app.RunningCharacters());
        var key = LocalizationService.CurrentLanguage + "\n" + string.Join("\n", names);
        if (_index == null || key != _indexKey)
        {
            _indexKey = key;
            string Resolve(string k) => LocalizationService.Str(k, "");
            _index = SearchIndex.Build(SettingsRegistry.Pages, HelpTopics.All, Resolve, LocalizationService.EnglishText,
                extra: CharacterStores.SearchEntries(names, Resolve).Concat(EventEntries(Resolve)));
        }
        return _index;
    }

    // Copy of SettingsUi/Pages/Alerts/AlertEvents.Wpf.cs:SearchEntries (that file is WPF) — keep in sync.
    private static IEnumerable<SearchEntry> EventEntries(Func<string, string> resolve)
    {
        var path = resolve("L.S.Nav.Alerts") + " › " + resolve("L.S.Page." + AlertsPages.EventsId);
        return AlertEvents.Listed.Select(e => new SearchEntry(SearchKind.Event,
            resolve(e.LabelKey) is { Length: > 0 } text ? text : e.Key, LocalizationService.EnglishText(e.LabelKey),
            path, AlertsPages.EventsId, AlertsPages.EventTableId)).ToList();
    }

    // ── The page ──

    private void Detach()
    {
        foreach (var undo in _detach) undo();
        _detach.Clear();
    }

    private void Watch(INotifyPropertyChanged vm, Action<string?> changed)
    {
        PropertyChangedEventHandler handler = (_, e) => changed(e.PropertyName);
        vm.PropertyChanged += handler;
        _detach.Add(() => vm.PropertyChanged -= handler);
    }

    private void Page()
    {
        Detach();
        _cards.Clear();
        _highlighted = null;
        _editors.ForgetClashLines();
        _header.Children.Clear();
        _sections.Children.Clear();
        var page = _vm.Current;
        if (page == null) return;

        if (_vm.GroupTitleKey is { } groupKey && _vm.BreadcrumbTargetId is { } target)
        {
            var crumb = new Button { Content = Str(groupKey) + "  ›", Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Opacity = 0.75 };
            crumb.Click += (_, _) => Navigate(target);
            _header.Children.Add(crumb);
        }
        _header.Children.Add(new TextBlock { Text = page.Page.Title ?? Str(page.Page.TitleKey), FontSize = 24, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (Optional(page.Page.DescKey) is { } desc) _header.Children.Add(new TextBlock { Text = desc, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        foreach (var section in page.Sections) _sections.Children.Add(Section(section));
        // A page opens at its top (owner, 2026-10-09); the same page drawn again (an editor changed it) keeps its place.
        if (page.Page.Id != _shownPageId) _scroll.Offset = default;
        _shownPageId = page.Page.Id;
        // The item search or a link opened the page for: Show marked it before this page was drawn.
        if (_vm.HighlightedItem is { } hit) Highlight(hit.Id, true);
    }

    private Control Section(SectionVM section)
    {
        var panel = new StackPanel { Spacing = 0, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = Str(section.TitleKey), FontSize = 15, FontWeight = FontWeight.SemiBold, Opacity = 0.85, Margin = new Thickness(0, 6, 0, 4) });
        foreach (var item in section.Items) panel.Children.Add(Card(item));
        panel.IsVisible = Safe(() => section.IsVisible, true);
        Watch(section, name => { if (name is "" or null or nameof(SectionVM.IsVisible)) panel.IsVisible = Safe(() => section.IsVisible, true); });
        return panel;
    }

    /// <summary>One card. A card that cannot be drawn says so instead of taking the window with it.</summary>
    private Control Card(ItemVM item)
    {
        Control inner;
        try
        {
            inner = item switch
            {
                SettingVM s => SettingCard(s),
                ActionRowVM a => ActionCard(a),
                InfoVM i => InfoCard(i),
                EditorVM e => EditorCard(e),
                _ => NotYetCard(null, item.Id),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Settings] card {item.Id}: {ex.GetType().Name}");
            inner = NotYetCard(item.LabelKey, item.Id);
        }
        var card = new Border
        {
            Background = CardBack, BorderBrush = CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8), Margin = new Thickness(0, 3), Child = inner,
        };
        _cards[item.Id] = card;
        card.PointerEntered += (_, _) => FollowHelp(item.Id);
        card.GotFocus += (_, _) => FollowHelp(item.Id);
        card.IsVisible = Safe(() => item.IsVisible, true);
        Watch(item, name => { if (name is "" or null or nameof(ItemVM.IsVisible)) card.IsVisible = Safe(() => item.IsVisible, true); });
        return card;
    }

    /// <summary>The label (and description) on the left, the control on the right; <paramref name="below"/> takes the whole
    /// width under the label (a box of several lines).</summary>
    private static Control Labelled(string label, string? desc, Control input, TextBlock? error = null, bool below = false)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (desc != null) text.Children.Add(new TextBlock { Text = desc, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        if (below)
        {
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(text);
            stack.Children.Add(input);
            if (error != null) stack.Children.Add(error);
            return stack;
        }
        if (error != null) text.Children.Add(error);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        input.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return grid;
    }

    private static TextBlock ErrorLine() => new() { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private Control SettingCard(SettingVM vm)
    {
        var error = ErrorLine();
        bool reading = false;   // the controls are being set from the view-model: their change events are not the player's
        Action read;
        Control input;
        bool below = false;

        void Commit(Action change)
        {
            if (reading) return;
            Run(change, error);
        }

        switch (vm.Kind)
        {
            case SettingKind.Toggle:
            {
                var box = new CheckBox();
                box.IsCheckedChanged += (_, _) => Commit(() => vm.IsOn = box.IsChecked == true);
                read = () => box.IsChecked = vm.IsOn;
                input = box;
                break;
            }
            case SettingKind.Choice:
            {
                var box = new ComboBox { MinWidth = 200, MaxWidth = 360 };
                foreach (var option in vm.Options) box.Items.Add(new TextBlock { Text = OptionText(option) });
                box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Commit(() => vm.Selected = vm.Options[box.SelectedIndex]); };
                read = () => box.SelectedIndex = vm.Selected is { } chosen ? IndexOf(vm.Options, chosen) : -1;
                input = box;
                break;
            }
            case SettingKind.Segmented:
            {
                var row = new WrapPanel { Orientation = Orientation.Horizontal };
                var group = "seg:" + vm.Id;
                var buttons = vm.Options.Select(o => new RadioButton { Content = new TextBlock { Text = OptionText(o) }, GroupName = group, Margin = new Thickness(0, 0, 12, 0) }).ToList();
                for (int i = 0; i < buttons.Count; i++)
                {
                    var (button, option) = (buttons[i], vm.Options[i]);
                    button.IsCheckedChanged += (_, _) => { if (button.IsChecked == true) Commit(() => vm.Selected = option); };
                    row.Children.Add(button);
                }
                read = () => { var chosen = vm.Selected; for (int i = 0; i < buttons.Count; i++) buttons[i].IsChecked = ReferenceEquals(vm.Options[i], chosen); };
                input = row;
                break;
            }
            case SettingKind.Slider:
            {
                var slider = new Slider { Minimum = vm.Min, Maximum = vm.Max, Width = 220, SmallChange = vm.Step, TickFrequency = vm.Step, IsSnapToTickEnabled = vm.Step > 0 };
                var shown = new TextBlock { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                slider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
                    shown.Text = WithUnit(slider.Value, vm.UnitKey);
                    Commit(() => vm.Number = slider.Value);
                };
                read = () => { slider.Value = Math.Clamp(vm.Number, vm.Min, vm.Max); shown.Text = WithUnit(vm.Number, vm.UnitKey); };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(slider);
                row.Children.Add(shown);
                input = row;
                break;
            }
            case SettingKind.Number:
            case SettingKind.Text:
            {
                var box = new TextBox { MinWidth = vm.Kind == SettingKind.Number ? 110 : 260, MaxWidth = 480, AcceptsReturn = vm.IsMultiLine, TextWrapping = vm.IsMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap };
                if (vm.IsMultiLine) { box.MinHeight = 80; box.MaxWidth = double.PositiveInfinity; below = true; }
                void Store() { if ((box.Text ?? "") != vm.Text) Commit(() => vm.Text = box.Text ?? ""); }
                box.LostFocus += (_, _) => Store();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter && !vm.IsMultiLine) { Store(); e.Handled = true; } };
                read = () => box.Text = vm.Text;
                input = vm.UnitKey != null ? Beside(box, Str(vm.UnitKey)) : box;
                break;
            }
            case SettingKind.Folder:
            case SettingKind.File:
            {
                var box = new TextBox { Width = 300 };
                void Store() { if ((box.Text ?? "") != vm.Text) Commit(() => vm.Text = (box.Text ?? "").Trim()); }
                box.LostFocus += (_, _) => Store();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Store(); e.Handled = true; } };
                var browse = new Button { Content = Ui.NoAccess(Str("L.S.Shell.Browse")) };
                browse.Click += (_, _) => Commit(() => vm.BrowseCommand.Execute(null));
                read = () => box.Text = vm.Text;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(box);
                row.Children.Add(browse);
                input = row;
                break;
            }
            case SettingKind.Color:
            {
                var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
                var box = new TextBox { Width = 120, MaxLength = 32 };
                void Paint()
                {
                    swatch.Background = Brushes.Transparent;
                    if (!ThumbnailFields.IsPaintable(box.Text)) return;
                    try { swatch.Background = new SolidColorBrush(ToAvalonia(AppSettings.ParseColor(box.Text!, System.Windows.Media.Colors.Transparent))); }
                    catch (Exception) { }   // ParseColor throws on rgb(300,0,0) (Setting.IsColor catches it the same way)
                }
                void Store() { if ((box.Text ?? "") != vm.Text) Commit(() => vm.Text = box.Text ?? ""); }
                box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Paint(); };
                box.LostFocus += (_, _) => Store();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Store(); e.Handled = true; } };
                read = () => { box.Text = vm.Text; Paint(); };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(swatch);
                row.Children.Add(box);
                input = row;
                break;
            }
            case SettingKind.Hotkey:
            {
                var box = new HotkeyBox(vm.D.Get(S) as string ?? "");
                var clash = new TextBlock { Foreground = Brushes.Orange, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, IsVisible = false };
                box.Capturing += _app.CaptureHotkey;
                box.Changed += key => Commit(() => vm.Commit(key));
                var clear = new Button { Content = "×", Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(clear, Str("L.S.Hotkey.Clear"));
                clear.Click += (_, _) => Commit(() => vm.Commit(""));
                read = () =>
                {
                    var key = vm.D.Get(S) as string ?? "";
                    box.Value = key;
                    var others = key.Length == 0 ? new List<HotkeyUse>() : HotkeyUses.Clashes(S, SettingsRegistry.Settings, key, HotkeyUses.IdOf(vm.D));
                    clash.IsVisible = others.Count > 0;
                    clash.Text = others.Count == 0 ? "" : Str("L.S.Hotkey.UsedBy") + " " + string.Join(", ", others.Select(o => o.Label)) + " " + Str("L.S.Hotkey.UsedByNote");
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(box);
                row.Children.Add(clear);
                var field = new StackPanel { Spacing = 2 };
                field.Children.Add(row);
                field.Children.Add(clash);
                input = field;
                break;
            }
            default:
                return NotYetCard(vm.LabelKey, vm.Id);
        }

        void Read()
        {
            reading = true;
            try
            {
                read();
                error.Text = vm.Error ?? "";
                error.IsVisible = vm.HasError;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine($"[Settings] read {vm.Id}: {ex.GetType().Name}"); }
            finally { reading = false; }
        }
        Read();
        Watch(vm, name => { if (name is "" or null) Read(); else if (name == nameof(ItemVM.IsHighlighted)) Highlight(vm.Id, vm.IsHighlighted); });
        return Labelled(Str(vm.LabelKey), Optional(vm.DescKey), input, error, below);
    }

    private Control ActionCard(ActionRowVM vm)
    {
        var error = ErrorLine();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in vm.Buttons)
        {
            var b = new Button { Content = new TextBlock { Text = Ui.NoAccess(Str(action.LabelKey)) }, Margin = new Thickness(6, 2, 0, 2) };
            if (action.IsDestructive) b.Foreground = Brushes.IndianRed;
            b.Click += (_, _) => Run(action.Run, error, AccessKeys.Spoken(Str(action.LabelKey)));
            buttons.Children.Add(b);
        }
        Watch(vm, name => { if (name == nameof(ItemVM.IsHighlighted)) Highlight(vm.Id, vm.IsHighlighted); });
        return Labelled(Str(vm.LabelKey), Optional(vm.DescKey), buttons, error);
    }

    private Control InfoCard(InfoVM vm)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = vm.Severity switch { InfoSeverity.Error => Brushes.IndianRed, InfoSeverity.Warning => Brushes.Orange, _ => Brushes.LightSkyBlue },
        };
        void Read() => text.Text = Safe(() => vm.Text, Str(vm.Def.TextKey));
        Read();
        Watch(vm, name => { if (name is "" or null) Read(); else if (name == nameof(ItemVM.IsHighlighted)) Highlight(vm.Id, vm.IsHighlighted); });
        return text;
    }

    private Control EditorCard(EditorVM vm)
    {
        Watch(vm, name => { if (name == nameof(ItemVM.IsHighlighted)) Highlight(vm.Id, vm.IsHighlighted); });
        Control? editor;
        try { editor = LinuxEditors.TryCreate(vm.Def, _editors, _ctx); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Settings] editor {vm.Id}: {ex.GetType().Name}");
            editor = null;
        }
        vm.Element = editor;
        return editor ?? NotYetCard(vm.Def.LabelKey, vm.Id);
    }

    /// <summary>A card for something Linux cannot show yet: its label, and "Not available on Linux yet: ‹id›".</summary>
    private static Control NotYetCard(string? labelKey, string id)
    {
        var panel = new StackPanel { Spacing = 2 };
        if (Optional(labelKey) is { } label) panel.Children.Add(new TextBlock { Text = AccessKeys.Spoken(label), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(LocalizationService.Str("L.Linux.NotYet", "Not available on Linux yet: {0}"), id),
            Foreground = Brushes.Orange, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    /// <summary>Runs a change or an action. One a Linux stand-in refuses says so under its card and as a toast; any other
    /// failure is said too (by its kind, never its message) and written to the console, and the window stays open.</summary>
    private void Run(Action run, TextBlock error, string? what = null)
    {
        error.IsVisible = false;   // a card's own error (a value out of range) is shown again by the card as it re-reads
        try
        {
            run();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine("[Settings] " + ex.Message);
            Say(error, string.Format(LocalizationService.Str("L.Linux.NotYet", "Not available on Linux yet: {0}"), what ?? ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[Settings] {what}: {ex.GetType().Name}: {ex.Message}");
            Say(error, ex.GetType().Name);
        }
    }

    private void Say(TextBlock error, string text)
    {
        error.Text = text;
        error.IsVisible = true;
        Toast(text);
    }

    private static T Safe<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine($"[Settings] {ex.GetType().Name}: {ex.Message}"); return fallback; }
    }

    private static string OptionText(ChoiceOption o) => o.Literal ?? Ui.NoAccess(Str(o.LabelKey));

    private static int IndexOf(IReadOnlyList<ChoiceOption> options, ChoiceOption chosen)
    {
        for (int i = 0; i < options.Count; i++) if (ReferenceEquals(options[i], chosen)) return i;
        return -1;
    }

    private static string WithUnit(double value, string? unitKey)
    {
        var number = value.ToString("0.##", CultureInfo.CurrentCulture);
        if (unitKey == null) return number;
        var unit = Str(unitKey);
        return number + (unit == "%" ? "" : " ") + unit;   // as SettingChanges shows a value
    }

    private static Control Beside(Control box, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = unit, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Color ToAvalonia(System.Windows.Media.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    // ── Highlight (search, Navigate with an item) ──

    private void Highlight(string id, bool on)
    {
        if (!on || !_cards.TryGetValue(id, out var card)) return;
        if (_highlighted != null) { _highlighted.BorderBrush = CardEdge; _highlighted.BorderThickness = new Thickness(1); }
        _highlighted = card;
        card.BorderBrush = Accent;
        card.BorderThickness = new Thickness(2);
        Dispatcher.UIThread.Post(() => card.BringIntoView(), DispatcherPriority.Background);
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void ClearHighlight()
    {
        if (_highlighted != null) { _highlighted.BorderBrush = CardEdge; _highlighted.BorderThickness = new Thickness(1); }
        _highlighted = null;
        _vm.ClearHighlight();
    }

    // ── Help ──

    /// <summary>The Windows HelpPane: the page's introduction and its topics, in the help markup (paragraphs, bullets, bold).</summary>
    private void Help()
    {
        _help.IsVisible = _vm.HelpOpen;
        if (!_vm.HelpOpen) return;
        var pageId = _vm.Current?.Page.HelpId;
        _helpBody.Children.Clear();
        _topics.Clear();
        _helpItem = null;
        _helpTitle.Text = Str("L.S.Help.Title");
        ToolTip.SetTip(_helpClose, Str("L.S.Help.Close"));
        if (pageId == null) return;
        Blocks(LocalizationService.Str(HelpTopics.IntroKey(pageId), ""), _helpBody);
        foreach (var topic in HelpTopics.ForPage(pageId))
        {
            var title = LocalizationService.Str(topic.TitleKey, "");
            var body = LocalizationService.Str(topic.BodyKey, "");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) continue;   // spec §8.5: hide, don't show blank
            var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            Blocks(body, panel);
            _helpBody.Children.Add(panel);
            foreach (var id in topic.SettingIds) _topics[id] = panel;
        }
        _helpScroll.Offset = default;
    }

    private static void Blocks(string text, Panel into)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var block in HelpMarkup.Parse(text))
        {
            var inlines = new InlineCollection();
            if (block is HelpBullet) inlines.Add(new Run("•  "));
            foreach (var run in block.Runs) inlines.Add(new Run(run.Text) { FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal });
            into.Children.Add(new TextBlock { Inlines = inlines, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(block is HelpBullet ? 12 : 0, 6, 0, 0) });
        }
    }

    /// <summary>While help is open, the card under the mouse or the keyboard scrolls the help to the topic that explains it.</summary>
    private void FollowHelp(string itemId)
    {
        if (!_vm.HelpOpen || itemId == _helpItem || !_topics.TryGetValue(itemId, out var topic)) return;
        _helpItem = itemId;
        if (topic.TranslatePoint(new Point(0, 0), _helpBody) is { } at) _helpScroll.Offset = new Vector(0, at.Y);
    }

    // ── Save errors, toast ──

    private void SaveBar()
    {
        _saveBar.IsVisible = _vm.HasSaveError;
        if (!_vm.HasSaveError) return;
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Str(_vm.SaveErrorKey!), TextWrapping = TextWrapping.Wrap });
        if (_vm.SaveErrorLine is { } line) panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        if (_vm.CanRetrySave)
        {
            var retry = new Button { Content = Str("L.S.Shell.Retry"), HorizontalAlignment = HorizontalAlignment.Left };
            retry.Click += (_, _) => _vm.RetrySave();
            panel.Children.Add(retry);
        }
        _saveBar.Child = panel;
    }

    // ── Size ──

    /// <summary>1240×800 unless a settings window was resized before (the Windows shell's rule).</summary>
    private void RestoreSize()
    {
        var defaults = new AppSettings();
        bool resized = S.SettingsWindowWidth != defaults.SettingsWindowWidth || S.SettingsWindowHeight != defaults.SettingsWindowHeight;
        Width = resized ? Math.Max(MinWidth, S.SettingsWindowWidth) : 1240;
        Height = resized ? Math.Max(MinHeight, S.SettingsWindowHeight) : 800;
    }

    private void SaveSize()
    {
        if (WindowState != WindowState.Normal || Width < MinWidth || Height < MinHeight) return;
        S.SettingsWindowWidth = (int)Width;
        S.SettingsWindowHeight = (int)Height;
    }

    // ── The shared ISettingsHost ──

    public void Navigate(string pageId, string? itemId = null) => _vm.Navigate(pageId, itemId);

    public void Toast(string text)
    {
        _toast.Text = text;
        _toast.IsVisible = text.Length > 0;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public bool Confirm(string title, string body, SafeAnswer safe = SafeAnswer.Cancel) =>
        Ask(title, body, new[] { Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel") }, safe == SafeAnswer.First ? 0 : 1) == 0;

    public bool? ConfirmChoice(string title, string body, string yesLabel, string noLabel, SafeAnswer safe = SafeAnswer.Cancel) =>
        Ask(title, body, new[] { yesLabel, noLabel, Str("L.S.Shell.Cancel") }, safe switch { SafeAnswer.First => 0, SafeAnswer.Second => 1, _ => 2 }) switch
        {
            0 => true,
            1 => false,
            _ => null,
        };

    public string? AskText(string title, string prompt, string initial)
    {
        var box = new TextBox { Text = initial };
        var answer = Ask(title, prompt, new[] { Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel") }, 0, box);
        return answer == 0 && !string.IsNullOrWhiteSpace(box.Text) ? box.Text : null;
    }

    public string? PickCharacter(string title, IReadOnlyList<string> names)
    {
        var box = new AutoCompleteBox { ItemsSource = names.ToList(), FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinWidth = 260 };
        var answer = Ask(title, "", new[] { Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel") }, 0, box);
        return answer == 0 && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    public string? PickFolder(string? initial) => Pick(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, SuggestedStartLocation = await StartAt(initial) });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    });

    public string? PickFile(string? initial, string filter) => Pick(async () =>
    {
        var start = initial is { Length: > 0 } ? Path.GetDirectoryName(initial) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = Filter(filter), SuggestedStartLocation = await StartAt(start) });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    });

    public string? PickSaveFile(string defaultName, string filter) => Pick(async () =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = defaultName, FileTypeChoices = Filter(filter) });
        return file?.TryGetLocalPath();
    });

    private bool _checking;

    /// <summary>About › Check for updates (AboutActions.CheckNow): the Linux updater asks GitHub and offers the update
    /// dialog itself; its answer (up to date, or why the check failed) is the toast.</summary>
    internal async void CheckForUpdates()
    {
        if (_checking) return;
        _checking = true;
        Toast(Str("L.S.About.Checking"));
        try { Toast(await _app.CheckForUpdates(quiet: false) ?? ""); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Toast(string.Format(Str("L.S.About.CheckFailed"), ex.GetType().Name)); }
        finally { _checking = false; }
    }

    /// <summary>No colour picker on Linux yet: the colour cards take a typed colour.</summary>
    public string? PickColor(string? initial) => null;

    /// <summary>The hotkey cards capture in their own box (HotkeyBox); nothing on Linux opens the Windows capture dialog.</summary>
    public string? CaptureHotkey(string settingId, string? current) => null;

    /// <summary>Everything again from the settings, in the current language (a language change, a reset, an import).</summary>
    public void Reload()
    {
        _index = null;
        _vm.Reload();
        _app.TrayChanged();
        QueueRender();
    }

    public System.Windows.Window? DialogOwner => null;

    // ── Questions and pickers, answered before the call returns (the shared ISettingsHost asks synchronously) ──

    /// <summary>Waits for <paramref name="task"/> while the window keeps working (a nested dispatcher frame, as WPF's ShowDialog).</summary>
    private static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    private static string? Pick(Func<Task<string?>> pick)
    {
        try { return Wait(pick()); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { return null; }   // no file chooser on this desktop
    }

    private async Task<IStorageFolder?> StartAt(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        try { return await StorageProvider.TryGetFolderFromPathAsync(folder); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>A Windows filter ("WAV files|*.wav|All files|*.*") as the picker's file types.</summary>
    private static List<FilePickerFileType> Filter(string filter)
    {
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        return types;
    }

    /// <summary>A question in a small window over this one: the buttons in order, <paramref name="safe"/> the default (Enter)
    /// and the answer of Esc and of closing. Returns the button's index.</summary>
    private int Ask(string title, string body, IReadOnlyList<string> answers, int safe, Control? extra = null)
    {
        var dialog = new Window
        {
            Title = AccessKeys.Spoken(title), Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            RequestedThemeVariant = ThemeVariant.Dark, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        int result = safe;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        Button? safeButton = null;
        for (int i = 0; i < answers.Count; i++)
        {
            int index = i;
            var b = new Button { Content = new TextBlock { Text = Ui.NoAccess(answers[i]) }, IsDefault = i == safe, IsCancel = i == safe };
            b.Click += (_, _) => { result = index; dialog.Close(); };
            if (i == safe) safeButton = b;
            buttons.Children.Add(b);
        }
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = AccessKeys.Spoken(title), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (body.Length > 0) panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        if (extra != null) panel.Children.Add(extra);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Opened += (_, _) => { if (extra != null) extra.Focus(); else safeButton?.Focus(); };
        Wait(dialog.ShowDialog<object?>(this));
        return result;
    }
}
