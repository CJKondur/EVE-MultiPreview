using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

    private readonly ISettingsHost _app;
    private readonly SettingsContext _ctx;
    private readonly SettingsEditors _editors;
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
    private readonly Button _helpClose = new() { Content = "×", Padding = new Thickness(8, 2), Classes = { "quiet" } };

    private readonly StackPanel _nav = new() { Spacing = 1 };
    private readonly StackPanel _navBottom = new() { Spacing = 1 };
    private readonly StackPanel _header = new() { Spacing = 2, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _sections = new() { Spacing = 4, Margin = new Thickness(0, 0, 16, 24) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly StackPanel _helpBody = new() { Spacing = 0, Margin = new Thickness(0, 0, 8, 24) };
    private readonly ScrollViewer _helpScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Border _help = new() { Width = HelpWidth, BorderThickness = new Thickness(1, 0, 0, 0) };
    private readonly TextBlock _toastText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _toast = new() { IsVisible = false, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 6), BorderThickness = new Thickness(1) };
    private readonly Border _saveBar = new() { IsVisible = false, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
    /// <summary>The side bar (search and navigation), in the look's side-bar colour, as wide as the look's.</summary>
    private readonly Border _side = new();
    private readonly ColumnDefinition _sideColumn = new(250, GridUnitType.Pixel);
    private readonly DockPanel _middle = new();
    /// <summary>The whole window at the player's size (AppSettings.SettingsUiFontSize / 12, as the Windows shell's frame).</summary>
    private readonly LayoutTransformControl _scaled = new();
    private readonly TextBox _search = new();
    private readonly ListBox _results = new() { IsVisible = false, MaxHeight = 360 };

    public SharedSettingsWindow(ISettingsHost app, string? pageId = null)
    {
        _app = app;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Icon.ico"))
            if (icon != null) Icon = new WindowIcon(icon);

        var service = app.Settings;
        // The Windows shell's applier: a change waits a second, then the file is saved and the app is told, as Windows'
        // App does on SettingsApplied (keys bound again, alert profiles and log folders applied, the tray rebuilt). The
        // Linux thumbnails read their look at every tick; what is re-applied to them is their place and size (ThumbnailManager.
        // ReapplySettings: a whole-profile copy, a layout, a size given to all).
        var applier = new SettingsApplier(service.Save, app.ReapplyThumbnails, () => { }, () => { }, new DispatcherDebounceTimer(TimeSpan.FromSeconds(1)));
        applier.Applied += () => { app.HotkeysChanged(); app.AlertsChanged(); app.TrayChanged(); };
        var services = new AppServices
        {
            OpenAlertHistory = app.OpenAlertHistory,
            Snoozes = app.Snoozes,
            Speaker = app.Speaker,
            VoiceAnswered = app.VoiceAnswered,
            TestPush = app.TestPush,
            PushStatus = () => app.PushStatus,
            // The app's rule store, so the rules page's file is the one the log monitor reads (never a second store).
            CustomAlerts = app.CustomAlerts,
            CustomRulesSwitchedOff = () => app.CustomRulesSwitchedOff,
        };
        _ctx = new SettingsContext(service, applier, services, this);
        // The saved look (Windows' App.ApplySavedLook): the pick, and whether Daylight shows while the desktop is light.
        LinuxLooks.ApplySaved(S);
        LinuxLooks.Dress(this);
        _editors = new SettingsEditors(app, this, () => QueueRender(), Reload);
        RestoreSize();

        // The pages as Linux shows them: without the cards of Windows-only features and of those Linux does not read yet.
        _vm = new ShellVM(_ctx, LinuxSupport.Pages, a => Dispatcher.UIThread.Post(a), LinuxSupport.Dynamic);
        _vm.PropertyChanged += OnShellChanged;
        _vm.Reloaded += () => { _index = null; QueueRender(); };
        if (pageId != null) _vm.Navigate(pageId);

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.IsVisible = false; };
        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); ClearHighlight(); };
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new RelayCommand(_ => { _search.Focus(); _search.SelectAll(); }) });

        _scaled.Child = Layout();
        Content = _scaled;
        Render();
        Action restyled = QueueRender;   // a change of look: the page is drawn again in it (its colours follow by themselves)
        LinuxLooks.Changed += restyled;

        Closing += (_, _) => { SaveSize(); _vm.OnClosing(); };
        Closed += (_, _) =>
        {
            _toastTimer.Stop();
            _highlightTimer.Stop();
            LinuxLooks.Changed -= restyled;
            Detach();
            _vm.PropertyChanged -= OnShellChanged;
            _vm.Dispose();
        };
    }

    private AppSettings S => _ctx.S;
    /// <summary>A key's text, in its Linux wording where that differs (LinuxSupport.Key).</summary>
    private static string Str(string key) => SettingsContext.Str(LinuxSupport.Key(key));
    /// <summary>The text of a key, or null when the catalog has none (a card without a description).</summary>
    private static string? Optional(string? key) => key != null && LocalizationService.Str(LinuxSupport.Key(key), "") is { Length: > 0 } text ? text : null;

    /// <summary>Shows a page by its id (the tray, a thumbnail's menu, EVEMP_OPEN_SETTINGS). An Advanced page leaves Simple mode.</summary>
    public void ShowPage(string pageId) => Navigate(pageId);

    // ── The window's parts ──

    private Control Layout()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(_sideColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

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
        _side.Child = left;
        LinuxLooks.Paint(_side, Border.BackgroundProperty, "Fx.NavBrush");
        LinuxLooks.Paint(_side, Border.BorderBrushProperty, "Fx.DividerBrush");
        _side.BorderThickness = new Thickness(0, 0, 1, 0);
        root.Children.Add(_side);

        var middle = _middle;
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
        LinuxLooks.Paint(_help, Border.BorderBrushProperty, "Fx.DividerBrush");
        LinuxLooks.Paint(_helpTitle, TextBlock.ForegroundProperty, "Fx.TitleBrush");
        LinuxLooks.Paint(_toast, Border.BackgroundProperty, "Fx.FlyoutBrush");
        LinuxLooks.Paint(_toast, Border.BorderBrushProperty, "Fx.FlyoutBorderBrush");
        LinuxLooks.Paint(_toastText, TextBlock.ForegroundProperty, "Fx.TextBrush");
        _toast.Child = _toastText;
        LinuxLooks.Paint(_saveBar, Border.BorderBrushProperty, "Fx.CriticalBrush");
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
        Restyle();
        Title = Str("L.S.Shell.Title");
        _search.PlaceholderText = Str("L.S.Search.Placeholder");
        Nav();
        Page();
        Help();
        SaveBar();
    }

    /// <summary>The window's metrics in the look drawn and at the player's size: the side bar's width, the page's padding,
    /// the text's family and size, the toast's corners.</summary>
    private void Restyle()
    {
        var look = LinuxLooks.Effective;
        _sideColumn.Width = new GridLength(Math.Max(200, look.Number("Nav.Width")));
        var pad = look.Thickness("Page.Padding");
        _middle.Margin = new Thickness(Math.Max(12, pad.Left), Math.Max(10, pad.Top), 0, 8);
        FontFamily = look.Font("Font");
        FontSize = look.Number("Size.Body");
        _toast.CornerRadius = look.Radius("FlyoutRadius");
        _saveBar.Background = new SolidColorBrush(look.Colour("Critical"), 0.18);
        _helpTitle.FontFamily = look.Font("TitleFont");
        var scale = LinuxLooks.Scale(S);
        _scaled.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? null : new ScaleTransform(scale, scale);
    }

    // ── Navigation ──

    private void Nav()
    {
        _nav.Children.Clear();
        _navBottom.Children.Clear();
        var current = _vm.Current?.Page;
        if (_vm.Home is { } home) _nav.Children.Add(NavButton(Str("L.S.Nav.Home"), current == home, sub: false, () => Navigate(home.Id)));
        if (_vm.IsSimple) _nav.Children.Add(NavButton(Str("L.S.Nav.AllSettings"), false, sub: false, _vm.AllSettings));
        foreach (var group in _vm.VisibleGroups) AddGroup(_nav, group);
        if (_vm.AppGroup is { } app) AddGroup(_navBottom, app);

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(6, 10, 0, 4) };
        var simple = new RadioButton { Content = NavText(Str("L.S.Shell.Simple")), GroupName = "mode", IsChecked = _vm.IsSimple };
        var all = new RadioButton { Content = NavText(Str("L.S.Nav.AllSettings")), GroupName = "mode", IsChecked = !_vm.IsSimple };
        simple.IsCheckedChanged += (_, _) => { if (simple.IsChecked == true && !_vm.IsSimple) _vm.IsSimple = true; };
        all.IsCheckedChanged += (_, _) => { if (all.IsChecked == true && _vm.IsSimple) _vm.AllSettings(); };
        LinuxLooks.Paint(simple, TemplatedControl.ForegroundProperty, "Fx.NavTextBrush");
        LinuxLooks.Paint(all, TemplatedControl.ForegroundProperty, "Fx.NavTextBrush");
        modes.Children.Add(simple);
        modes.Children.Add(all);
        _navBottom.Children.Add(modes);

        var help = new ToggleButton { Content = Str("L.S.Shell.Help"), IsChecked = _vm.HelpOpen, Margin = new Thickness(6, 4, 0, 0) };
        help.IsCheckedChanged += (_, _) => { if ((help.IsChecked == true) != _vm.HelpOpen) _vm.HelpOpen = help.IsChecked == true; };
        _navBottom.Children.Add(help);
    }

    private void AddGroup(StackPanel into, NavGroupVM group)
    {
        into.Children.Add(NavButton(Str(group.TitleKey), group.IsCurrent && !group.IsExpanded, sub: false, () => Navigate(group.Pages[0].Page.Id)));
        if (!group.IsExpanded) return;
        var indent = Math.Clamp(LinuxLooks.Effective.Number("Nav.SubIndent") - 16, 8, 30);
        foreach (var page in group.Pages)
        {
            var b = NavButton(Str(page.TitleKey), page.IsSelected, sub: true, () => Navigate(page.Page.Id));
            b.Margin = new Thickness(indent, 1, 0, 1);
            into.Children.Add(b);
        }
    }

    /// <summary>A navigation entry (Base.Nav.Entry): a group or Home in the look's navigation weight, a page under its group
    /// in the look's sub-entry size; the open one in the look's selected colours (the window's "nav" styles).</summary>
    private static Button NavButton(string text, bool selected, bool sub, Action open)
    {
        var look = LinuxLooks.Effective;
        var b = new Button
        {
            Content = NavText(text, selected ? look.Weight("Weight.NavSelected") : sub ? look.Weight("Weight.Nav") : FontWeight.SemiBold),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("nav");
        if (sub) b.Classes.Add("sub");
        if (selected) b.Classes.Add("selected");
        b.Click += (_, _) => open();
        return b;
    }

    /// <summary>A navigation text, in capitals where the look writes them so (Fx.Case.Nav).</summary>
    private static TextBlock NavText(string text, FontWeight? weight = null) =>
        new() { Text = LinuxLooks.Cased(text, LinuxLooks.Effective.Upper("Nav")), TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeight.Normal };

    // ── Search ──

    private void Search()
    {
        var hits = SearchIndex.Query(SearchEntries(), _search.Text ?? "");
        _results.Items.Clear();
        foreach (var hit in hits)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = hit.Text, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(LinuxLooks.Text(hit.Path, "Desc"));
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
    /// alert events and the player's own alert rules, built again after a reload and whenever the known characters or the
    /// rules change; of the pages and help only what Linux shows, plus its own cards (LinuxSupport).</summary>
    private IReadOnlyList<SearchEntry> SearchEntries()
    {
        var names = CharacterDirectory.Known(S, _app.RunningCharacters());
        var rules = _ctx.CustomAlerts.Rules;
        var key = LocalizationService.CurrentLanguage + "\n" + string.Join("\n", names) + "\n\n" + string.Join("\n", rules.Select(r => r.Id + " " + r.Name + " " + r.Pattern));
        if (_index == null || key != _indexKey)
        {
            _indexKey = key;
            string Resolve(string k) => LocalizationService.Str(LinuxSupport.Key(k), "");
            string English(string k) => LocalizationService.EnglishText(LinuxSupport.Key(k));
            _index = SearchIndex.Build(LinuxSupport.Pages, LinuxSupport.Topics, Resolve, English,
                extra: CharacterStores.SearchEntries(names, Resolve).Concat(EventEntries(Resolve)).Concat(LinuxSupport.SearchEntries(Resolve))
                    .Concat(SettingsEditors.CustomRuleEntries(rules, Resolve)));
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

        var look = LinuxLooks.Effective;
        // The group's name over the title, where the look shows a breadcrumb (Fx.Show.Breadcrumb).
        if (look.Shows("Show.Breadcrumb") && _vm.GroupTitleKey is { } groupKey && _vm.BreadcrumbTargetId is { } target)
        {
            var crumb = new Button { Content = Str(groupKey) + "  ›", Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
            crumb.Classes.Add("quiet");
            crumb.Click += (_, _) => Navigate(target);
            _header.Children.Add(crumb);
        }
        _header.Children.Add(LinuxLooks.Text(page.Page.Title ?? Str(page.Page.TitleKey), "Title"));
        if (Optional(page.Page.DescKey) is { } desc)
        {
            var line = LinuxLooks.Text(desc, "Label");
            LinuxLooks.Paint(line, TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
            line.Margin = new Thickness(0, 4, 0, 0);
            _header.Children.Add(line);
        }

        foreach (var section in page.Sections) _sections.Children.Add(Section(section));
        // A page opens at its top (owner, 2026-10-09); the same page drawn again (an editor changed it) keeps its place.
        if (page.Page.Id != _shownPageId) _scroll.Offset = default;
        _shownPageId = page.Page.Id;
        // The item search or a link opened the page for: Show marked it before this page was drawn.
        if (_vm.HighlightedItem is { } hit) Highlight(hit.Id, true);
        // A Linux-only card the search opened: no view-model marks it, so the window does.
        if (_linuxHit != null && _cards.ContainsKey(_linuxHit)) Highlight(_linuxHit, true);
        _linuxHit = null;
    }

    private Control Section(SectionVM section)
    {
        var rows = new StackPanel { Spacing = 0 };
        foreach (var item in section.Items) rows.Children.Add(Card(item));
        if (LinuxSupport.Additions.TryGetValue(section.Def.Id, out var additions))
            foreach (var addition in additions) rows.Children.Add(LinuxCard(addition));
        var panel = LookSections.Build(LinuxLooks.Effective, Str(section.TitleKey), section.Ordinal, rows);
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
        var card = LookSections.Row(LinuxLooks.Effective, inner);
        _cards[item.Id] = card;
        card.PointerEntered += (_, _) => FollowHelp(item.Id);
        card.GotFocus += (_, _) => FollowHelp(item.Id);
        card.IsVisible = Safe(() => item.IsVisible, true);
        Watch(item, name => { if (name is "" or null or nameof(ItemVM.IsVisible)) card.IsVisible = Safe(() => item.IsVisible, true); });
        return card;
    }

    /// <summary>A card of the old Linux window (LinuxSupport.Additions): a note, the commands example, the import from Windows.</summary>
    private Control LinuxCard(LinuxSupport.Addition addition)
    {
        Control inner;
        try
        {
            inner = addition.Kind switch
            {
                LinuxSupport.AdditionKind.Commands => SettingsEditors.CommandsExample(),
                LinuxSupport.AdditionKind.Import => Titled(addition.TitleKey!, _editors.Import()),
                _ => LinuxLooks.Text(Str(addition.TextKey), "Desc"),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[Settings] card {addition.Id}: {ex.GetType().Name}");
            inner = NotYetCard(addition.TitleKey, addition.Id);
        }
        var card = LookSections.Row(LinuxLooks.Effective, inner);
        _cards[addition.Id] = card;   // the search opens it like a shared card
        return card;
    }

    private static Control Titled(string titleKey, Control body)
    {
        var panel = new StackPanel { Spacing = 4 };
        var title = LinuxLooks.Text(Str(titleKey), "Label");
        title.FontWeight = FontWeight.SemiBold;
        panel.Children.Add(title);
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>The label (and description) on the left, the control on the right; <paramref name="below"/> takes the whole
    /// width under the label (a box of several lines).</summary>
    private static Control Labelled(string label, string? desc, Control input, TextBlock? error = null, bool below = false, bool checkFirst = false)
    {
        var look = LinuxLooks.Effective;
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(LinuxLooks.Text(label, "Label"));
        if (desc != null) text.Children.Add(LinuxLooks.Text(desc, "Desc"));
        if (below)
        {
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(text);
            stack.Children.Add(input);
            if (error != null) stack.Children.Add(error);
            return stack;
        }
        if (error != null) text.Children.Add(error);
        input.VerticalAlignment = VerticalAlignment.Center;
        // The on/off control before its label (Base.Card.CheckFirst: Daylight, Dusk, Carbon).
        if (checkFirst)
        {
            var first = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            input.Margin = new Thickness(0, 0, 12, 0);
            first.Children.Add(input);
            Grid.SetColumn(text, 1);
            first.Children.Add(text);
            return first;
        }
        // A look with a label column (Fx.Label.Width: Classic's 150, Capsule's and Terminal's columns) puts the control
        // beside the label at that width; the others put it at the row's right end.
        var width = look.Number("Label.Width");
        var grid = new Grid { ColumnDefinitions = width > 0 ? new ColumnDefinitions(width.ToString(CultureInfo.InvariantCulture) + ",*") : new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        if (width > 0) input.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return grid;
    }

    private static TextBlock ErrorLine()
    {
        var line = LinuxLooks.Text("", "Desc");
        line.IsVisible = false;
        return LinuxLooks.Paint(line, TextBlock.ForegroundProperty, "Fx.CriticalBrush");
    }

    private Control SettingCard(SettingVM vm)
    {
        var error = ErrorLine();
        bool reading = false;   // the controls are being set from the view-model: their change events are not the player's
        Action read;
        Control input;
        bool below = false;
        bool checkFirst = false;
        Action? reread = null;   // Read, below: a refused change shows the stored value again

        void Commit(Action change)
        {
            if (reading) return;
            if (Run(change, error, Str(vm.LabelKey))) return;
            var said = error.Text;
            reread?.Invoke();
            error.Text = said;   // Read shows the view-model's own error only; the refusal stays under the card
            error.IsVisible = true;
        }

        switch (vm.Kind)
        {
            case SettingKind.Toggle:
            {
                var box = new LookToggle();
                box.IsCheckedChanged += (_, _) => Commit(() => vm.IsOn = box.IsChecked == true);
                read = () => box.IsChecked = vm.IsOn;
                input = box;
                checkFirst = LinuxLooks.Effective.CheckFirst;
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
                var box = new TextBox { Width = 120, MaxLength = 32 };
                var swatch = ColourPicker.Button(() => box.Text, () => S.CustomColorPalette, (hex, picked) =>
                {
                    RememberColour(picked);
                    box.Text = hex;
                    Commit(() => vm.Text = hex);
                }, Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel"));
                ToolTip.SetTip(swatch, Str(vm.LabelKey));
                void Paint() => (swatch.Tag as Action)?.Invoke();
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
                var clash = LinuxLooks.Paint(LinuxLooks.Text("", "Desc"), TextBlock.ForegroundProperty, "Fx.WarningBrush");
                clash.MaxWidth = 320;
                clash.IsVisible = false;
                box.Capturing += _app.CaptureHotkey;
                box.Changed += key => Commit(() => vm.Commit(key));
                var clear = new Button { Content = "×", Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
                clear.Classes.Add("quiet");
                ToolTip.SetTip(clear, Str("L.S.Hotkey.Clear"));
                clear.Click += (_, _) => Commit(() => vm.Commit(""));
                read = () =>
                {
                    var key = vm.D.Get(S) as string ?? "";
                    box.Value = key;
                    var others = key.Length == 0 ? new List<HotkeyUse>() : HotkeyUses.Clashes(S, LinuxSupport.Settings, key, HotkeyUses.IdOf(vm.D));
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
        reread = Read;
        Watch(vm, name => { if (name is "" or null) Read(); else if (name == nameof(ItemVM.IsHighlighted)) Highlight(vm.Id, vm.IsHighlighted); });
        return Labelled(Str(vm.LabelKey), Optional(vm.DescKey), input, error, below, checkFirst);
    }

    private Control ActionCard(ActionRowVM vm)
    {
        var error = ErrorLine();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var action in vm.Buttons)
        {
            var b = new Button { Content = new TextBlock { Text = Ui.NoAccess(Str(action.LabelKey)) }, Margin = new Thickness(6, 2, 0, 2) };
            if (action.IsDestructive) LinuxLooks.Paint(b, TemplatedControl.ForegroundProperty, "Fx.CriticalBrush");
            b.Click += (_, _) => Run(action.Run, error, AccessKeys.Spoken(Str(action.LabelKey)));
            buttons.Children.Add(b);
        }
        Watch(vm, name => { if (name == nameof(ItemVM.IsHighlighted)) Highlight(vm.Id, vm.IsHighlighted); });
        return Labelled(Str(vm.LabelKey), Optional(vm.DescKey), buttons, error);
    }

    private Control InfoCard(InfoVM vm)
    {
        var text = LinuxLooks.Text("", "Label");
        var key = vm.Severity switch
        {
            InfoSeverity.Error => "Fx.CriticalBrush",
            InfoSeverity.Warning => "Fx.WarningBrush",
            _ => "Fx.InfoBrush",
        };
        LinuxLooks.Paint(text, TextBlock.ForegroundProperty, key);
        // A note Linux words its own way (LinuxSupport.Key) shows the Linux text; the others the view-model's.
        bool linuxWords = LinuxSupport.Key(vm.Def.TextKey) != vm.Def.TextKey;
        void Read() => text.Text = linuxWords ? Str(vm.Def.TextKey) : Safe(() => vm.Text, Str(vm.Def.TextKey));
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
        if (Optional(labelKey) is { } label) panel.Children.Add(LinuxLooks.Text(AccessKeys.Spoken(label), "Label"));
        panel.Children.Add(LinuxLooks.Paint(LinuxLooks.Text(string.Format(LocalizationService.Str("L.Linux.NotYet", "Not available on Linux yet: {0}"), id), "Desc"),
            TextBlock.ForegroundProperty, "Fx.WarningBrush"));
        return panel;
    }

    /// <summary>Runs a change or an action. One a Linux stand-in refuses says so under its card and as a toast; any other
    /// failure is said too (by its kind, never its message) and written to the console, and the window stays open.</summary>
    /// <returns>False when it failed: a card then shows its stored value again instead of the refused one.</returns>
    private bool Run(Action run, TextBlock error, string? what = null)
    {
        error.IsVisible = false;   // a card's own error (a value out of range) is shown again by the card as it re-reads
        try
        {
            run();
            return true;
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine("[Settings] " + ex.Message);
            Say(error, string.Format(LocalizationService.Str("L.Linux.NotYet", "Not available on Linux yet: {0}"), what ?? ex.Message));
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[Settings] {what}: {ex.GetType().Name}: {ex.Message}");
            Say(error, ex.GetType().Name);
            return false;
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
        var text = LinuxLooks.Text(unit, "Desc");
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        return row;
    }

    // ── Highlight (search, Navigate with an item) ──

    private void Highlight(string id, bool on)
    {
        if (!on || !_cards.TryGetValue(id, out var card)) return;
        if (_highlighted != null) LookSections.Unmark(_highlighted);
        _highlighted = card;
        LookSections.Mark(card);
        Dispatcher.UIThread.Post(() => card.BringIntoView(), DispatcherPriority.Background);
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void ClearHighlight()
    {
        if (_highlighted != null) LookSections.Unmark(_highlighted);
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
        Blocks(LocalizationService.Str(LinuxSupport.Key(HelpTopics.IntroKey(pageId)), ""), _helpBody);
        foreach (var topic in LinuxSupport.Topics.Where(t => t.PageId == pageId))
        {
            var title = LocalizationService.Str(LinuxSupport.Key(topic.TitleKey), "");
            var body = LocalizationService.Str(LinuxSupport.Key(topic.BodyKey), "");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body)) continue;   // spec §8.5: hide, don't show blank
            var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            var heading = LinuxLooks.Text(title, "Label");
            heading.FontWeight = FontWeight.SemiBold;
            panel.Children.Add(heading);
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
            var line = LinuxLooks.Paint(new TextBlock { Inlines = inlines, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(block is HelpBullet ? 12 : 0, 6, 0, 0) },
                TextBlock.ForegroundProperty, "Fx.TextSecondaryBrush");
            into.Children.Add(line);
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
        if (_vm.SaveErrorLine is { } line) panel.Children.Add(LinuxLooks.Text(line, "Desc"));
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

    public void Navigate(string pageId, string? itemId = null)
    {
        _linuxHit = itemId != null && LinuxSupport.Additions.Values.Any(cards => cards.Any(c => c.Id == itemId)) ? itemId : null;
        _vm.Navigate(pageId, itemId);
        if (_linuxHit != null) QueueRender();   // the same page again: ShellVM raises nothing, so draw it to mark the card
    }

    private string? _linuxHit;

    public void Toast(string text)
    {
        _toastText.Text = text;
        _toast.IsVisible = text.Length > 0;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public bool Confirm(string title, string body, SafeAnswer safe = SafeAnswer.Cancel) =>
        Ask(title, body, new[] { Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel") }, safe == SafeAnswer.First ? 0 : 1, safe == SafeAnswer.First ? 0 : 1) == 0;

    public bool? ConfirmChoice(string title, string body, string yesLabel, string noLabel, SafeAnswer safe = SafeAnswer.Cancel)
    {
        int safeIndex = safe switch { SafeAnswer.First => 0, SafeAnswer.Second => 1, _ => 2 };
        return Ask(title, body, new[] { yesLabel, noLabel, Str("L.S.Shell.Cancel") }, safeIndex, safeIndex) switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
    }

    public string? AskText(string title, string prompt, string initial)
    {
        var box = new TextBox { Text = initial };
        var answer = Ask(title, prompt, new[] { Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel") }, defaultIndex: 0, cancelIndex: 1, box);
        return answer == 0 && !string.IsNullOrWhiteSpace(box.Text) ? box.Text : null;
    }

    public string? PickCharacter(string title, IReadOnlyList<string> names)
    {
        var box = new AutoCompleteBox { ItemsSource = names.ToList(), FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinWidth = 260 };
        var answer = Ask(title, "", new[] { Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel") }, defaultIndex: 0, cancelIndex: 1, box);
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

    /// <summary>The shared editors' colour question (an alert's colour, a severity's, a group's): the Linux picker in a small
    /// window over this one; OK gives "#RRGGBB" and keeps it among the player's own colours, as the Windows dialog does.</summary>
    public string? PickColor(string? initial)
    {
        var picker = new ColourPicker(initial, S.CustomColorPalette);
        var answer = Ask(Str("L.S.Shell.Title"), "", new[] { Str("L.S.Shell.Ok"), Str("L.S.Shell.Cancel") }, defaultIndex: 0, cancelIndex: 1, picker);
        if (answer != 0) return null;
        RememberColour(picker.Picked);
        return picker.Hex;
    }

    /// <summary>A picked colour first among the player's own colours (AppSettings.CustomColorPalette, shared with Windows).</summary>
    private void RememberColour(Color picked)
    {
        S.CustomColorPalette = ColourPicker.Remember(S.CustomColorPalette, picked);
        _ctx.Applier.Request(ApplyScope.Save);
    }

    /// <summary>The hotkey cards capture in their own box (HotkeyBox); nothing on Linux opens the Windows capture dialog.</summary>
    public string? CaptureHotkey(string settingId, string? current) => null;

    /// <summary>Everything again from the settings, in the current language (a language change, a reset, an import).</summary>
    public void Reload()
    {
        LinuxLooks.ApplySaved(S);
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

    /// <summary>A question in a small window over this one: the buttons in order, <paramref name="defaultIndex"/> the answer of
    /// Enter, <paramref name="cancelIndex"/> the answer of Esc and of closing the window. A question with a box (a name to type,
    /// a character to pick) takes OK on Enter but Cancel on Esc; a yes/no question answers its safe button to both.
    /// Returns the button's index.</summary>
    private int Ask(string title, string body, IReadOnlyList<string> answers, int defaultIndex, int cancelIndex, Control? extra = null)
    {
        var dialog = new Window
        {
            Title = AccessKeys.Spoken(title), Width = extra is ColourPicker ? 520 : 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        LinuxLooks.Dress(dialog);
        int result = cancelIndex;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        Button? safeButton = null;
        for (int i = 0; i < answers.Count; i++)
        {
            int index = i;
            var b = new Button { Content = new TextBlock { Text = Ui.NoAccess(answers[i]) }, IsDefault = i == defaultIndex, IsCancel = i == cancelIndex };
            b.Click += (_, _) => { result = index; dialog.Close(); };
            if (i == defaultIndex) safeButton = b;
            buttons.Children.Add(b);
        }
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        var heading = LinuxLooks.Text(AccessKeys.Spoken(title), "Label");
        heading.FontWeight = FontWeight.SemiBold;
        panel.Children.Add(heading);
        if (body.Length > 0) panel.Children.Add(LinuxLooks.Text(body, "Label"));
        if (extra != null) panel.Children.Add(extra);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Opened += (_, _) => { if (extra != null) extra.Focus(); else safeButton?.Focus(); };
        Wait(dialog.ShowDialog<object?>(this));
        return result;
    }

    // ── The Linux side of the shared editors' dialogs (ProfileActions.Linux.cs, CharacterDirectory.Running) ──

    /// <summary>The characters with a client running now (the Linux app's).</summary>
    internal IReadOnlyCollection<string> RunningCharacters() => _app.RunningCharacters();

    /// <summary>The Linux app behind this window (LinuxSupport's Linux bodies for shared buttons ask it).</summary>
    internal ISettingsHost App => _app;

    private BugReportWindow? _bugReport;

    /// <summary>S8: Diagnostics › Create bug report… (BugReportActions.Open): the report window over this one, built from this
    /// PC's files when it opens and again when the names switch changes. It reads only; the zip is written where the player
    /// picks, when they click Save, and the folder then opens. One at a time.</summary>
    internal void OpenBugReport()
    {
        if (_bugReport != null) { _bugReport.Activate(); return; }
        var settings = S;
        var running = _app.RunningCharacters().ToList();
        var screens = (Screens?.All ?? Array.Empty<Avalonia.Platform.Screen>())
            .Select(sc => new LinuxBugReport.Monitor(sc.IsPrimary,
                new System.Drawing.Rectangle(sc.Bounds.X, sc.Bounds.Y, sc.Bounds.Width, sc.Bounds.Height),
                new System.Drawing.Rectangle(sc.WorkingArea.X, sc.WorkingArea.Y, sc.WorkingArea.Width, sc.WorkingArea.Height), sc.Scaling))
            .ToList();
        var window = new BugReportWindow(mask => LinuxBugReport.Gather(settings, running, screens, mask),
            path => { if (Path.GetDirectoryName(path) is { Length: > 0 } dir) LinuxSupport.OpenWithDesktop(dir); },
            LinuxBugReport.Redactor(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.MachineName));
        window.Closed += (_, _) => _bugReport = null;
        _bugReport = window;
        window.Show(this);
    }

    /// <summary>Puts text on the clipboard (a look or profile code). False when this desktop has no clipboard to ask.</summary>
    internal bool CopyText(string text)
    {
        if (Clipboard is not { } clipboard) return false;
        try { _ = clipboard.SetTextAsync(text); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private Window Dialog(string titleKey, double width)
    {
        var dialog = new Window
        {
            Title = Ui.NoAccess(Str(titleKey)), Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        LinuxLooks.Dress(dialog);
        return dialog;
    }

    /// <summary>The Windows CopyLayoutDialog: copy from (the current profile first chosen), copy to (every other profile, or one),
    /// and what (the thumbnail layout only, or the entire profile). Null when it was closed without Copy.</summary>
    internal (string From, string? To, bool Full)? AskCopy(IReadOnlyList<string> names, string current)
    {
        var dialog = Dialog("L.Copy.Title", 420);
        var from = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var name in names) from.Items.Add(new TextBlock { Text = name });
        from.SelectedIndex = Math.Max(0, names.ToList().IndexOf(current));
        var to = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        to.Items.Add(new TextBlock { Text = LocalizationService.Str("L.Copy.AllProfiles", "— All Profiles —") });   // told by its place, not its words
        foreach (var name in names) to.Items.Add(new TextBlock { Text = name });
        to.SelectedIndex = 0;
        var layoutOnly = new RadioButton { GroupName = "what", IsChecked = true, Content = new TextBlock { Text = Str("L.Copy.LayoutOnly") } };
        var entireText = new StackPanel();
        entireText.Children.Add(new TextBlock { Text = Str("L.Copy.EntireProfile") });
        var entireNote = LinuxLooks.Text(Str("L.Copy.EntireNote"), "Desc");
        entireNote.MaxWidth = 280;
        entireText.Children.Add(entireNote);
        var entire = new RadioButton { GroupName = "what", Content = entireText };
        var what = new StackPanel { Spacing = 4 };
        what.Children.Add(layoutOnly);
        what.Children.Add(entire);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        void Put(Control c, int row, int column)
        {
            c.Margin = new Thickness(0, 0, column == 0 ? 8 : 0, 12);
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            grid.Children.Add(c);
        }
        TextBlock Label(string key) => new() { Text = Str(key), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Put(Label("L.Copy.From"), 0, 0);
        Put(from, 0, 1);
        Put(Label("L.Copy.To"), 1, 0);
        Put(to, 1, 1);
        var whatLabel = Label("L.Copy.What");
        whatLabel.VerticalAlignment = VerticalAlignment.Top;
        Put(whatLabel, 2, 0);
        Put(what, 2, 1);
        (string, string?, bool)? result = null;
        var copy = new Button { Content = new TextBlock { Text = Ui.Plain(Ui.NoAccess(Str("L.Copy.CopyBtn"))) }, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        copy.Click += (_, _) =>
        {
            if (from.SelectedIndex >= 0 && from.SelectedIndex < names.Count)
                result = (names[from.SelectedIndex], to.SelectedIndex <= 0 ? null : names[to.SelectedIndex - 1], entire.IsChecked == true);
            dialog.Close();
        };
        Grid.SetRow(copy, 3);
        Grid.SetColumnSpan(copy, 2);
        grid.Children.Add(copy);
        dialog.Content = new Border { Padding = new Thickness(20, 16), Child = grid };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        Wait(dialog.ShowDialog<object?>(this));
        return result;
    }

    /// <summary>The Windows LayoutRemapDialog: each character of a code (or slot of a layout) against one of the player's,
    /// a name that is also the player's (ignoring case) chosen first, "(skip)" to leave it out. The chosen character per index
    /// (a skipped one is left out), or null when it was cancelled or closed.</summary>
    internal Dictionary<int, string>? AskMapping(IReadOnlyList<string> characters, IReadOnlyList<string> local, bool profileCode)
    {
        static string L(string key, string english) => LocalizationService.Str(key, english);
        var dialog = Dialog(profileCode ? "L.Remap.ProfileTitle" : "L.Remap.Title", 480);
        var dock = new DockPanel { Margin = new Thickness(12) };
        var intro = new TextBlock
        {
            Text = profileCode
                ? L("L.Remap.ProfileIntro", "Assign each character in the code to one of your characters. Exact name matches are pre-selected; choose “(skip)” to leave a character out. Positions in a code are screen pixels, so they fit best on a similar monitor setup. A code never sets a hotkey.")
                : L("L.Remap.Intro", "Assign each preset slot to one of your running characters. Exact name matches are pre-selected; choose “(skip)” to leave a slot unused."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(intro, Dock.Top);
        dock.Children.Add(intro);
        Dictionary<int, string>? result = null;
        var lists = new List<ComboBox>();
        var ok = new Button { Content = new TextBlock { Text = L("L.Remap.Apply", "Apply") }, IsDefault = true };
        var cancel = new Button { Content = new TextBlock { Text = L("L.Remap.Cancel", "Cancel") }, IsCancel = true };
        ok.Click += (_, _) =>
        {
            result = new Dictionary<int, string>();
            for (int i = 0; i < lists.Count; i++)
                if (lists[i].SelectedIndex > 0 && !string.IsNullOrWhiteSpace(local[lists[i].SelectedIndex - 1]))
                    result[i] = local[lists[i].SelectedIndex - 1];
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        var rows = new StackPanel();
        for (int i = 0; i < characters.Count; i++)
        {
            var character = characters[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = $"{i + 1}.  {(string.IsNullOrWhiteSpace(character) ? L("L.Remap.Slot", "(slot)") : character)}",
                Width = 200, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var list = new ComboBox { Width = 220 };
            list.Items.Add(new TextBlock { Text = L("L.Remap.Skip", "(skip)") });
            int chosen = 0;
            for (int j = 0; j < local.Count; j++)
            {
                list.Items.Add(new TextBlock { Text = local[j] });
                if (chosen == 0 && string.Equals(local[j], character, StringComparison.OrdinalIgnoreCase)) chosen = j + 1;
            }
            list.SelectedIndex = chosen;
            row.Children.Add(list);
            rows.Children.Add(row);
            lists.Add(list);
        }
        dock.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 420, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        dialog.Content = dock;
        Wait(dialog.ShowDialog<object?>(this));
        return result;
    }
}
