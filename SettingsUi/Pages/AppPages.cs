using System;
using System.Collections.Generic;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §5 App group: General, Profiles &amp; backups, About &amp; updates, Diagnostics.</summary>
public static class AppPages
{
    public static IReadOnlyList<PageDef> All => new[] { General(), Profiles(), About(), Diagnostics() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic("app.general", "language", new[] { "app.general.language", "app.general.scale" }),
        new HelpTopic("app.general", "startup", new[] { "app.general.startupSettings", "app.general.startWithWindows" }),
        new HelpTopic("app.diagnostics", "logs", new[]
        {
            "app.diagnostics.injection", "app.diagnostics.cycling", "app.diagnostics.windowHooks",
            "app.diagnostics.dwm", "app.diagnostics.alerts", "app.diagnostics.folder",
        }),
        new HelpTopic("app.profiles", "profiles", new[] { "app.profiles.current" }),
        new HelpTopic("app.profiles", "backups", new[] { "app.profiles.backup" }),
        new HelpTopic("app.about", "updates", new[] { "app.about.links", "app.about.checkOnStartup", "app.about.preRelease", "app.about.installVersion" }),
        new HelpTopic("app.about", "window", new[] { "app.about.useOldWindow" }),
    };

    private static PageDef General() => new("app.general", NavGroup.App, new[]
    {
        new SectionDef("app.general.display", new PageItem[]
        {
            new SettingItem(Setting.Choice("app.general.language", s => s.Language, LanguageOptions()) with
            {
                // Same live switch as the old window; Reload re-reads cards, help, search and the CJK font.
                OnChanged = ctx => { LocalizationService.SetLanguage(ctx.S.Language); ctx.Host.Reload(); },
                Replaces = new[] { "CmbLanguage" },
            }),
            // A dropdown, not the old slider: scaling the window moved the slider under the
            // mouse, which needed a drag-completed workaround (and arrow keys never applied).
            new SettingItem(Setting.Choice("app.general.scale", s => s.SettingsUiFontSize, ScaleOptions()) with
            {
                OnChanged = ctx => ctx.Host.Reload(),
                Replaces = new[] { "SliderUiScale" },
            }),
        }),
        new SectionDef("app.general.startup", new PageItem[]
        {
            new SettingItem(Setting.Choice("app.general.startupSettings", s => s.StartupSettings, new[]
            {
                new ChoiceOption(StartupSettingsMode.Off, "L.S.Opt.StartupOff"),
                new ChoiceOption(StartupSettingsMode.Open, "L.S.Opt.StartupOpen"),
                new ChoiceOption(StartupSettingsMode.OpenMinimized, "L.S.Opt.StartupMinimized"),
            }) with { Replaces = new[] { "CmbStartupSettings" } }),
            // Not in the settings file: the Windows Run key (Services/StartWithWindows).
            new SettingItem(Setting.Custom("app.general.startWithWindows", SettingKind.Toggle,
                _ => StartWithWindows.IsEnabled(),
                (_, v) => StartWithWindows.Set(v is true),
                binds: Array.Empty<string>()) with { External = true, Replaces = new[] { "ChkStartWithWindows" } }),
        }),
    });

    private static PageDef Profiles() => new("app.profiles", NavGroup.App, new[]
    {
        new SectionDef("app.profiles.profiles", new PageItem[]
        {
            new EditorItem("app.profiles.current", ctx => new ProfileEditor(ctx))
            {
                Binds = new[] { "AppSettings.LastUsedProfile", "AppSettings.Profiles" },
                Replaces = new[] { "OnCopyLayout", "CmbProfiles", "OnProfileChanged", "OnCreateProfile", "OnDeleteProfile" },
            },
        }),
        new SectionDef("app.profiles.backups", new PageItem[]
        {
            new ActionRow("app.profiles.backup", new[]
            {
                new ActionDef("app.profiles.backupNow", SettingsBackup.BackupInteractive) { Replaces = new[] { "OnBackupConfig" } },
                new ActionDef("app.profiles.export", SettingsBackup.ExportInteractive) { Replaces = new[] { "OnExportSettings" } },
                // ImportInteractive asks for confirmation itself, after checking the file is valid.
                new ActionDef("app.profiles.import", SettingsBackup.ImportInteractive) { Replaces = new[] { "OnImportSettings" } },
            }),
        }),
    });

