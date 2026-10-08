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
            if (e.PropertyName is "" or nameof(ShellVM.HelpOpen) or nameof(ShellVM.Current)) UpdateHelp();
        };
        Sections.MouseMove += (_, e) => FollowHelp(e.OriginalSource);
        Sections.GotKeyboardFocus += (_, e) => FollowHelp(e.OriginalSource);
        UpdateHelp();

        _search = new SearchBox(SearchEntries, entry => Navigate(entry.PageId, entry.ItemId));
        SearchHost.Content = _search;
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _search.FocusBox()), Key.F, ModifierKeys.Control));

        Closing += (_, _) => { SaveSize(); Vm.OnClosing(); };
        Closed += (_, _) => { _clock.Stop(); _toastTimer.Stop(); _highlightTimer.Stop(); Vm.Dispose(); };
    }

    public ShellVM Vm { get; }
    internal SettingsContext Context => _ctx;

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
        if (!string.IsNullOrEmpty(initial))
        {
            var c = AppSettings.ParseColor(initial, Color.FromRgb(0xFF, 0xFF, 0xFF));
            d.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        return d.ShowDialog() == WinForms.DialogResult.OK ? $"#{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}" : null;
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

    public bool? ConfirmChoice(string title, string body) =>
        MessageBox.Show(this, body, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

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

    /// <summary>Built on first use and rebuilt after a reload, so a language switch is searchable at once.</summary>
    private IReadOnlyList<SearchEntry> SearchEntries() =>
        _searchIndex ??= SearchIndex.Build(_pages, HelpTopics.All,
            key => LocalizationService.Str(key, ""),
            key => EnglishText(key));

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
        var scale = Math.Clamp(_ctx.S.SettingsUiFontSize, 9, 20) / 12.0;
        Root.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
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

    private string? _helpPage;
    private string? _helpItem;

    /// <summary>Help column: 320 px when open, 0 when closed (spec §4: it pushes the content left).</summary>
    private void UpdateHelp()
    {
        HelpColumn.Width = new GridLength(Vm.HelpOpen ? 320 : 0);
        HelpHost.Visibility = Vm.HelpOpen ? Visibility.Visible : Visibility.Collapsed;
        var page = Vm.Current?.Page.HelpId;
        if (Vm.HelpOpen && page != null && page != _helpPage)
        {
            _help.ShowPage(page);
            _helpPage = page;
            _helpItem = null;
        }
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
