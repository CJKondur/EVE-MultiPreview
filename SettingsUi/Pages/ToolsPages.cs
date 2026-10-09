using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §5 Tools: Stats overlay, EVE settings.</summary>
public static class ToolsPages
{
    public const string StatsId = "tools.stats";
    public const string EveId = "tools.eve";

    public static IReadOnlyList<PageDef> All => new[] { Stats(), Eve() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(StatsId, "overlay", new[] { "tools.stats.enabled", "tools.stats.hideOnLostFocus" }),
        new HelpTopic(StatsId, "numbers", new[] { "tools.stats.metrics" }),
        new HelpTopic(StatsId, "characters", new[] { "tools.stats.characterTable" }),
        new HelpTopic(StatsId, "look", new[] { "tools.stats.fontSize", "tools.stats.opacity", "tools.stats.background", "tools.stats.textColor" }),
        new HelpTopic(StatsId, "logging", new[]
        {
            "tools.stats.csv", "tools.stats.logFolder", "tools.stats.noFolder", "tools.stats.keepDays", "tools.stats.keepZero",
        }),
        new HelpTopic(EveId, "copy", new[] { "tools.eve.editor" }),
        new HelpTopic(EveId, "accounts", new[] { "tools.eve.editor" }),
    };

    /// <summary>A stats overlay colour: anything the overlay paints (StatOverlayWindow.ParseOverlayColor), stored
    /// trimmed. The picker's #RRGGBB is kept as it is, the defaults' form.</summary>
    private static SettingDescriptor StatsColour(string id, Expression<Func<AppSettings, string>> prop)
    {
        var d = Setting.Color(id, prop, ApplyScope.Thumbnails);
        return d with
        {
            Set = (s, v) => d.Set(s, (v as string)?.Trim() ?? ""),
            Validate = v => StatsFields.IsOverlayColour(v as string) ? null : Setting.ErrColor,
        };
    }

