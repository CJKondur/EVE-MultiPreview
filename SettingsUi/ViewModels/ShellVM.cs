using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

public sealed class NavPageVM : ObservableObject
{
    private bool _selected;
    public NavPageVM(PageDef page, Action<string> navigate, int ordinal = 0)
    {
        Page = page;
        Ordinal = ordinal;
        OpenCommand = new RelayCommand(_ => navigate(page.Id));
    }
    public PageDef Page { get; }
    /// <summary>The page's place in its group, from 1 (spec §5.4: a look may number its index).</summary>
    public int Ordinal { get; }
    /// <summary>Its group's place in the navigation.</summary>
    public int GroupOrdinal { get; internal set; }
    public string TitleKey => Page.TitleKey;
    public bool IsSelected { get => _selected; set { _selected = value; Raise(); } }
    public ICommand OpenCommand { get; }
}

public sealed class NavGroupVM : ObservableObject
{
    private bool _expanded;
    public NavGroupVM(NavGroup group, IReadOnlyList<NavPageVM> pages, Action<string> navigate, int ordinal = 0)
    {
        Group = group;
        Pages = pages;
        Ordinal = ordinal;
        foreach (var page in pages) page.GroupOrdinal = ordinal;
        OpenCommand = new RelayCommand(_ => navigate(pages[0].Page.Id));
    }
    public NavGroup Group { get; }
    /// <summary>The group's place in the navigation, Home being 1 when there is one (spec §5.4).</summary>
    public int Ordinal { get; }
    public string TitleKey => "L.S.Nav." + Group;
    /// <summary>Segoe Fluent Icons glyph (falls back to Segoe MDL2 Assets on Windows 10).</summary>
    public string Icon => Group switch
    {
        NavGroup.Clients => "",
        NavGroup.Thumbnails => "",
        NavGroup.Alerts => "",
        NavGroup.Performance => "",
        NavGroup.Tools => "",
        _ => "",
    };
    public IReadOnlyList<NavPageVM> Pages { get; }
    public bool IsExpanded { get => _expanded; set { _expanded = value; Raise(); } }
    public bool IsCurrent => Pages.Any(p => p.IsSelected);
    public ICommand OpenCommand { get; }
    public void RaiseCurrent() => Raise(nameof(IsCurrent));
}

/// <summary>The window's state: navigation, Simple/Advanced, help, profile, save errors (spec §4).</summary>
public sealed class ShellVM : ObservableObject, IDisposable
{
    private readonly SettingsContext _ctx;
    private readonly Action<Action> _marshal;
    private readonly Func<string, PageDef?>? _resolve;
    private readonly Dictionary<string, PageVM> _pages = new();

    /// <param name="resolve">Builds a page that isn't in <paramref name="pages"/> from its id (a
    /// character page, "character/Logi"); null when there is none.</param>
    public ShellVM(SettingsContext ctx, IReadOnlyList<PageDef> pages, Action<Action> marshal, Func<string, PageDef?>? resolve = null)
    {
        _ctx = ctx;
        _marshal = marshal;
        _resolve = resolve;
        Pages = pages;
        Home = pages.FirstOrDefault(p => p.Group == NavGroup.Home);
        int firstGroup = Home != null ? 2 : 1;   // Home is entry 1 of the navigation when there is one
        Groups = pages.Where(p => p.Group != NavGroup.Home)
                      .GroupBy(p => p.Group).OrderBy(g => g.Key)
                      .Select((g, i) => new NavGroupVM(g.Key, g.Select((p, j) => new NavPageVM(p, id => Navigate(id), j + 1)).ToList(),
                          id => Navigate(id), firstGroup + i))
                      .ToList();
        HomeCommand = new RelayCommand(_ => { if (Home != null) Navigate(Home.Id); });
        AllSettingsCommand = new RelayCommand(_ => AllSettings());
        var start = Home ?? pages.FirstOrDefault();
        if (start != null) Show(start, null);
        // Subscribe last: if Show throws, the app falls back to the old window and no dead view-model stays wired up.
        ctx.Service.ProfileSwitched += OnProfileSwitched;
        ctx.Service.SaveCompleted += OnSaveCompletedAnyThread;
    }

    public IReadOnlyList<PageDef> Pages { get; }
    public IReadOnlyList<NavGroupVM> Groups { get; }
    public PageDef? Home { get; }
    public bool HasHome => Home != null;
    /// <summary>Home's place in the navigation: 1, or 0 without Home.</summary>
    public int HomeOrdinal => HasHome ? 1 : 0;
    public PageVM? Current { get; private set; }
    /// <summary>The breadcrumb's first part: a dynamic page's parent ("Characters"), else the group.</summary>
    public string? GroupTitleKey =>
        Current == null || Current.Page.Group == NavGroup.Home ? null
        : Current.Page.ParentId is { } parent ? Pages.FirstOrDefault(p => p.Id == parent)?.TitleKey
        : "L.S.Nav." + Current.Page.Group;

    /// <summary>Where clicking the breadcrumb's first part goes: the parent page, else the group's first page.</summary>
    public string? BreadcrumbTargetId =>
        Current?.Page.ParentId ?? Groups.FirstOrDefault(g => g.Group == Current?.Page.Group)?.Pages.FirstOrDefault()?.Page.Id;

    public ICommand HomeCommand { get; }
    public ICommand AllSettingsCommand { get; }
    public ItemVM? HighlightedItem { get; private set; }

