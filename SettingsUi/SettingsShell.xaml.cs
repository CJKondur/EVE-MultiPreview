using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace EveMultiPreview.SettingsUi;

/// <summary>The new settings window (spec §4). Created by App when UseLegacySettingsWindow is off.</summary>
public partial class SettingsShell : Window, ISettingsHost
{
    private readonly SettingsService _svc;
    private readonly SettingsContext _ctx;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HelpPane _help = new();
    private readonly IReadOnlyList<PageDef> _pages;
    private IReadOnlyList<SearchEntry>? _searchIndex;
    private SearchBox? _search;

    /// <summary>Same contract as SettingsWindow.SettingsApplied: App re-registers hotkeys and syncs
    /// the log monitor and alert hub on it (App.xaml.cs applyLiveSettings).</summary>
    public event Action? SettingsApplied;

    public SettingsShell(SettingsService service, AppServices services)
        : this(service, services, new DispatcherDebounceTimer(TimeSpan.FromSeconds(1)), SettingsRegistry.Pages) { }

    public SettingsShell(SettingsService service, AppServices services, IDebounceTimer timer, IReadOnlyList<PageDef> pages,
        Func<string, PageDef?>? resolve = null)
    {
        UiStyles.Attach(this);   // before the XAML is read: it names the base's converters (plan S1, ruling 3)
        InitializeComponent();
        _pages = pages;
        _svc = service;
        var applier = new SettingsApplier(
            save: service.Save,
            reapplyThumbnails: () => services.Thumbnails?.ReapplySettings(),
            reapplyOpacity: () => services.Thumbnails?.ApplyOpacityToAll(),
            refreshCrops: () => services.Crops?.Refresh(),
            timer: timer);
        applier.Applied += () => SettingsApplied?.Invoke();
        _ctx = new SettingsContext(service, applier, services, this);
        Vm = new ShellVM(_ctx, pages, a => Dispatcher.BeginInvoke(a), resolve ?? SettingsRegistry.Dynamic);
        Vm.Reloaded += () => { ApplyLanguageAndScale(); SettingsApplied?.Invoke(); };
        DataContext = Vm;

        services.Crops?.ShowCrops();   // issue #81 recovery net: opening settings always un-hides crops (as the old window did)
        ApplyLanguageAndScale();
        RestoreSize();

        _clock.Tick += (_, _) => UpdateTitle();
        _clock.Start();
        UpdateTitle();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastHost.Visibility = Visibility.Collapsed; };
        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); Vm.ClearHighlight(); };

        HelpHost.Content = _help;
        _help.CloseRequested += () => Vm.HelpOpen = false;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or nameof(ShellVM.Current)) ScrollToTopOnNewPage();
            if (e.PropertyName is "" or nameof(ShellVM.HelpOpen) or nameof(ShellVM.Current)) UpdateHelp();
        };
        Sections.MouseMove += (_, e) => FollowHelp(e.OriginalSource);
        Sections.GotKeyboardFocus += (_, e) => FollowHelp(e.OriginalSource);
        _helpShown = Vm.HelpOpen;   // remembered open: the saved width already holds the column
        UpdateHelp();

        _search = new SearchBox(SearchEntries, entry => Navigate(entry.PageId, entry.ItemId));
        SearchHost.Content = _search;
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _search.FocusBox()), Key.F, ModifierKeys.Control));

        // The wheel over a closed dropdown list scrolls the page: one handler for every list of the window.
        PreviewMouseWheel += WheelPastClosedLists;

        // The constructor ran before there was a handle, so it measured the primary monitor.
        SourceInitialized += (_, _) => UpdateMinWidth();
        DpiChanged += (_, _) => UpdateMinWidth();

        Closing += (_, _) => { SaveSize(); Vm.OnClosing(); };
        Closed += (_, _) => { _clock.Stop(); _toastTimer.Stop(); _highlightTimer.Stop(); Vm.Dispose(); };
    }

    public ShellVM Vm { get; }
    internal SettingsContext Context => _ctx;

    /// <summary>The wheel over a CLOSED dropdown list is not the list's: it goes to what is around the list, so the
    /// page scrolls. WPF's list takes one notch as "next choice" while it is closed and has the keyboard, so a player
    /// who had just used a list and then turned the wheel to read on changed the setting under the pointer without
    /// seeing it. An open list is left alone (it scrolls its own items), and so is everything that is no list; the
    /// arrow keys on a focused list change its choice as before. The window sees the wheel on its way down
    /// (PreviewMouseWheel), takes it, and raises the wheel message WPF would have raised, on what the list stands in.</summary>
    public static void WheelPastClosedLists(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        ComboBox? list = null;
        for (var at = e.OriginalSource as DependencyObject; at != null && list == null; at = ParentOf(at)) list = at as ComboBox;
        if (list == null || list.IsDropDownOpen) return;
        e.Handled = true;
        if (ParentOf(list) is UIElement around)
            around.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
    }

    /// <summary>What an element stands in: its visual parent, or where it has none (the window of an open drop-down,
    /// a run of text) its logical parent, which for a drop-down leads on to its list.</summary>
    private static DependencyObject? ParentOf(DependencyObject at) =>
        (at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : null) ?? LogicalTreeHelper.GetParent(at);

    private string? _scrolledPageId;

    /// <summary>A page always opens at its top (owner, 2026-10-09), however it was opened: sidebar, Home, the mode
    /// switch, search or a link. Re-reading the page it's on (a profile switch) keeps its place.</summary>
    private void ScrollToTopOnNewPage()
    {
        var id = Vm.Current?.Page.Id;
        if (id == _scrolledPageId) return;
        _scrolledPageId = id;
        ContentScroll.ScrollToTop();
    }

    // ── ISettingsHost ────────────────────────────────────────────────

    public void Navigate(string pageId, string? itemId = null)
    {
        Vm.Navigate(pageId, itemId);
        ContentScroll.ScrollToTop();
        if (itemId == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindItemElement(itemId)?.BringIntoView());
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    public void Toast(string text)
    {
        ToastText.Text = text;
        ToastHost.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public bool Confirm(string title, string body) =>
        MessageBox.Show(this, body, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? PickFolder(string? initial)
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(this) == true ? d.FolderName : null;
    }

    public string? PickFile(string? initial, string filter)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        var dir = string.IsNullOrEmpty(initial) ? null : Path.GetDirectoryName(initial);
        if (dir != null && Directory.Exists(dir)) d.InitialDirectory = dir;
        return d.ShowDialog(this) == true ? d.FileName : null;
    }

    public string? PickSaveFile(string defaultName, string filter)
    {
        var d = new Microsoft.Win32.SaveFileDialog { FileName = defaultName, Filter = filter };
        return d.ShowDialog(this) == true ? d.FileName : null;
    }

    public string? PickColor(string? initial)
    {
        using var d = new WinForms.ColorDialog { FullOpen = true };
        // The old window's shared custom palette: a colour saved once shows in every picker.
        if (_ctx.S.CustomColorPalette is { Length: > 0 } palette) d.CustomColors = palette;
        if (!string.IsNullOrEmpty(initial))
        {
            var c = AppSettings.ParseColor(initial, Color.FromRgb(0xFF, 0xFF, 0xFF));
            d.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        var owner = new DialogOwnerHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (d.ShowDialog(owner) != WinForms.DialogResult.OK) return null;
        _ctx.S.CustomColorPalette = d.CustomColors;
        _ctx.Applier.Request(ApplyScope.Save);
        return $"#{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}";
    }

    /// <summary>Makes this window the WinForms colour dialog's owner, so it opens over settings.</summary>
    private sealed class DialogOwnerHandle : WinForms.IWin32Window
    {
        public DialogOwnerHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    public string? CaptureHotkey(string settingId, string? current)
    {
        var title = HotkeyUses.LabelFor(_ctx.S, SettingsRegistry.Settings, settingId) ?? settingId;
        // Capture mode: every MultiPreview hotkey is released while the dialog is open, so a bound
        // key reaches it (and the clash list) instead of switching clients (Plan 3a Task 1).
        return HotkeyCapture.Run(_ctx.Services.Hotkeys, () =>
        {
            var dialog = new HotkeyCaptureDialog(title, current,
                candidate => HotkeyUses.Clashes(_ctx.S, SettingsRegistry.Settings, candidate, settingId)) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Result : null;
        });
    }

    public string? PickCharacter(string title, IReadOnlyList<string> names)
    {
        var dialog = new Views.CharacterPickerDialog(title, names) { Owner = this };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedName) ? dialog.SelectedName!.Trim() : null;
    }

    public bool? ConfirmChoice(string title, string body, string yesLabel, string noLabel)
    {
        var dialog = new ChoiceDialog(title, body, yesLabel, noLabel) { Owner = this };
        dialog.ShowDialog();
        return dialog.Result;
    }

    public string? AskText(string title, string prompt, string initial)
    {
        var text = Microsoft.VisualBasic.Interaction.InputBox(prompt, title, initial);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void Reload()
    {
        Vm.Reload();
        ApplyLanguageAndScale();
        _helpPage = null;
        _searchIndex = null;
        UpdateHelp();
    }

    private string? _searchCharacters;

    /// <summary>Built on first use, rebuilt after a reload (a language switch) and whenever the known
    /// characters change, so a client that just logged in is searchable (spec §6).</summary>
    private IReadOnlyList<SearchEntry> SearchEntries()
    {
        var names = CharacterDirectory.Known(_ctx.S, CharacterDirectory.Running(_ctx));
        var rules = _ctx.CustomAlerts.Rules;   // IDEAS #8: a rule is found by its name or its pattern
        var key = string.Join("\n", names) + "\n\n" + string.Join("\n", rules.Select(r => r.Id + " " + r.Name + " " + r.Pattern));
        if (_searchIndex == null || key != _searchCharacters)
        {
            _searchCharacters = key;
            _searchIndex = SearchIndex.Build(_pages, HelpTopics.All,
                k => LocalizationService.Str(k, ""),
                k => EnglishText(k),
                extra: CharacterStores.SearchEntries(names, k => LocalizationService.Str(k, ""))
                    .Concat(AlertEvents.SearchEntries(k => LocalizationService.Str(k, ""), EnglishText))
                    .Concat(CustomRulesEditor.SearchEntries(rules, k => LocalizationService.Str(k, ""))));
        }
        return _searchIndex;
    }

    private static Dictionary<string, string>? _english;

    /// <summary>The English catalog (always merged first in App.xaml) read once, for the search aliases.</summary>
    private static string EnglishText(string key)
    {
        if (_english == null)
        {
            var dict = System.Windows.Application.Current?.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Strings.en.xaml", StringComparison.OrdinalIgnoreCase) == true);
            if (dict != null)
            {
                _english = new Dictionary<string, string>();
                foreach (var k in dict.Keys.OfType<string>()) if (dict[k] is string v) _english[k] = v;
            }
        }
        return _english != null && _english.TryGetValue(key, out var text) ? text : LocalizationService.Str(key, "");
    }

    public Window? DialogOwner => this;

    /// <summary>Opens settings on a page (Plan 4): a thumbnail's "Open ‹character›'s settings" (D6) and the tray's Manage
    /// profiles…. Only the new window has pages; the old one opens as it always did. Returns whether the window now shows
    /// that page. Like search, a page outside Home leaves Simple mode (ShellVM.Navigate).</summary>
    public static bool OpenOn(Window window, string? pageId)
    {
        if (string.IsNullOrEmpty(pageId) || window is not SettingsShell shell) return false;
        shell.Navigate(pageId);
        return shell.Vm.Current?.Page.Id == pageId;
    }

    // ── Chrome ───────────────────────────────────────────────────────

    private void OnProfileMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProfileButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var name in _svc.GetProfileNames())
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = name == _ctx.S.LastUsedProfile };
            item.Click += (_, _) => ProfileActions.Switch(_ctx, name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("L.S.Profile.New", () => ProfileActions.NewInteractive(_ctx)));
        menu.Items.Add(MenuEntry("L.S.Profile.Copy", () => ProfileActions.CopyInteractive(_ctx)));
        menu.Items.Add(MenuEntry("L.S.Profile.BackUp", () => SettingsBackup.BackupInteractive(_ctx)));
        menu.Items.Add(MenuEntry("L.S.Profile.Manage", () => Navigate("app.profiles")));
        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string key, Action run)
    {
        var item = new MenuItem();
        Loc.SetKey(item, key);
        item.Click += (_, _) => run();
        return item;
    }

    private void OnBreadcrumbGroup(object sender, RoutedEventArgs e)
    {
        if (Vm.BreadcrumbTargetId is { } target) Navigate(target);
    }

    private void OnRetrySave(object sender, RoutedEventArgs e) => Vm.RetrySave();

    private void UpdateTitle()
    {
        var now = DateTime.Now;
        Title = $"{SettingsContext.Str("L.S.Shell.Title")}  ·  {now:HH:mm}  ·  EVE {now.ToUniversalTime():HH:mm}";
    }

    /// <summary>CJK font per language (spec §7) and the window scale (SettingsUiFontSize / 12, as the old window).</summary>
    private void ApplyLanguageAndScale()
    {
        FontFamily = UiFont.For(LocalizationService.CurrentLanguage);
        var scale = _scale = Math.Clamp(_ctx.S.SettingsUiFontSize, 9, 20) / 12.0;
        Root.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        UpdateMinWidth();
    }

    /// <summary>1240×800 unless a settings window was resized before; never larger than the work area.</summary>
    private void RestoreSize()
    {
        var defaults = new AppSettings();
        bool resized = _ctx.S.SettingsWindowWidth != defaults.SettingsWindowWidth || _ctx.S.SettingsWindowHeight != defaults.SettingsWindowHeight;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(resized ? _ctx.S.SettingsWindowWidth : 1240, area.Width);
        Height = Math.Min(resized ? _ctx.S.SettingsWindowHeight : 800, area.Height);
    }

    private void SaveSize()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;
        _ctx.S.SettingsWindowWidth = (int)bounds.Width;
        _ctx.S.SettingsWindowHeight = (int)bounds.Height;
    }

    private double _scale = 1.0;
    private string? _helpPage;
    private string? _helpItem;

    /// <summary>Every page fits from about 600 px of content (the pseudo-locale pass, Plan 3d): a 960 px window with help
    /// closed, 1240 px with the 320 px help column open, as far as the screen allows.</summary>
    public const double ClosedMinWidth = 960, HelpOpenMinWidth = 1240;

    /// <summary>The help column's width before the window's UI scale.</summary>
    public const double HelpWidth = 320;

    /// <summary>Whether the help column is shown now; opening and closing are told apart from page changes by it.
    /// Starts as the remembered state, so a window that opens with help open keeps its saved width.</summary>
    private bool _helpShown;

    /// <summary>Help column: 320 px when open, 0 when closed. Opening it adds the column on the window's right and
    /// closing it gives that width back (owner, 2026-10-08), so the page keeps its width.</summary>
    private void UpdateHelp()
    {
        bool toggled = Vm.HelpOpen != _helpShown;
        _helpShown = Vm.HelpOpen;
        HelpColumn.Width = new GridLength(Vm.HelpOpen ? HelpWidth : 0);
        HelpHost.Visibility = Vm.HelpOpen ? Visibility.Visible : Visibility.Collapsed;
        if (toggled) ResizeForHelp();
        else KeepRoomForThePage();
        var page = Vm.Current?.Page.HelpId;
        if (Vm.HelpOpen && page != null && page != _helpPage)
        {
            _help.ShowPage(page);
            _helpPage = page;
            _helpItem = null;
        }
    }

    /// <summary>Opening help widens a narrow window instead of squeezing the page, on the monitor the window is on. A
    /// window that grows is nudged left so it stays on that monitor; a window that isn't on it (tests) is left where it
    /// is, and so is a maximized one.</summary>
    private void KeepRoomForThePage()
    {
        var area = UpdateMinWidth();
        if (WindowState != WindowState.Normal) return;
        // Nudge only when this call grew the window: a window the player dragged part-way off the screen stays put.
        if (Width < MinWidth) { Width = MinWidth; Left = NudgedLeft(Left, Width, area.Left, area.Right); }
    }

    /// <summary>Help was opened or closed: the window grows or shrinks by the column (scaled with the UI size) on its right
    /// edge. A window that would then run past its monitor's right edge is moved left just enough; a maximized one
    /// can't grow, so there the column shares the space.</summary>
    private void ResizeForHelp()
    {
        var width = Width;   // read first: raising MinWidth below snaps the window up to it
        var area = UpdateMinWidth();
        if (WindowState != WindowState.Normal) return;
        Width = WidthForHelp(width, Vm.HelpOpen, HelpWidth * _scale, MinWidth, area.Width);
        Left = NudgedLeft(Left, Width, area.Left, area.Right);
    }

    /// <summary>The window width after help opens (adds the column) or closes (removes it), held between the minimum
    /// width and the width of the monitor's work area.</summary>
    public static double WidthForHelp(double width, bool opening, double column, double minWidth, double areaWidth) =>
        Math.Min(Math.Max(opening ? width + column : width - column, minWidth), Math.Max(minWidth, areaWidth));

    /// <summary>The minimum width scales with the window's UI size (the content's scale transform is inside the window),
    /// and is capped by the work area of the monitor the window is on. Returns that work area.</summary>
    private Rect UpdateMinWidth()
    {
        var area = WorkAreaOfThisMonitor();
        MinWidth = Math.Min((Vm.HelpOpen ? HelpOpenMinWidth : ClosedMinWidth) * _scale, area.Width);
        return area;
    }

    /// <summary>The left edge that keeps a window of this width inside [areaLeft, areaRight], or the same left when the
    /// window starts outside that span (or has no position yet).</summary>
    public static double NudgedLeft(double left, double width, double areaLeft, double areaRight) =>
        left >= areaLeft && left < areaRight && left + width > areaRight
            ? Math.Max(areaLeft, areaRight - width)
            : left;

    private Rect WorkAreaOfThisMonitor()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;
        var px = MonitorInfo.WorkAreaOf(hwnd);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(px.Left / dpi.DpiScaleX, px.Top / dpi.DpiScaleY, px.Width / dpi.DpiScaleX, px.Height / dpi.DpiScaleY);
    }

    /// <summary>While help is open, the card under the mouse or keyboard focus scrolls the pane to its topic.</summary>
    private void FollowHelp(object source)
    {
        if (!Vm.HelpOpen) return;
        for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: ItemVM item })
            {
                if (item.Id != _helpItem && _help.ScrollToTopicFor(item.Id)) _helpItem = item.Id;
                return;
            }
        }
    }

    private FrameworkElement? FindItemElement(string itemId)
    {
        var queue = new Queue<DependencyObject>(new[] { (DependencyObject)Sections });
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is FrameworkElement fe && fe.DataContext is ItemVM vm && vm.Id == itemId && fe is ContentPresenter) return fe;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }
}
