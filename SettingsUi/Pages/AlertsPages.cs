using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §5 Alerts: Events, Look &amp; severity, Sounds, Alert hub. App re-syncs the log monitor and the
/// alert hub on Applied, and the thumbnails read the rest at the next alert, so most cards only save.</summary>
public static class AlertsPages
{
    public const string EventsId = "alerts.events";
    public const string LookId = "alerts.look";
    public const string SoundsId = "alerts.sounds";
    public const string HubId = "alerts.hub";
    /// <summary>The event table: where event search results land.</summary>
    public const string EventTableId = "alerts.events.table";

    public static IReadOnlyList<PageDef> All => new[] { Events() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(EventsId, "logs", new[]
        {
            "alerts.events.restartNote", "alerts.events.chatLog", "alerts.events.chatFolder", "alerts.events.gameLog", "alerts.events.gameFolder",
        }),
        new HelpTopic(EventsId, "combat", new[] { "alerts.events.pve", "alerts.events.underFire", "alerts.events.underFireTime" }),
        new HelpTopic(EventsId, "table", new[] { EventTableId, "alerts.events.repeatWait" }),
    };

    private static SettingItem Toggle(string id, Expression<Func<AppSettings, bool>> prop, params string[] replaces) =>
        new(Setting.Toggle(id, prop) with { Replaces = replaces });

    private static PageDef Events() => new(EventsId, NavGroup.Alerts, new[]
    {
        new SectionDef("alerts.events.logs", new PageItem[]
        {
            // LogMonitorService.Start reads the four settings below once, at startup (ruling 5).
            new InfoItem("alerts.events.restartNote", InfoSeverity.Info),
            Toggle("alerts.events.chatLog", s => s.EnableChatLogMonitoring, "ChkChatLogMon"),
            new SettingItem(Setting.Folder("alerts.events.chatFolder", s => s.ChatLogDirectory) with
            {
                ShowIf = s => s.EnableChatLogMonitoring,
                Replaces = new[] { "TxtChatLogDir", "OnBrowseChatLog" },
            }),
            Toggle("alerts.events.gameLog", s => s.EnableGameLogMonitoring, "ChkGameLogMon"),
            new SettingItem(Setting.Folder("alerts.events.gameFolder", s => s.GameLogDirectory) with
            {
                ShowIf = s => s.EnableGameLogMonitoring,
                Replaces = new[] { "TxtGameLogDir", "OnBrowseGameLog" },
            }),
        }),
        new SectionDef("alerts.events.combat", new PageItem[]
        {
            // App pushes PveMode to the log monitor on Applied.
            Toggle("alerts.events.pve", s => s.PveMode, "ChkPveMode"),
            // ThumbnailManager.SignalUnderFire reads both at every hit.
            Toggle("alerts.events.underFire", s => s.EnableUnderFireIndicator, "ChkUnderFire"),
            new SettingItem(Setting.Number("alerts.events.underFireTime", s => s.UnderFireTimeoutSeconds, 1, 600, "L.S.Unit.Sec") with
            {
                ShowIf = s => s.EnableUnderFireIndicator,
                Replaces = new[] { "TxtUnderFireTimeout" },
            }),
        }),
        new SectionDef("alerts.events.repeats", new PageItem[]
        {
            // App pushes it to the log monitor on Applied (SetCooldown). It is the wait every event uses: the
            // severity cooldowns never match (ruling 1, IDEAS P28).
            new SettingItem(Setting.Number("alerts.events.repeatWait", s => s.AlertCooldown, 0, 600, "L.S.Unit.Sec")),
        }),
        new SectionDef("alerts.events.list", new PageItem[]
        {
            new EditorItem(EventTableId, ctx => new EventTableEditor(ctx))
            {
                Binds = new[]
                {
                    "AppSettings.AlertColors", "AppSettings.AlertDurations", "AppSettings.BadgeOnThumbnailAlertTypes",
                    "AppSettings.AlertSolidBorder", "AppSettings.EnabledAlertTypes",
                },
            },
        }),
    });
}
