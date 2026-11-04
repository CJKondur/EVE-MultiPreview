using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

public sealed class SectionVM : ObservableObject
{
    public SectionVM(SectionDef def, IReadOnlyList<ItemVM> items, int ordinal = 0)
    {
        Def = def; Items = items; _ordinal = ordinal;
        // A row already in error when the section is made (a profile switched in with a bad value) marks a folded header at
        // once (Task 3 review: the mark waited for the next change). The section is not opened for it here.
        _hasError = items.Any(i => i.HasError);
        foreach (var item in items) item.PropertyChanged += (_, _) => CheckErrors();
    }
    public SectionDef Def { get; }
    private int _ordinal;

    /// <summary>The section's place among the sections its page shows, from 1 (spec §5.4): a section hidden by Simple
    /// mode or by its cards' ShowIf is not counted, so the numbers a look draws never jump (fix round C). The page sets
    /// it whenever what it shows may have changed.</summary>
    public int Ordinal { get => _ordinal; internal set { if (_ordinal == value) return; _ordinal = value; Raise(); } }
    public string TitleKey => Def.TitleKey;
    public IReadOnlyList<ItemVM> Items { get; }

    private CardList? _cards;

    /// <summary>The section's cards, one list for the section's life (made the first time a page shows it, on the
    /// window's thread). A look's Fx.Section hosts it as content (spec §8.1: a kept element in a swappable template), so
    /// a change of look that draws the section anew re-hosts the same cards and keeps what is in them.</summary>
    public CardList Cards => _cards ??= new CardList { ItemsSource = Items, Focusable = false };

    /// <summary>A section whose every card is hidden disappears with its title.</summary>
    public bool IsVisible => Items.Any(i => i.IsVisible);

    private bool _open = true;

    /// <summary>Open, or folded by a look whose sections fold (Capsule, UI styles plan S4). Not saved (spec O19): the
    /// window opens a page with its sections open, and a search hit opens its section.</summary>
    public bool IsOpen { get => _open; set { if (_open == value) return; _open = value; Raise(); } }
    private bool _hasError;

    /// <summary>A row of the section shows an error. A section that folds never hides it: the section opens itself when a
    /// row gains an error, and a look marks a folded header while this is true.</summary>
    public bool HasError => _hasError;

    private void CheckErrors()
    {
        var has = Items.Any(i => i.HasError);
        if (has == _hasError) return;
        _hasError = has;
        Raise(nameof(HasError));
        if (has) IsOpen = true;
    }

    public void RaiseVisibility() { foreach (var i in Items) i.RaiseVisibility(); Raise(nameof(IsVisible)); }
}

public sealed class PageVM : ObservableObject
{
    public PageVM(PageDef page, SettingsContext ctx)
    {
        Page = page;
        Sections = page.Sections.Select((s, i) => new SectionVM(s, s.Items.Select(item => Create(item, ctx)).ToList(), i + 1)).ToList();
        Renumber();
        foreach (var item in Items) item.Changed += RefreshVisibility;
        // A card of this page that sits beside Recently changed (App > General) lists its change at once.
        var recent = Items.OfType<EditorVM>().Where(e => e.Id == EditorIds.RecentChangesEditor).ToList();
        if (recent.Count > 0) foreach (var item in Items.OfType<SettingVM>()) item.Changed += () => recent.ForEach(e => e.Refresh());
    }

    public PageDef Page { get; }
    public IReadOnlyList<SectionVM> Sections { get; }
    public IEnumerable<ItemVM> Items => Sections.SelectMany(s => s.Items);
    public ItemVM? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public void Refresh()
    {
        foreach (var i in Items) i.Refresh();
        RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        foreach (var s in Sections) s.RaiseVisibility();
        Renumber();
    }

    /// <summary>Numbers the sections shown 1, 2, 3 in order; a hidden one keeps 0.</summary>
    private void Renumber()
    {
        int n = 0;
        foreach (var s in Sections) s.Ordinal = s.IsVisible ? ++n : 0;
    }

    private static ItemVM Create(PageItem item, SettingsContext ctx) => item switch
    {
        SettingItem si => new SettingVM(si.Setting, ctx),
        ActionRow ar => new ActionRowVM(ar, ctx),
        InfoItem ii => new InfoVM(ii, ctx),
        EditorItem ei => new EditorVM(ei, ctx),
        _ => throw new NotSupportedException(item.GetType().Name),
    };
}