    private static PageDef About() => new("app.about", NavGroup.App, new[]
    {
        new SectionDef("app.about.version", new PageItem[]
        {
            new InfoItem("app.about.versionText", InfoSeverity.Info) { Args = _ => new object[] { AboutActions.VersionText } },
            new ActionRow("app.about.links", new[]
            {
                new ActionDef("app.about.checkNow", AboutActions.CheckNow) { Replaces = new[] { "BtnCheckVersion" } },
                new ActionDef("app.about.github", _ => AboutActions.OpenGitHub()) { Replaces = new[] { "OnOpenGitHub" } },
            }),
        }),
        new SectionDef("app.about.updates", new PageItem[]
        {
            new SettingItem(Setting.Toggle("app.about.checkOnStartup", s => s.CheckForUpdatesOnStartup) with { Replaces = new[] { "ChkCheckUpdatesOnStartup" } }),
            new SettingItem(Setting.Toggle("app.about.preRelease", s => s.ReceivePreReleaseUpdates) with { Replaces = new[] { "ChkPreReleaseUpdates" } }),
            new EditorItem("app.about.installVersion", ctx => new VersionPickerEditor(ctx)) { Replaces = new[] { "CmbOtherVersion", "BtnInstallVersion" } },
        }),
        new SectionDef("app.about.window", new PageItem[]
        {
            new SettingItem(Setting.Toggle("app.about.useOldWindow", s => s.UseLegacySettingsWindow) with
            {
                OnChanged = ctx => ctx.Host.Toast(SettingsContext.Str("L.S.app.about.useOldWindow.Toast")),
            }),
        }),
    });

    private static PageDef Diagnostics() => new("app.diagnostics", NavGroup.App, new[]
    {
        new SectionDef("app.diagnostics.logs", new PageItem[]
        {
            new InfoItem("app.diagnostics.note", InfoSeverity.Info),
            new SettingItem(Setting.Toggle("app.diagnostics.injection", s => s.EnableDebugLogging_Injection) with { Replaces = new[] { "ChkDebugInjection" } }),
            new SettingItem(Setting.Toggle("app.diagnostics.cycling", s => s.EnableDebugLogging_Cycling) with { Replaces = new[] { "ChkDebugCycling" } }),
            new SettingItem(Setting.Toggle("app.diagnostics.windowHooks", s => s.EnableDebugLogging_WindowHooks) with { Replaces = new[] { "ChkDebugWindowHooks" } }),
            new SettingItem(Setting.Toggle("app.diagnostics.dwm", s => s.EnableDebugLogging_DWM) with { Replaces = new[] { "ChkDebugDwm" } }),
            new SettingItem(Setting.Toggle("app.diagnostics.alerts", s => s.EnableDebugLogging_Alerts) with { Replaces = new[] { "ChkDebugAlerts" } }),
            new ActionRow("app.diagnostics.folder", new[]
            {
                new ActionDef("app.diagnostics.openLogs", _ => DiagnosticsService.OpenLogsFolder()) { Replaces = new[] { "OnOpenLogsFolder" } },
            }),
        }),
    });

    /// <summary>"" = Automatic (Windows language, LocalizationService.Resolve), then the native names.</summary>
    private static IReadOnlyList<ChoiceOption> LanguageOptions() =>
        new[] { new ChoiceOption("", "L.S.Opt.LanguageAuto") }
            .Concat(LocalizationService.Languages.Select(l => new ChoiceOption(l.Code, "") { Literal = l.Name }))
            .ToList();

    /// <summary>SettingsUiFontSize 9–20 pt, shown as the window scale it gives (size / 12).</summary>
    private static IReadOnlyList<ChoiceOption> ScaleOptions() =>
        Enumerable.Range(9, 12).Select(pt => new ChoiceOption(pt, "") { Literal = $"{Math.Round(pt * 100 / 12.0)}%" }).ToList();
}