    private static PageDef Stats() => new(StatsId, NavGroup.Tools, new[]
    {
        new SectionDef("tools.stats.overlay", new PageItem[]
        {
            // ReapplySettings opens or closes every stats window.
            new SettingItem(Setting.Toggle("tools.stats.enabled", s => s.StatOverlayEnabled, ApplyScope.Thumbnails) with
            {
                Replaces = new[] { "ChkStatsOverlayEnabled" },
            }),
            // The 50 ms focus sweep reads it. No control until now.
            new SettingItem(Setting.Toggle("tools.stats.hideOnLostFocus", s => s.HideStatsOnLostFocus) with
            {
                ShowIf = s => s.StatOverlayEnabled,
            }),
        }),
        new SectionDef("tools.stats.numbers", new PageItem[]
        {
            new EditorItem("tools.stats.metrics", ctx => new StatMetricsEditor(ctx))
            {
                Binds = new[] { "AppSettings.GlobalStatMetrics" },
                Replaces = StatsFields.GlobalControls,
            },
        }),
        new SectionDef("tools.stats.characters", new PageItem[]
        {
            // Reads only: each › opens a character's own picker. It follows running clients by itself, so the
            // old Refresh button has no job.
            new EditorItem("tools.stats.characterTable", ctx => new StatCharactersEditor(ctx))
            {
                Replaces = new[] { "LvStatCharacters", "BtnRefreshStatChars" },
            },
        }),
        new SectionDef("tools.stats.look", new PageItem[]
        {
            // ReapplySettings re-styles every stats window.
            new SettingItem(Setting.Custom("tools.stats.fontSize", SettingKind.Slider,
                s => Math.Clamp(s.StatOverlayFontSize, StatsFields.FontMin, StatsFields.FontMax),
                (s, v) => s.StatOverlayFontSize = (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.StatOverlayFontSize" }, apply: ApplyScope.Thumbnails) with
            {
                Min = StatsFields.FontMin, Max = StatsFields.FontMax, Step = 1, UnitKey = "L.S.Unit.Pt",
                Replaces = new[] { "SliderStatFont" },
            }),
            new SettingItem(Setting.Custom("tools.stats.opacity", SettingKind.Slider,
                s => StatsFields.OpacityPercentOf(s.StatOverlayOpacity),
                (s, v) => s.StatOverlayOpacity = OpacityPercent.ToByte(Math.Max(StatsFields.OpacityMin, Convert.ToDouble(v, CultureInfo.InvariantCulture))),
                binds: new[] { "AppSettings.StatOverlayOpacity" }, apply: ApplyScope.Thumbnails) with
            {
                Min = StatsFields.OpacityMin, Max = 100, Step = 1, UnitKey = "L.S.Unit.Percent",
                Replaces = new[] { "SliderStatOpacity" },
            }),
            new SettingItem(StatsColour("tools.stats.background", s => s.StatOverlayBgColor) with { Replaces = new[] { "TxtStatBgColor" } }),
            new SettingItem(StatsColour("tools.stats.textColor", s => s.StatOverlayTextColor) with { Replaces = new[] { "TxtStatTextColor" } }),
        }),
        new SectionDef("tools.stats.logging", new PageItem[]
        {
            // App passes all three to StatTrackerService.SetCsvLogging on Applied (IDEAS P31).
            new SettingItem(Setting.Toggle("tools.stats.csv", s => s.StatLoggingEnabled) with { Replaces = new[] { "ChkStatLogging" } }),
            new SettingItem(Setting.Folder("tools.stats.logFolder", s => s.StatLogDirectory) with
            {
                ShowIf = s => s.StatLoggingEnabled,
                Replaces = new[] { "TxtStatLogDir", "OnBrowseStatLog" },
            }),
            new InfoItem("tools.stats.noFolder", InfoSeverity.Warning) { ShowIf = s => s.StatLoggingEnabled && string.IsNullOrWhiteSpace(s.StatLogDirectory) },
            // A stored 0 or less keeps every file (StatTrackerService.CleanupOldLogs, IDEAS P31); the box takes 1 or more.
            new SettingItem(Setting.Number("tools.stats.keepDays", s => s.StatLogRetentionDays, 1, 3650, "L.S.Unit.Days") with
            {
                ShowIf = s => s.StatLoggingEnabled,
                Replaces = new[] { "TxtStatLogRetention" },
            }),
            new InfoItem("tools.stats.keepZero", InfoSeverity.Info) { ShowIf = s => s.StatLoggingEnabled && s.StatLogRetentionDays < 1 },
        }),
    });

    private static PageDef Eve() => new(EveId, NavGroup.Tools, new[]
    {
        new SectionDef("tools.eve.main", new PageItem[]
        {
            new EditorItem("tools.eve.editor", ctx => new EveSettingsEditor(ctx))
            {
                SearchLabelKey = "L.S.Eve.SearchLabel",
                // The folders, account names and the character-account links are written by its buttons. EveManager
                // (the saved character-name cache) is read for names, never written, as the old panel did.
                Binds = new[]
                {
                    "AppSettings.EveSettingsDir", "AppSettings.EveBackupDir", "AppSettings.AccountLabels",
                    "AppSettings.AccountCharacterMap", "AppSettings.EveManager",
                },
                Replaces = new[]
                {
                    "TxtEveSettingsDir", "TxtEveBackupDir", "OnBrowseEveDir", "OnAutoDetectEveDir", "OnBrowseBackupDir",
                    "OnResetBackupDir", "OnRefreshProfiles",
                    "BtnProfileCopyMode", "BtnCharCopyMode", "BtnAccountCopyMode",
                    "LvEveMgrSource", "LvEveMgrTarget", "OnBackupCheckedTargets", "OnCopyToCheckedTargets",
                    "CmbCharSrcProfile", "CmbCharTgtProfile", "TxtCharSrcSearch", "TxtCharTgtSearch",
                    "LvCharSrcChars", "LvCharTgtChars", "ChkCopyAllChars", "OnCharCopyExecute",
                    "CmbAcctSrcProfile", "CmbAcctTgtProfile", "TxtAcctSrcSearch", "TxtAcctTgtSearch",
                    "LvAcctSrcAccounts", "LvAcctTgtAccounts", "ChkCopyAllAccounts", "OnAcctCopyExecute",
                    "OnAcctDetectWizard", "TxtAcctLabel", "OnAcctSetLabel", "OnFetchESI",
                },
            },
        }),
    });
}
