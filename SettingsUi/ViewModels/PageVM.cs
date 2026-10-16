using System;
using System.Collections.Generic;
using System.Linq;

namespace EveMultiPreview.SettingsUi;

public sealed class SectionVM : ObservableObject
{
    public SectionVM(SectionDef def, IReadOnlyList<ItemVM> items, int ordinal = 0) { Def = def; Items = items; Ordinal = ordinal; }
    public SectionDef Def { get; }
    /// <summary>The section's place on its page, from 1 (spec §5.4).</summary>
    public int Ordinal { get; }
    public string TitleKey => Def.TitleKey;
    public IReadOnlyList<ItemVM> Items { get; }

    private CardList? _cards;

    /// <summary>The section's cards, one list for the section's life (made the first time a page shows it, on the
    /// window's thread). A look's Fx.Section hosts it as content (spec §8.1: a kept element in a swappable template), so
    /// a change of look that draws the section anew re-hosts the same cards and keeps what is in them.</summary>
    public CardList Cards => _cards ??= new CardList { ItemsSource = Items, Focusable = false };

    /// <summary>A section whose every card is hidden disappears with its title.</summary>
    public bool IsVisible => Items.Any(i => i.IsVisible);
    public void RaiseVisibility() { foreach (var i in Items) i.RaiseVisibility(); Raise(nameof(IsVisible)); }
}

public sealed class PageVM : ObservableObject
{
    public PageVM(PageDef page, SettingsContext ctx)
    {
        Page = page;
        Sections = page.Sections.Select((s, i) => new SectionVM(s, s.Items.Select(item => Create(item, ctx)).ToList(), i + 1)).ToList();
        foreach (var item in Items) item.Changed += RefreshVisibility;
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

    private void RefreshVisibility() { foreach (var s in Sections) s.RaiseVisibility(); }

    private static ItemVM Create(PageItem item, SettingsContext ctx) => item switch
    {
        SettingItem si => new SettingVM(si.Setting, ctx),
        ActionRow ar => new ActionRowVM(ar, ctx),
        InfoItem ii => new InfoVM(ii, ctx),
        EditorItem ei => new EditorVM(ei, ctx),
        _ => throw new NotSupportedException(item.GetType().Name),
    };
}
