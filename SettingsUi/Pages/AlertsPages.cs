using System;
using System.Collections.Generic;
using System.Globalization;
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
    public const string CustomId = "alerts.custom";
    /// <summary>The event table: where event search results land.</summary>
    public const string EventTableId = "alerts.events.table";

    public static IReadOnlyList<PageDef> All => new[] { Events(), Look(), Sounds(), Hub(), Custom() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(EventsId, "logs", new[]
        {
            "alerts.events.chatLog", "alerts.events.chatFolder", "alerts.events.gameLog", "alerts.events.gameFolder",
        }),
        new HelpTopic(EventsId, "combat", new[] { "alerts.events.pve", "alerts.events.underFire", "alerts.events.underFireTime" }),
        new HelpTopic(EventsId, "table", new[] { EventTableId, "alerts.events.repeatWait", "alerts.events.repeatBySeverity" }),
        new HelpTopic(EventsId, "straggler", new[] { "alerts.events.stragglerWait" }),
        new HelpTopic(EventsId, "snoozes", new[] { SnoozesEditor.ItemId, "alerts.events.keepSnoozes" }),
        new HelpTopic(LookId, "thumbnail", new[]
        {
            "alerts.look.pulse", "alerts.look.borderWidth", "alerts.look.badge", "alerts.look.text", "alerts.look.textPosition",
        }),
        new HelpTopic(LookId, "severity", new[] { "alerts.look.severityTable" }),
        new HelpTopic(LookId, "repeats", new[]
        {
            "alerts.look.waitBySeverity", "alerts.look.waitCritical", "alerts.look.waitWarning", "alerts.look.waitInfo",
        }),
        new HelpTopic(LookId, "colorBlind", new[] { "alerts.look.colorBlind" }),
        new HelpTopic(SoundsId, "sounds", new[] { "alerts.sounds.enabled", "alerts.sounds.volume", "alerts.sounds.table" }),
        new HelpTopic(SoundsId, "dnd", new[]
        {
            "alerts.sounds.dndHotkey", "alerts.sounds.dndCritical", "alerts.sounds.quietHours", "alerts.sounds.quietFrom", "alerts.sounds.quietTo",
            DoNotDisturbStateEditor.ItemId,
        }),
        new HelpTopic(SoundsId, "escalation", new[] { "alerts.sounds.escalate", "alerts.sounds.escalateAfter", "alerts.sounds.escalateRepeats" }),
        new HelpTopic(HubId, "hub", new[] { "alerts.hub.enabled", "alerts.hub.duration", "alerts.hub.direction", "alerts.hub.skipActive" }),
        new HelpTopic(HubId, "hiding", new[] { "alerts.hub.autoHide", "alerts.hub.autoHideAfter", "alerts.hub.positionRow" }),
        new HelpTopic(HubId, "history", new[] { "alerts.hub.keepHistory", "alerts.hub.historyDays", "alerts.hub.historyFolder" }),
        new HelpTopic(HubId, "historyWindow", new[] { "alerts.hub.historyRow" }),
        new HelpTopic(CustomId, "rules", new[] { CustomRulesEditor.ItemId }),
        new HelpTopic(CustomId, "match", Array.Empty<string>()),
        new HelpTopic(CustomId, "chat", Array.Empty<string>()),
        new HelpTopic(CustomId, "test", Array.Empty<string>()),
        new HelpTopic(CustomId, "file", new[] { "alerts.custom.file" }),
    };

    private static SettingItem Toggle(string id, Expression<Func<AppSettings, bool>> prop, params string[] replaces) =>
        new(Setting.Toggle(id, prop) with { Replaces = replaces });

    private static PageDef Events() => new(EventsId, NavGroup.Alerts, new[]
    {
        new SectionDef("alerts.events.logs", new PageItem[]
        {
            // App passes the four settings below to the log monitor on Applied (LogMonitorService.Reconfigure, IDEAS P30).
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
            // App pushes it to the log monitor on Applied (SetCooldown). Every alert waits it, unless Quiet time per
            // severity is on (Look & severity, IDEAS P28); the old SeverityCooldowns never match.
            new SettingItem(Setting.Number("alerts.events.repeatWait", s => s.AlertCooldown, 0, 600, "L.S.Unit.Sec") with
            {
                ShowIf = s => !s.AlertCooldownBySeverity,
            }),
            new InfoItem("alerts.events.repeatBySeverity", InfoSeverity.Info) { ShowIf = s => s.AlertCooldownBySeverity },
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
            // IDEAS #5: App's one-second look reads it (StragglerWatch.Wait). The event's own switch is its row in the
            // table above: "Left behind", under Travel.
            new SettingItem(Setting.Number("alerts.events.stragglerWait", s => s.StragglerWaitSeconds,
                StragglerWatch.MinWaitSeconds, StragglerWatch.MaxWaitSeconds, "L.S.Unit.Sec")),
        }),
        // IDEAS #30: what is snoozed for one character right now, with a way to end it, and whether the timed snoozes
        // outlive a restart. App follows the switch on Applied (the snooze file is written or removed).
        new SectionDef("alerts.events.snoozes", new PageItem[]
        {
            new EditorItem(SnoozesEditor.ItemId, ctx => new SnoozesEditor(ctx)),
            new SettingItem(Setting.Toggle("alerts.events.keepSnoozes", s => s.KeepAlertSnoozes)),
        }),
    });

    private static PageDef Look() => new(LookId, NavGroup.Alerts, new[]
    {
        new SectionDef("alerts.look.thumbnail", new PageItem[]
        {
            // ResolveAlertFlashColor reads it at every flash tick.
            new SettingItem(Setting.Custom("alerts.look.pulse", SettingKind.Slider,
                s => AlertFields.PulsePercent(s.AlertOpacityPercent),
                (s, v) => s.AlertOpacityPercent = (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.AlertOpacityPercent" }) with
            {
                Min = AlertFields.PulseMin, Max = AlertFields.PulseMax, Step = AlertFields.PulseStep, UnitKey = "L.S.Unit.Percent",
                Replaces = new[] { "SliderAlertOpacity" },
            }),
            // ApplyAlertBorder reads it at every flash tick; 0 = no band (the old box clamped to 0–20 too).
            new SettingItem(Setting.Number("alerts.look.borderWidth", s => s.AlertBorderThickness, 0, 20, "L.S.Unit.Px") with
            {
                Replaces = new[] { "TxtAlertBorderThickness" },
            }),
            Toggle("alerts.look.badge", s => s.ShowAlertBadgeOnThumbnails, "ChkAlertBadgeOnThumbnails"),
            Toggle("alerts.look.text", s => s.ShowAlertTextOnThumbnails, "ChkAlertText"),
            // Placed by the per-thumbnail apply (ReapplySettings → SetAlertTextPosition).
            new SettingItem(Setting.Custom("alerts.look.textPosition", SettingKind.Choice,
                s => AlertFields.TextPosition(s.AlertTextPosition),
                (s, v) => s.AlertTextPosition = v as string ?? "Bottom",
                binds: new[] { "AppSettings.AlertTextPosition" }, apply: ApplyScope.Thumbnails) with
            {
                Options = ThumbnailFields.AnchorOptions,
                ShowIf = s => s.ShowAlertTextOnThumbnails,
                Replaces = new[] { "CmbAlertTextPos" },
            }),
        }),
        new SectionDef("alerts.look.severity", new PageItem[]
        {
            new EditorItem("alerts.look.severityTable", ctx => new SeverityTableEditor(ctx))
            {
                Binds = new[] { "AppSettings.SeverityColors", "AppSettings.SeverityFlashRates", "AppSettings.SeverityTrayNotify" },
            },
        }),
        new SectionDef("alerts.look.repeats", new PageItem[]
        {
            // App passes them to the log monitor on Applied (LogMonitorService.SeverityWaits, IDEAS P28). New keys: a
            // file without them keeps every alert on the one quiet time (Events).
            new SettingItem(Setting.Toggle("alerts.look.waitBySeverity", s => s.AlertCooldownBySeverity)),
            Wait("alerts.look.waitCritical", s => s.AlertCooldownCritical),
            Wait("alerts.look.waitWarning", s => s.AlertCooldownWarning),
            Wait("alerts.look.waitInfo", s => s.AlertCooldownInfo),
        }),
        new SectionDef("alerts.look.accessibility", new PageItem[]
        {
            new EditorItem("alerts.look.colorBlind", ctx => new ColorBlindEditor(ctx))
            {
                Binds = ColorBlindSwitch.Binds,
                Replaces = new[] { "BtnColorBlind" },
            },
        }),
    });

    /// <summary>One severity's quiet time, shown while Quiet time per severity is on.</summary>
    private static SettingItem Wait(string id, Expression<Func<AppSettings, int>> prop) =>
        new(Setting.Number(id, prop, 0, 600, "L.S.Unit.Sec") with { ShowIf = s => s.AlertCooldownBySeverity });

    /// <summary>Sound files MediaPlayer plays (as Clients › Hotkeys' wrap-around sound).</summary>
    public static string SoundFilter =>
        LocalizationService.Str("L.S.Filter.Sounds", "Sound files") + " (*.wav, *.mp3)|*.wav;*.mp3|*.*|*.*";

    private static bool HubOn(AppSettings s) => s.AlertHubEnabled;

    /// <summary>Old OnResetHubPosition only cleared the saved place; the hub moved at the next start. Now it moves
    /// at once (AlertHub.ResetPosition, Task 1) when the window has the hub.</summary>
    public static void ResetHubPosition(SettingsContext ctx)
    {
        ctx.S.AlertHubX = 0;
        ctx.S.AlertHubY = 0;
        ctx.Services.AlertHub?.ResetPosition();
        ctx.Applier.Request(ApplyScope.Save);
        ctx.Host.Toast(SettingsContext.Str("L.S.AlertHub.ResetDone"));
    }

    /// <summary>A time of day on the PC's clock ("23:00"), shown while the quiet hours are on. What can't be read as a
    /// time is not stored.</summary>
    private static SettingItem TimeOfDay(string id, Expression<Func<AppSettings, string>> prop) =>
        new(Setting.Text(id, prop) with
        {
            ShowIf = s => s.QuietHoursEnabled,
            Validate = v => DoNotDisturb.TryParseTime(v as string, out _) ? null : "L.S.Err.Time",
        });

    private static PageDef Sounds() => new(SoundsId, NavGroup.Alerts, new[]
    {
        new SectionDef("alerts.sounds.main", new PageItem[]
        {
            // App.PlayAlertSound reads both at every alert. The switch is Home's "Play a sound" too.
            Toggle("alerts.sounds.enabled", s => s.EnableAlertSounds, "ChkEnableSounds"),
            new SettingItem(Setting.Custom("alerts.sounds.volume", SettingKind.Slider,
                s => Math.Clamp(s.AlertSoundVolume, 0, 100),
                (s, v) => s.AlertSoundVolume = (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.AlertSoundVolume" }) with
            {
                Min = 0, Max = 100, Step = 1, UnitKey = "L.S.Unit.Percent",
                Replaces = new[] { "TxtMasterVolume" },
            }),
        }),
        // IDEAS #32: do-not-disturb mutes the sounds and nothing else. App.PlayAlertSound asks at every alert, and App
        // registers the hotkey again on Applied.
        new SectionDef("alerts.sounds.dnd", new PageItem[]
        {
            new SettingItem(Setting.Hotkey("alerts.sounds.dndHotkey", s => s.DoNotDisturbHotkey)),
            // A line that says so while do-not-disturb is on right now (App's answer, AppServices.DoNotDisturbOn), and
            // nothing while it is off. Not the section's first item: a search for the section lands on the card above.
            new EditorItem(DoNotDisturbStateEditor.ItemId, ctx => new DoNotDisturbStateEditor(ctx)),
            new SettingItem(Setting.Toggle("alerts.sounds.dndCritical", s => s.DoNotDisturbCriticalPlays)),
            new SettingItem(Setting.Toggle("alerts.sounds.quietHours", s => s.QuietHoursEnabled)),
            TimeOfDay("alerts.sounds.quietFrom", s => s.QuietHoursFrom),
            TimeOfDay("alerts.sounds.quietTo", s => s.QuietHoursTo),
        }),
        // IDEAS #29: App's one-second look reads all three (AlertEscalation.Due), and starts or stops on Applied.
        new SectionDef("alerts.sounds.escalation", new PageItem[]
        {
            new SettingItem(Setting.Toggle("alerts.sounds.escalate", s => s.EscalationEnabled)),
            new SettingItem(Setting.Number("alerts.sounds.escalateAfter", s => s.EscalationAfterSeconds,
                AlertEscalation.MinAfterSeconds, AlertEscalation.MaxAfterSeconds, "L.S.Unit.Sec") with
            {
                ShowIf = s => s.EscalationEnabled,
            }),
            new SettingItem(Setting.Number("alerts.sounds.escalateRepeats", s => s.EscalationRepeats,
                AlertEscalation.MinRepeats, AlertEscalation.MaxRepeats) with
            {
                ShowIf = s => s.EscalationEnabled,
            }),
        }),
        new SectionDef("alerts.sounds.list", new PageItem[]
        {
            new EditorItem("alerts.sounds.table", ctx => new SoundTableEditor(ctx))
            {
                Binds = new[] { "AppSettings.AlertSoundFile", "AppSettings.AlertSounds", "AppSettings.SoundCooldowns" },
            },
        }),
    });

    private static PageDef Hub() => new(HubId, NavGroup.Alerts, new[]
    {
        new SectionDef("alerts.hub.main", new PageItem[]
        {
            // App shows or hides the hub on Applied.
            Toggle("alerts.hub.enabled", s => s.AlertHubEnabled, "ChkAlertHub"),
            // App pushes both to the hub on Applied (AlertHub.ApplySettings, Task 1).
            new SettingItem(Setting.Number("alerts.hub.duration", s => s.AlertToastDuration, 1, 120, "L.S.Unit.Sec") with
            {
                ShowIf = HubOn,
                Replaces = new[] { "TxtToastDuration" },
            }),
            new SettingItem(Setting.Custom("alerts.hub.direction", SettingKind.Segmented,
                s => Math.Clamp(s.AlertToastDirection, 0, 3),
                (s, v) => s.AlertToastDirection = Convert.ToInt32(v, CultureInfo.InvariantCulture),
                binds: new[] { "AppSettings.AlertToastDirection" }) with
            {
                Options = new[]
                {
                    new ChoiceOption(0, "L.S.Opt.DirUp"), new ChoiceOption(1, "L.S.Opt.DirRight"),
                    new ChoiceOption(2, "L.S.Opt.DirDown"), new ChoiceOption(3, "L.S.Opt.DirLeft"),
                },
                ShowIf = HubOn,
            }),
            // Read for every toast.
            new SettingItem(Setting.Toggle("alerts.hub.skipActive", s => s.SuppressAlertHubToastForActiveClient) with
            {
                ShowIf = HubOn,
                Replaces = new[] { "ChkSuppressToastActive" },
            }),
        }),
        new SectionDef("alerts.hub.hiding", new PageItem[]
        {
            // The hub reads both each time it would hide.
            new SettingItem(Setting.Toggle("alerts.hub.autoHide", s => s.AlertHubAutoHide) with
            {
                ShowIf = HubOn,
                Replaces = new[] { "ChkAlertHubAutoHide" },
            }),
            new SettingItem(Setting.Number("alerts.hub.autoHideAfter", s => s.AlertHubAutoHideSeconds, 1, 3600, "L.S.Unit.Sec") with
            {
                ShowIf = s => s.AlertHubEnabled && s.AlertHubAutoHide,
                Replaces = new[] { "TxtAlertHubAutoHideSeconds" },
            }),
            new ActionRow("alerts.hub.positionRow", new[]
            {
                new ActionDef("alerts.hub.resetPosition", ResetHubPosition) { Replaces = new[] { "OnResetHubPosition" } },
            }) { ShowIf = HubOn },
        }),
        // IDEAS #1. Shown whether the hub is on or not: an alert is kept either way. App passes both settings to the
        // history on Applied. New keys: a file without them loads on, 30 days.
        new SectionDef("alerts.hub.history", new PageItem[]
        {
            // The window is App's (one at a time); a settings window opened without the app (tests) has none to open.
            new ActionRow("alerts.hub.historyRow", new[]
            {
                new ActionDef("alerts.hub.openHistory", ctx => ctx.Services.OpenAlertHistory?.Invoke()),
            }),
            new SettingItem(Setting.Toggle("alerts.hub.keepHistory", s => s.AlertHistoryEnabled)),
            new SettingItem(Setting.Number("alerts.hub.historyDays", s => s.AlertHistoryDays,
                AlertHistory.MinKeepDays, AlertHistory.MaxKeepDays, "L.S.Unit.Days") with
            {
                ShowIf = s => s.AlertHistoryEnabled,
            }),
            new InfoItem("alerts.hub.historyFolder", InfoSeverity.Info) { Args = _ => new object[] { AppPaths.HistoryDir } },
        }),
    });

    /// <summary>IDEAS #8. The rules are in custom_alerts.json, not in the settings file: nothing on this page binds a
    /// setting, and nothing on it asks the settings file to be saved.</summary>
    private static PageDef Custom() => new(CustomId, NavGroup.Alerts, new[]
    {
        new SectionDef("alerts.custom.rules", new PageItem[]
        {
            new EditorItem(CustomRulesEditor.ItemId, ctx => new CustomRulesEditor(ctx)),
            new InfoItem("alerts.custom.file", InfoSeverity.Info) { Args = ctx => new object[] { ctx.CustomAlerts.FilePath } },
        }),
    });
}
