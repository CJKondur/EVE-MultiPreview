using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

/// <summary>One alert event: its key (the string LogMonitorService raises), its severity and its table group.</summary>
public sealed record AlertEventDef(string Key, string Severity, string Group)
{
    /// <summary>The event's name: the hub and the thumbnail text use the same key (AlertHub.EventDisplayName).</summary>
    public string LabelKey => "L.AlertEvt." + Key;
}

/// <summary>The alert events, defined once for the settings window (spec §5 Alerts). The severities match the
/// LogMonitorService.TriggerAlert call sites; the runtime keeps its own literals.</summary>
public static class AlertEvents
{
    public const string Critical = "critical", Warning = "warning", Info = "info";

    public static IReadOnlyList<string> Severities { get; } = new[] { Critical, Warning, Info };

    /// <summary>Table groups in order; the mining names ("Cargo full") need their heading.</summary>
    public static IReadOnlyList<string> Groups { get; } = new[] { "combat", "fleet", "travel", "mining" };

    public static IReadOnlyList<AlertEventDef> All { get; } = new[]
    {
        new AlertEventDef("attack", Critical, "combat"),
        new AlertEventDef("warp_scramble", Critical, "combat"),
        new AlertEventDef("decloak", Critical, "combat"),
        new AlertEventDef("fleet_invite", Warning, "fleet"),
        new AlertEventDef("convo_request", Warning, "fleet"),
        new AlertEventDef("follow_warp", Warning, "fleet"),
        new AlertEventDef("regroup", Warning, "fleet"),
        new AlertEventDef("system_change", Info, "travel"),
        new AlertEventDef("mine_cargo_full", Warning, "mining"),
        new AlertEventDef("mine_asteroid_depleted", Info, "mining"),
        new AlertEventDef("mine_crystal_broken", Warning, "mining"),
        new AlertEventDef("mine_module_stopped", Info, "mining"),
    };

    public static AlertEventDef Find(string key) => All.First(e => e.Key == key);

    /// <summary>The event's name in the current language (English fallback), for accessible names and messages.</summary>
    public static string Name(string key) => Views.AlertHub.EventDisplayName(key);

    public static string SeverityKey(string severity) => "L.S.Sev." + severity;

    /// <summary>The severity's name in its colour token. The text carries the meaning, so colour never stands
    /// alone (spec §7).</summary>
    public static TextBlock SeverityText(string severity)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Loc.SetKey(t, SeverityKey(severity));
        t.SetResourceReference(TextBlock.ForegroundProperty,
            severity == Critical ? "Fx.CriticalBrush" : severity == Warning ? "Fx.WarningBrush" : "Fx.InfoBrush");
        return t;
    }

    /// <summary>Spec §6: search covers events. Each entry opens Alerts › Events at the table.</summary>
    public static IReadOnlyList<SearchEntry> SearchEntries(Func<string, string> resolve, Func<string, string> english)
    {
        var path = resolve("L.S.Nav.Alerts") + " › " + resolve("L.S.Page." + AlertsPages.EventsId);
        return All.Select(e => new SearchEntry(SearchKind.Event,
            resolve(e.LabelKey) is { Length: > 0 } text ? text : e.Key, english(e.LabelKey),
            path, AlertsPages.EventsId, AlertsPages.EventTableId)).ToList();
    }
}

/// <summary>Reads and writes of the Alerts cards, each on one existing dictionary entry, with the runtime's rules
/// for a missing key. Reading never writes.</summary>
public static class AlertFields
{
    // ── Event table (EnabledAlertTypes, BadgeOnThumbnailAlertTypes, AlertSolidBorder, AlertDurations, AlertColors) ──

    /// <summary>LogMonitorService: a missing key is on (follow_warp and regroup have no default key).</summary>
    public static bool IsOn(AppSettings s, string key) => !s.EnabledAlertTypes.TryGetValue(key, out var on) || on;
    public static void SetOn(AppSettings s, string key, bool on) => s.EnabledAlertTypes[key] = on;

    /// <summary>ThumbnailManager.IncrementAlertBadge: a missing key is on.</summary>
    public static bool Badge(AppSettings s, string key) => !s.BadgeOnThumbnailAlertTypes.TryGetValue(key, out var on) || on;
    public static void SetBadge(AppSettings s, string key, bool on) => s.BadgeOnThumbnailAlertTypes[key] = on;

    /// <summary>ThumbnailManager.ApplyAlertBorder paints info alerts steady whatever is stored.</summary>
    public static bool Steady(AppSettings s, AlertEventDef e) => e.Severity == AlertEvents.Info || s.AlertSolidBorder.GetValueOrDefault(e.Key);
    public static void SetSteady(AppSettings s, string key, bool on) => s.AlertSolidBorder[key] = on;

    public const int MinLasts = 0, MaxLasts = 3600;

    /// <summary>Seconds the alert stays, or null for the severity's time (ThumbnailManager.FlashAlertTick treats a
    /// negative value as missing). 0 = until you switch to that client.</summary>
    public static int? Lasts(AppSettings s, string key) => s.AlertDurations.TryGetValue(key, out var d) && d >= 0 ? d : null;

    public static void SetLasts(AppSettings s, string key, int? seconds)
    {
        if (seconds == null) s.AlertDurations.Remove(key);
        else s.AlertDurations[key] = seconds.Value;
    }

    /// <summary>Empty = the severity's time; else whole seconds 0–3600. Returns the L.* key of an error, or null.</summary>
    public static string? CheckLasts(string? text, out int? seconds)
    {
        seconds = null;
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return null;
        if (ThumbnailFields.TryWhole(t, out var v) && v >= MinLasts && v <= MaxLasts) { seconds = v; return null; }
        return "L.S.Events.LastsError";
    }

    /// <summary>ThumbnailManager.ResolveAlertFlashColor's order: the event's own colour, else its severity's.</summary>
    public static string Colour(AppSettings s, AlertEventDef e) =>
        HasOwnColour(s, e.Key) ? s.AlertColors[e.Key] : SeverityColour(s, e.Severity);

    public static bool HasOwnColour(AppSettings s, string key) =>
        s.AlertColors.TryGetValue(key, out var c) && !string.IsNullOrWhiteSpace(c);

    /// <summary>The picker's #RRGGBB stored as 0xrrggbb, the old picker's form (ThumbnailManager.ParseColor paints it).</summary>
    public static void SetColour(AppSettings s, string key, string picked) => s.AlertColors[key] = ThumbnailFields.StoreColour(picked);

    public static void ClearColour(AppSettings s, string key) => s.AlertColors.Remove(key);

    /// <summary>The severity's stored colour, else the old window's defaults.</summary>
    public static string SeverityColour(AppSettings s, string severity) =>
        s.SeverityColors.TryGetValue(severity, out var c) && !string.IsNullOrWhiteSpace(c) ? c
        : severity == AlertEvents.Critical ? "#FF0000" : severity == AlertEvents.Warning ? "#FFA500" : "#4A9EFF";
}
