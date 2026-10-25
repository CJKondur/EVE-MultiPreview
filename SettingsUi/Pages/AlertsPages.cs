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
        new HelpTopic(EventsId, "ewar", new[] { "alerts.events.ewarNote" }),
        new HelpTopic(EventsId, "straggler", new[] { "alerts.events.stragglerWait" }),
        new HelpTopic(EventsId, "combatWatch", new[]
        {
            "alerts.events.dpsThreshold", "alerts.events.dpsSeconds", "alerts.events.repsSeconds", "alerts.events.combatNote",
        }),
        new HelpTopic(EventsId, "memory", new[] { "alerts.events.memoryLimit", "alerts.events.memoryGrowth", "alerts.events.memoryMinutes" }),
        new HelpTopic(EventsId, "clientStops", new[] { "alerts.events.clientNote" }),
        new HelpTopic(EventsId, "idle", new[] { "alerts.events.idleMinutes" }),
        new HelpTopic(EventsId, "clientReady", Array.Empty<string>()),   // no card of its own: the event is a row of the table
        new HelpTopic(EventsId, "groups", new[] { GroupAlertsEditor.ItemId }),
        new HelpTopic(EventsId, "snoozes", new[] { SnoozesEditor.ItemId, "alerts.events.keepSnoozes" }),
        new HelpTopic(LookId, "thumbnail", new[]
        {
            "alerts.look.pulse", "alerts.look.borderWidth", "alerts.look.badge", "alerts.look.text", "alerts.look.textPosition",
        }),
        new HelpTopic(LookId, "enlarge", new[]
        {
            "alerts.look.enlarge", "alerts.look.enlargeFrom", "alerts.look.enlargePercent", "alerts.look.enlargeSeconds",
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
        new HelpTopic(SoundsId, "speech", new[] { "alerts.sounds.speak", "alerts.sounds.speakFrom", "alerts.sounds.speechVolume", SpeechVoicesEditor.ItemId }),
        new HelpTopic(SoundsId, "groups", new[] { GroupSoundsEditor.ItemId }),
        new HelpTopic(HubId, "hub", new[] { "alerts.hub.enabled", "alerts.hub.duration", "alerts.hub.direction", "alerts.hub.skipActive" }),
        new HelpTopic(HubId, "hiding", new[] { "alerts.hub.autoHide", "alerts.hub.autoHideAfter", "alerts.hub.positionRow" }),
        new HelpTopic(HubId, "windows", new[] { "alerts.hub.notify", "alerts.hub.notifyFrom", NotificationCheckEditor.ItemId }),
        new HelpTopic(HubId, "push", new[] { "alerts.hub.push", "alerts.hub.pushFrom", "alerts.hub.pushMention", "alerts.hub.pushAway", PushWebhookEditor.ItemId }),
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
            // IDEAS #4: what a player must know before relying on "Capacitor neutralized" and "Jammed": off until
            // switched on, and read from an English game client only. Always shown: like the card below, a line
            // can't follow a switch inside the table.
            new InfoItem("alerts.events.ewarNote", InfoSeverity.Info),
            // IDEAS #5: the one-second look reads it (StragglerAlerts.Look, held to its range by StragglerWatch.Wait).
            // The event's own switch is its row in the table above: "Left behind", under Travel. The card is shown
            // whether that switch is on or not: a card can't follow a switch inside the table (only a card's own
            // change makes the page ask every ShowIf again), so its text says when the wait is used. It shows the
            // wait that is used, which is the stored number held to the range; a number outside it is not rewritten.
            new SettingItem(Setting.Number("alerts.events.stragglerWait", s => s.StragglerWaitSeconds,
                StragglerWatch.MinWaitSeconds, StragglerWatch.MaxWaitSeconds, "L.S.Unit.Sec") with
            {
                Get = s => Math.Clamp(s.StragglerWaitSeconds, StragglerWatch.MinWaitSeconds, StragglerWatch.MaxWaitSeconds),
            }),
            // IDEAS #27, #28: the log monitor reads the three at every hit while one of the two events is on
            // (CombatOptions.Of), so a change only saves. Shown whether the events are on or not, as the wait of "Left
            // behind" is: a card can't follow a switch inside the table, so the note under them says so. Each shows the
            // number that is used; a stored number outside the range is not rewritten.
            Combat("alerts.events.dpsThreshold", s => s.DpsAlertThreshold, CombatOptions.MinDps, CombatOptions.MaxDps, "L.S.Unit.Dps"),
            Combat("alerts.events.dpsSeconds", s => s.DpsAlertSeconds, CombatOptions.MinSeconds, CombatOptions.MaxSeconds, "L.S.Unit.Sec"),
            Combat("alerts.events.repsSeconds", s => s.RepsStoppedSeconds, CombatOptions.MinRepsSeconds, CombatOptions.MaxRepsSeconds, "L.S.Unit.Sec"),
            new InfoItem("alerts.events.combatNote", InfoSeverity.Info),
            // IDEAS #45: App reads the three at each look while the event is on (MemoryOptions.Of), so a change only saves.
            // Shown whether the event is on or not, as the combat numbers are.
            Combat("alerts.events.memoryLimit", s => s.MemoryAlertMb, MemoryOptions.MinLimitMb, MemoryOptions.MaxLimitMb, "L.S.Unit.Mb"),
            Combat("alerts.events.memoryGrowth", s => s.MemoryGrowthMb, MemoryOptions.MinGrowthMb, MemoryOptions.MaxGrowthMb, "L.S.Unit.Mb"),
            Combat("alerts.events.memoryMinutes", s => s.MemoryGrowthMinutes, MemoryOptions.MinMinutes, MemoryOptions.MaxMinutes, "L.S.Unit.Min"),
            // IDEAS #22: what a player must know before relying on "Client not responding" and "Client closed
            // unexpectedly": off until switched on in the table above, after 30 s frozen, and only an end with an error
            // code. Always shown, as ewarNote: a card can't follow a switch inside the table.
            new InfoItem("alerts.events.clientNote", InfoSeverity.Info),
            // IDEAS #7: App reads it on its half-second timer while "Character idle" is on (IdleWatch.Minutes), so a
            // change only saves. Shown whether the event is on or not, as the memory numbers are.
            Combat("alerts.events.idleMinutes", s => s.IdleAlertMinutes, IdleWatch.MinMinutes, IdleWatch.MaxMinutes, "L.S.Unit.Min"),
        }),
        // IDEAS #31: a group's own choice of which events are on. App hands it to the gate (AlertProfiles) and to the
        // log monitor on Applied.
        new SectionDef("alerts.events.groups", new PageItem[]
        {
            new EditorItem(GroupAlertsEditor.ItemId, ctx => new GroupAlertsEditor(ctx)) { Binds = new[] { "AppSettings.GroupAlertProfiles" } },
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
            // IDEAS #13: AlertEnlarge reads all four at every alert that was shown and at its tick (EnlargeOptions.Of),
            // so a change only saves. The two numbers are used held to their ranges, and each card shows the number
            // that is used: a stored number outside the range is not rewritten.
            new SettingItem(Setting.Toggle("alerts.look.enlarge", s => s.EnlargeOnAlert)),
            From("alerts.look.enlargeFrom", s => s.EnlargeOnAlertFrom, (s, v) => s.EnlargeOnAlertFrom = v, "AppSettings.EnlargeOnAlertFrom", s => s.EnlargeOnAlert),
            new SettingItem(Setting.Number("alerts.look.enlargePercent", s => s.EnlargeOnAlertPercent,
                EnlargeOptions.MinPercent, EnlargeOptions.MaxPercent, "L.S.Unit.Percent") with
            {
                Get = s => Math.Clamp(s.EnlargeOnAlertPercent, EnlargeOptions.MinPercent, EnlargeOptions.MaxPercent),
                ShowIf = s => s.EnlargeOnAlert,
            }),
            new SettingItem(Setting.Number("alerts.look.enlargeSeconds", s => s.EnlargeOnAlertSeconds,
                EnlargeOptions.MinSeconds, EnlargeOptions.MaxSeconds, "L.S.Unit.Sec") with
            {
                Get = s => Math.Clamp(s.EnlargeOnAlertSeconds, EnlargeOptions.MinSeconds, EnlargeOptions.MaxSeconds),
                ShowIf = s => s.EnlargeOnAlert,
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

    /// <summary>One of the three numbers of "Incoming DPS high" and "Reps stopped", shown held to its range.</summary>
    private static SettingItem Combat(string id, Expression<Func<AppSettings, int>> prop, int min, int max, string unit)
    {
        var read = prop.Compile();
        return new(Setting.Number(id, prop, min, max, unit) with { Get = s => Math.Clamp(read(s), min, max) });
    }

    /// <summary>One severity's quiet time, shown while Quiet time per severity is on.</summary>
    private static SettingItem Wait(string id, Expression<Func<AppSettings, int>> prop) =>
        new(Setting.Number(id, prop, 0, 600, "L.S.Unit.Sec") with { ShowIf = s => s.AlertCooldownBySeverity });

    /// <summary>From which severity a further way tells of an alert (IDEAS batch 6; AlertWays.Reaches): spoken
    /// alerts, Windows notifications, the enlarged thumbnail and push to Discord (IDEAS #2) each have a card with
    /// these three.</summary>
    public static IReadOnlyList<ChoiceOption> FromOptions { get; } = new[]
    {
        new ChoiceOption(0, "L.S.Opt.FromCritical"), new ChoiceOption(1, "L.S.Opt.FromWarning"), new ChoiceOption(2, "L.S.Opt.FromInfo"),
    };

    /// <summary>The three choices of the push card "Mention on critical alerts" (IDEAS #2, batch 30).</summary>
    public static IReadOnlyList<ChoiceOption> PushMentionOptions { get; } = new[]
    {
        new ChoiceOption(0, "L.S.Opt.PushMentionNone"), new ChoiceOption(1, "L.S.Opt.PushMentionHere"), new ChoiceOption(2, "L.S.Opt.PushMentionEveryone"),
    };

    /// <summary>A "from which severity" card. It shows the choice that is used: a stored number outside 0 to 2 is
    /// held to it, and not rewritten.</summary>
    private static SettingItem From(string id, Func<AppSettings, int> get, Action<AppSettings, int> set, string bind, Func<AppSettings, bool> showIf) =>
        new(Setting.Custom(id, SettingKind.Choice, s => Math.Clamp(get(s), 0, 2),
            (s, v) => set(s, Convert.ToInt32(v, CultureInfo.InvariantCulture)), binds: new[] { bind }) with
        {
            Options = FromOptions,
            ShowIf = showIf,
        });

    /// <summary>Sound files MediaPlayer plays (as Clients › Hotkeys' wrap-around sound).</summary>
    public static string SoundFilter =>
        LocalizationService.Str("L.S.Filter.Sounds", "Sound files") + " (*.wav, *.mp3)|*.wav;*.mp3|*.*|*.*";

    /// <summary>The toast key for a sound file the pickers refuse (CustomRuleCheck.LocalSound): a name Windows keeps for
    /// a device says so, since the file is on this PC; any other refusal says to pick a file on one of its drives.</summary>
    public static string SoundRefusedKey(string picked) =>
        CustomRuleCheck.IsDeviceNamed(picked) ? "L.S.Rules.SoundDeviceName" : "L.S.Rules.SoundNotLocal";

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
        // IDEAS #32: do-not-disturb holds the alert sounds and the voice, and nothing else. App.PlayAlertSound asks at
        // every alert, the voice asks for every sentence (SpokenWay), and App registers the hotkey again on Applied.
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
        // The two numbers are used held to their ranges, and each card shows the number that is used, as the wait
        // card of "Left behind" does: a stored number outside the range is not rewritten.
        new SectionDef("alerts.sounds.escalation", new PageItem[]
        {
            new SettingItem(Setting.Toggle("alerts.sounds.escalate", s => s.EscalationEnabled)),
            new SettingItem(Setting.Number("alerts.sounds.escalateAfter", s => s.EscalationAfterSeconds,
                AlertEscalation.MinAfterSeconds, AlertEscalation.MaxAfterSeconds, "L.S.Unit.Sec") with
            {
                Get = s => Math.Clamp(s.EscalationAfterSeconds, AlertEscalation.MinAfterSeconds, AlertEscalation.MaxAfterSeconds),
                ShowIf = s => s.EscalationEnabled,
            }),
            new SettingItem(Setting.Number("alerts.sounds.escalateRepeats", s => s.EscalationRepeats,
                AlertEscalation.MinRepeats, AlertEscalation.MaxRepeats) with
            {
                Get = s => Math.Clamp(s.EscalationRepeats, AlertEscalation.MinRepeats, AlertEscalation.MaxRepeats),
                ShowIf = s => s.EscalationEnabled,
            }),
        }),
        // IDEAS #26: SpokenWay reads all four for every alert that was shown (SpeechOptions.Of, SpeechVoiceChoice.For),
        // so a change only saves.
        new SectionDef("alerts.sounds.speech", new PageItem[]
        {
            new SettingItem(Setting.Toggle("alerts.sounds.speak", s => s.SpeakAlerts)),
            From("alerts.sounds.speakFrom", s => s.SpeakAlertsFrom, (s, v) => s.SpeakAlertsFrom = v, "AppSettings.SpeakAlertsFrom", s => s.SpeakAlerts),
            new SettingItem(Setting.Custom("alerts.sounds.speechVolume", SettingKind.Slider,
                s => Math.Clamp(s.SpeechVolume, 0, 100),
                (s, v) => s.SpeechVolume = (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.SpeechVolume" }) with
            {
                Min = 0, Max = 100, Step = 1, UnitKey = "L.S.Unit.Percent",
                ShowIf = s => s.SpeakAlerts,
            }),
            new EditorItem(SpeechVoicesEditor.ItemId, ctx => new SpeechVoicesEditor(ctx))
            {
                Binds = new[] { "AppSettings.SpeechVoices" },
                ShowIf = s => s.SpeakAlerts,
            },
        }),
        new SectionDef("alerts.sounds.list", new PageItem[]
        {
            new EditorItem("alerts.sounds.table", ctx => new SoundTableEditor(ctx))
            {
                Binds = new[] { "AppSettings.AlertSoundFile", "AppSettings.AlertSounds", "AppSettings.SoundCooldowns" },
            },
        }),
        // IDEAS #58: a group's own sound. App hands GroupSounds its picture on Applied, and App.PlayAlertSound asks it
        // at every alert, so a change only saves.
        new SectionDef("alerts.sounds.groups", new PageItem[]
        {
            new EditorItem(GroupSoundsEditor.ItemId, ctx => new GroupSoundsEditor(ctx)) { Binds = new[] { "AppSettings.GroupAlertSounds" } },
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
        // IDEAS #33. NotificationsWay reads both for every alert that got past the gates. App follows the switch on
        // Applied: the registry key Windows needs is written when it goes on and removed when it goes off.
        new SectionDef("alerts.hub.windows", new PageItem[]
        {
            new SettingItem(Setting.Toggle("alerts.hub.notify", s => s.WindowsNotifications)),
            From("alerts.hub.notifyFrom", s => s.WindowsNotificationsFrom, (s, v) => s.WindowsNotificationsFrom = v,
                "AppSettings.WindowsNotificationsFrom", s => s.WindowsNotifications),
            // The player's way to see that notifications arrive: a button that shows one as a test, and a line that
            // says so when Windows itself is not showing MultiPreview's. It binds no setting: nothing here is saved.
            new EditorItem(NotificationCheckEditor.ItemId, ctx => new NotificationCheckEditor(ctx)) { ShowIf = s => s.WindowsNotifications },
        }),
        // IDEAS #2. PushWay reads all four for every alert that got past the gates, so a change only saves; App hands
        // the way every settings change (PushWay.Follow), which starts the sending again after a new link was saved.
        new SectionDef("alerts.hub.push", new PageItem[]
        {
            new SettingItem(Setting.Toggle("alerts.hub.push", s => s.PushAlerts)),
            From("alerts.hub.pushFrom", s => s.PushAlertsFrom, (s, v) => s.PushAlertsFrom = v, "AppSettings.PushAlertsFrom", s => s.PushAlerts),
            // IDEAS #2, batch 30: PushWay puts @here or @everyone on a message that holds a critical line. The card shows
            // the choice that is used (a stored number outside 0 to 2 is none) and does not rewrite it.
            new SettingItem(Setting.Custom("alerts.hub.pushMention", SettingKind.Choice, s => (s.PushMention is 1 or 2) ? s.PushMention : 0,
                (s, v) => s.PushMention = Convert.ToInt32(v, CultureInfo.InvariantCulture), binds: new[] { "AppSettings.PushMention" }) with
            {
                Options = PushMentionOptions,
                ShowIf = s => s.PushAlerts,
            }),
            new SettingItem(Setting.Toggle("alerts.hub.pushAway", s => s.PushOnlyWhenAway) with { ShowIf = s => s.PushAlerts }),
            // The link is a secret: the card keeps it DPAPI-encrypted, empties its box and shows it masked.
            new EditorItem(PushWebhookEditor.ItemId, ctx => new PushWebhookEditor(ctx))
            {
                Binds = new[] { "AppSettings.PushDiscordWebhook" },
                ShowIf = s => s.PushAlerts,
            },
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
