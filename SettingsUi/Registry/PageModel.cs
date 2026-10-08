using System;
using System.Collections.Generic;
using EveMultiPreview.Models;
using FrameworkElement = System.Windows.FrameworkElement;

namespace EveMultiPreview.SettingsUi;

/// <summary>Navigation groups in the order the pane shows them (spec §4, §5).</summary>
public enum NavGroup { Home, Clients, Thumbnails, Alerts, Performance, Tools, App }

/// <summary>A navigation page. Title "L.S.Page.{Id}", description "L.S.Page.{Id}.Desc".</summary>
public sealed record PageDef(string Id, NavGroup Group, IReadOnlyList<SectionDef> Sections)
{
    public string TitleKey => "L.S.Page." + Id;
    public string DescKey => "L.S.Page." + Id + ".Desc";
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
}
