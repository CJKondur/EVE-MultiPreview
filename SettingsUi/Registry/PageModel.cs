using System;
using System.Collections.Generic;
using EveMultiPreview.Models;
using FrameworkElement = System.Windows.FrameworkElement;

namespace EveMultiPreview.SettingsUi;

/// <summary>Navigation groups in the order the pane shows them (spec §4, §5).</summary>
public enum NavGroup { Home, Clients, Thumbnails, Alerts, Performance, Tools, App }

/// <summary>A page. Title "L.S.Page.{KeyId}", description "L.S.Page.{KeyId}.Desc", help
/// "L.Help.{HelpId}.*". A page built at run time (a character page) sets Title, ParentId and Template.</summary>
public sealed record PageDef(string Id, NavGroup Group, IReadOnlyList<SectionDef> Sections)
{
    /// <summary>Shown instead of the TitleKey resource: a character's name, never translated.</summary>
    public string? Title { get; init; }

    /// <summary>A page opened from another page instead of the navigation pane (a character page
    /// from Characters). The breadcrumb names and opens the parent, and the parent stays selected.</summary>
    public string? ParentId { get; init; }

    /// <summary>Pages built at run time share one set of keys and one help page: "character"
    /// for every character page.</summary>
    public string? Template { get; init; }

    public string KeyId => Template ?? Id;
    public string TitleKey => "L.S.Page." + KeyId;
    public string DescKey => "L.S.Page." + KeyId + ".Desc";
    public string HelpId => KeyId;
}

/// <summary>A titled block of cards. Title "L.S.Sec.{Id}". Ids are unique across all pages.</summary>
public sealed record SectionDef(string Id, IReadOnlyList<PageItem> Items)
{
    public string TitleKey => "L.S.Sec." + Id;
}

public abstract record PageItem(string Id);

public sealed record SettingItem(SettingDescriptor Setting) : PageItem(Setting.Id);

/// <summary>A card with a label ("L.S.{Id}", optional "L.S.{Id}.Desc") and a row of buttons.</summary>
public sealed record ActionRow(string Id, IReadOnlyList<ActionDef> Actions) : PageItem(Id)
{
    public Func<AppSettings, bool>? ShowIf { get; init; }
    public string LabelKey => "L.S." + Id;
    public string DescKey => "L.S." + Id + ".Desc";
}

/// <summary>A button. Label "L.S.Act.{Id}". With Confirm, it first asks "L.S.Act.{Id}.Confirm"
/// (spec §6: destructive buttons ask for confirmation).</summary>
public sealed record ActionDef(string Id, Action<SettingsContext> Run)
{
    public bool Confirm { get; init; }
    public bool Destructive { get; init; }
    public IReadOnlyList<string> Replaces { get; init; } = Array.Empty<string>();
    public string LabelKey => "L.S.Act." + Id;
    public string ConfirmKey => "L.S.Act." + Id + ".Confirm";
}

public enum InfoSeverity { Info, Warning, Error }

/// <summary>An info line between cards. Text "L.S.{Id}" formatted with Text(ctx) when given.</summary>
public sealed record InfoItem(string Id, InfoSeverity Severity) : PageItem(Id)
{
    public Func<AppSettings, bool>? ShowIf { get; init; }
    /// <summary>Arguments for the {0}.. placeholders of the text (e.g. the app version).</summary>
    public Func<SettingsContext, object[]>? Args { get; init; }
    public string TextKey => "L.S." + Id;
}

/// <summary>A hand-built editor for what generic cards can't express (spec §8.2).</summary>
public sealed record EditorItem(string Id, Func<SettingsContext, FrameworkElement> Create) : PageItem(Id)
{
    public IReadOnlyList<string> Binds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Replaces { get; init; } = Array.Empty<string>();
    /// <summary>The editor shows only while this returns true (spec §6 conditional rows).</summary>
    public Func<AppSettings, bool>? ShowIf { get; init; }
}
