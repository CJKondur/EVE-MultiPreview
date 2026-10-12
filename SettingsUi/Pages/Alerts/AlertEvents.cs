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

    /// <summary>The events the log monitor raises from a log line. GoldenLogTests holds each of them to a line from a
    /// real log, so an event that no log line raises is not added here: it goes in <see cref="Derived"/>.</summary>
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

    /// <summary>The event MultiPreview works out from what it knows instead of reading it from one log line: a group
    /// member a jump wave left behind (IDEAS #5, StragglerWatch). App raises it, through the same dispatcher.</summary>
    public static IReadOnlyList<AlertEventDef> Derived { get; } = new[]
    {
        new AlertEventDef(Services.StragglerWatch.EventKey, Services.StragglerWatch.Severity, "travel"),
    };

    /// <summary>The events the log monitor reads from a log line only once the player has switched them on
    /// (IDEAS #4): for these a missing key is off (AlertFields.IsOn, EwarLines.Bits). Each is read from a line of a
    /// real English log, and the fixtures hold that line; but what the pinned fixture expects of it is "nothing",
    /// which is what a player who has not switched the event on gets. So they are not in <see cref="All"/>, whose
    /// every event that fixture must expect.</summary>
    public static IReadOnlyList<AlertEventDef> OptIn { get; } = new[]
    {
        new AlertEventDef(Services.EwarLines.Neutralized, Services.EwarLines.NeutralizedSeverity, "combat"),
        new AlertEventDef(Services.EwarLines.Jammed, Services.EwarLines.JammedSeverity, "combat"),
    };

    public static bool IsOptIn(string key) => key == Services.EwarLines.Neutralized || key == Services.EwarLines.Jammed;

    /// <summary>Every built-in event as the settings window lists them: by table group, the log monitor's first.</summary>
    public static IReadOnlyList<AlertEventDef> Listed { get; } =
        Groups.SelectMany(group => All.Concat(OptIn).Concat(Derived).Where(e => e.Group == group)).ToList();

    public static AlertEventDef Find(string key) => Listed.First(e => e.Key == key);

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
        return Listed.Select(e => new SearchEntry(SearchKind.Event,
            resolve(e.LabelKey) is { Length: > 0 } text ? text : e.Key, english(e.LabelKey),
            path, AlertsPages.EventsId, AlertsPages.EventTableId)).ToList();
    }
}

/// <summary>Reads and writes of the Alerts cards, each on one existing dictionary entry, with the runtime's rules
/// for a missing key. Reading never writes.</summary>
public static class AlertFields
{
    // ── Event table (EnabledAlertTypes, BadgeOnThumbnailAlertTypes, AlertSolidBorder, AlertDurations, AlertColors) ──

    /// <summary>LogMonitorService: a missing key is on (follow_warp and regroup have no default key). Three events are
    /// the other way round, off until switched on: "Left behind" (StragglerWatch.IsOn), which MultiPreview raises
    /// itself, and the two of IDEAS #4 (AlertEvents.OptIn), for which the log monitor has the same rule (EwarLines.Bits).</summary>
    public static bool IsOn(AppSettings s, string key) =>
        key == Services.StragglerWatch.EventKey ? Services.StragglerWatch.IsOn(s)
        : AlertEvents.IsOptIn(key) ? s.EnabledAlertTypes.TryGetValue(key, out var chosen) && chosen
        : !s.EnabledAlertTypes.TryGetValue(key, out var on) || on;
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

    /// <summary>The severity's stored colour, else what ThumbnailManager.ResolveAlertFlashColor paints: AlertFlashColor, then red.</summary>
    public static string SeverityColour(AppSettings s, string severity) =>
        s.SeverityColors.TryGetValue(severity, out var c) && !string.IsNullOrEmpty(c) ? c
        : !string.IsNullOrEmpty(s.AlertFlashColor) ? s.AlertFlashColor : "#FF0000";

    // ── Look & severity (AlertOpacityPercent, AlertTextPosition, SeverityFlashRates, SeverityTrayNotify, SeverityColors) ──

    public const double PulseMin = 10, PulseMax = 100, PulseStep = 5;

    /// <summary>The old slider's 10–100 % in steps of 5, clamped and rounded, so the slider never moves a stored value.</summary>
    public static double PulsePercent(int stored) => Math.Clamp(Math.Round(stored / PulseStep) * PulseStep, PulseMin, PulseMax);

    /// <summary>What TextOverlayWindow paints: a missing value is "Bottom"; an empty or unknown one falls to top left.</summary>
    public static string TextPosition(string? stored) => stored == null ? "Bottom" : ThumbnailFields.Anchor(stored);

    public const int FlashMin = 100, FlashMax = 2000, FlashStep = 50;

    /// <summary>ThumbnailManager.FlashAlertTick's rate: the stored one, else 200 / 500 / 1000 ms.</summary>
    public static int FlashRate(AppSettings s, string severity) =>
        s.SeverityFlashRates.TryGetValue(severity, out var ms) ? ms
        : severity == AlertEvents.Critical ? 200 : severity == AlertEvents.Warning ? 500 : 1000;

    /// <summary>The slider's place for a stored rate: whole steps of 50 within 100–2000.</summary>
    public static double FlashSlider(int ms) => Math.Clamp(Math.Round(ms / (double)FlashStep) * FlashStep, FlashMin, FlashMax);

    public static void SetFlashRate(AppSettings s, string severity, int ms) => s.SeverityFlashRates[severity] = ms;

    /// <summary>The App's toast gate (SeverityTrayNotify): a missing key means no toast.</summary>
    public static bool HubToast(AppSettings s, string severity) =>
        s.SeverityTrayNotify.TryGetValue(severity, out var on) && on;

    public static void SetHubToast(AppSettings s, string severity, bool on) => s.SeverityTrayNotify[severity] = on;

    public static void SetSeverityColour(AppSettings s, string severity, string picked) =>
        s.SeverityColors[severity] = ThumbnailFields.StoreColour(picked);
}