    /// <summary>Raised after a profile switch re-read every card. The shell forwards it as SettingsApplied.</summary>
    public event Action? Reloaded;

    public bool IsSimple
    {
        get => _ctx.S.SimpleMode;
        set
        {
            if (value == IsSimple) return;
            _ctx.S.SimpleMode = value;
            _ctx.Applier.Request(ApplyScope.Save);
            if (value && Home != null) Show(Home, null);
            else Current?.Refresh();   // ShowIf-gated items (the Simple banner on Home) follow the mode now
            RaiseAll();
        }
    }
    public bool IsAdvanced { get => !IsSimple; set => IsSimple = !value; }
    public bool ShowGroups => !IsSimple;

    /// <summary>Simple mode is Home plus the App pages (owner, 2026-10-08): language, profiles, updates and
    /// diagnostics stay one click away. Every other page is Advanced.</summary>
    public static bool InSimpleMode(PageDef page) => page.Group is NavGroup.Home or NavGroup.App;

    /// <summary>App settings: shown apart at the bottom left, above the Simple/Advanced switch, in both modes
    /// (owner, 2026-10-09).</summary>
    public NavGroupVM? AppGroup => Groups.FirstOrDefault(g => g.Group == NavGroup.App);

    /// <summary>The scrolling list's groups: none in Simple mode, every group but App in Advanced.</summary>
    public IReadOnlyList<NavGroupVM> VisibleGroups =>
        IsSimple ? new List<NavGroupVM>() : Groups.Where(g => g.Group != NavGroup.App).ToList();

    public bool HelpOpen
    {
        get => _ctx.S.SettingsHelpOpen;
        set { _ctx.S.SettingsHelpOpen = value; _ctx.Applier.Request(ApplyScope.Save); Raise(); }
    }

    public string ProfileName => _ctx.S.LastUsedProfile;
    public string ProfileInitial => ProfileName.Length > 0 ? ProfileName[..1].ToUpperInvariant() : "?";

    public string? SaveErrorKey { get; private set; }
    public bool HasSaveError => SaveErrorKey != null;
    public bool CanRetrySave => SaveErrorKey == "L.S.Shell.SaveFailed";

    public void Navigate(string pageId, string? itemId = null)
    {
        var page = Pages.FirstOrDefault(p => p.Id == pageId)
                   ?? (_pages.TryGetValue(pageId, out var built) ? built.Page : _resolve?.Invoke(pageId));
        if (page == null) return;
        if (!InSimpleMode(page) && IsSimple)
        {
            _ctx.S.SimpleMode = false;   // an Advanced page from search or help: show where it lives
            _ctx.Applier.Request(ApplyScope.Save);
        }
        Show(page, itemId);
    }

    public void AllSettings()
    {
        IsSimple = false;
        var first = Groups.FirstOrDefault()?.Pages.FirstOrDefault();
        if (first != null && (Current == null || Current.Page.Group == NavGroup.Home)) Show(first.Page, null);
    }

    public void ClearHighlight()
    {
        if (HighlightedItem != null) HighlightedItem.IsHighlighted = false;
        HighlightedItem = null;
    }

    /// <summary>Re-read every card on the current page (pages are re-read whenever they're shown).</summary>
    public void Reload()
    {
        Current?.Refresh();
        RaiseAll();
    }

    /// <summary>Spec D5: no Apply button and no prompt. Closing applies anything still waiting
    /// and saves (the window size is written just before this).</summary>
    public void OnClosing() => _ctx.Applier.ApplyNow();

    public void OnSaveCompleted(SaveStatus status)
    {
        SaveErrorKey = status switch
        {
            SaveStatus.WriteFailed => "L.S.Shell.SaveFailed",
            SaveStatus.BlockedAfterLoadError => "L.S.Shell.SaveBlocked",
            _ => null,
        };
        Raise(nameof(SaveErrorKey));
        Raise(nameof(HasSaveError));
        Raise(nameof(CanRetrySave));
    }

    public void RetrySave() => _ctx.Service.Save();

    public void Dispose()
    {
        _ctx.Service.ProfileSwitched -= OnProfileSwitched;
        _ctx.Service.SaveCompleted -= OnSaveCompletedAnyThread;
    }

    private void Show(PageDef page, string? itemId)
    {
        if (!_pages.TryGetValue(page.Id, out var vm)) _pages[page.Id] = vm = new PageVM(page, _ctx);
        else vm.Refresh();   // values may have changed elsewhere (a thumbnail drag, a hotkey, another page)
        Current = vm;
        var navId = page.ParentId ?? page.Id;   // a character page keeps "Characters" selected
        foreach (var g in Groups)
        {
            g.IsExpanded = g.Group == page.Group;
            foreach (var p in g.Pages) p.IsSelected = p.Page.Id == navId;
            g.RaiseCurrent();
        }
        ClearHighlight();
        if (itemId != null && vm.Find(itemId) is { } item) { item.IsHighlighted = true; HighlightedItem = item; }
        RaiseAll();
    }

    private void OnProfileSwitched(string _) => _marshal(() =>
    {
        foreach (var page in _pages.Values) page.Refresh();
        RaiseAll();
        Reloaded?.Invoke();
    });

    private void OnSaveCompletedAnyThread(SaveStatus status) => _marshal(() => OnSaveCompleted(status));
}
