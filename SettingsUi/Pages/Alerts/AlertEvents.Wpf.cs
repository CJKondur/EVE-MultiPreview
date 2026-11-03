using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>The settings window's parts of <see cref="AlertEvents"/>.</summary>
public static partial class AlertEvents
{
    /// <summary>The event's name in the current language (English fallback), for accessible names and messages.</summary>
    public static string Name(string key) => Views.AlertHub.EventDisplayName(key);

    /// <summary>The severity as the look draws it (Look.Severity, UI styles plan S4): by default its name in its colour
    /// token; a look may add a shape or a tag. The words carry the meaning in every look, so colour never stands alone
    /// (spec §7). The content is the severity's id; one the template does not know is drawn as info.</summary>
    public static FrameworkElement SeverityText(string severity)
    {
        var shown = new ContentPresenter { Content = severity, VerticalAlignment = VerticalAlignment.Center };
        shown.SetResourceReference(ContentPresenter.ContentTemplateProperty, "Look.Severity");
        return shown;
    }

    /// <summary>Spec §6: search covers events. Each entry opens Alerts › Events at the table.</summary>
    public static IReadOnlyList<SearchEntry> SearchEntries(Func<string, string> resolve, Func<string, string> english)
    {
        var path = resolve("L.S.Nav.Alerts") + " › " + resolve("L.S.Page." + AlertsPages.EventsId);
        return Listed.Select(e => new SearchEntry(SearchKind.Event,
            resolve(e.LabelKey) is { Length: > 0 } text ? text : e.Key, english(e.LabelKey),
            path, AlertsPages.EventsId, AlertsPages.EventTableId)).ToList();
    }
}
