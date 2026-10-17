using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §5 App group: General, Profiles &amp; backups, About &amp; updates, Diagnostics.</summary>
public static class AppPages
{
    /// <summary>App settings › Look (UI styles spec §9.1).</summary>
    public const string LookId = "app.look";

    public static IReadOnlyList<PageDef> All => new[] { General(), Look(), Profiles(), About(), Diagnostics() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic("app.general", "language", new[] { "app.general.language", "app.general.scale" }),
        new HelpTopic("app.general", "startup", new[] { "app.general.startupSettings", "app.general.startWithWindows" }),
        new HelpTopic(LookId, "look", new[] { LookGallery.ItemId }),
        new HelpTopic("app.diagnostics", "logs", new[]
        {
            "app.diagnostics.injection", "app.diagnostics.cycling", "app.diagnostics.windowHooks",
            "app.diagnostics.dwm", "app.diagnostics.alerts", "app.diagnostics.folder",
        }),
        new HelpTopic("app.diagnostics", "errors", new[] { "app.diagnostics.errorLog" }),
        new HelpTopic("app.profiles", "profiles", new[] { "app.profiles.current" }),
        new HelpTopic("app.profiles", "backups", new[] { "app.profiles.backup" }),
        new HelpTopic("app.about", "updates", new[] { "app.about.links", "app.about.checkOnStartup", "app.about.preRelease", "app.about.installVersion" }),
        new HelpTopic("app.about", "window", new[] { "app.about.useOldWindow" }),
        new HelpTopic("app.about", "fonts", new[] { FontLicencesEditor.ItemId }),
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

    /// <summary>UI styles spec §9.1: the look of the app, a gallery of the looks. An App page, so Simple mode has it too.
    /// Its second section, Windows contrast themes, comes with its rule in plan S4.</summary>
    private static PageDef Look() => new(LookId, NavGroup.App, new[]
    {
        new SectionDef("app.look.look", new PageItem[]
        {
            new EditorItem(LookGallery.ItemId, ctx => new LookGallery(ctx)) { Binds = new[] { "AppSettings.UiStyle" } },
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
            // IDEAS #57: %APPDATA%\EVE MultiPreview, or the exe folder when portable.
            new InfoItem("app.profiles.folderText", InfoSeverity.Info) { Args = _ => new object[] { AppPaths.ConfigDir } },
            new ActionRow("app.profiles.backup", new[]
            {
                new ActionDef("app.profiles.backupNow", SettingsBackup.BackupInteractive) { Replaces = new[] { "OnBackupConfig" } },
                new ActionDef("app.profiles.export", SettingsBackup.ExportInteractive) { Replaces = new[] { "OnExportSettings" } },
                // ImportInteractive asks for confirmation itself, after checking the file is valid.
                new ActionDef("app.profiles.import", SettingsBackup.ImportInteractive) { Replaces = new[] { "OnImportSettings" } },
                new ActionDef("app.profiles.openFolder", _ => DiagnosticsService.OpenFolder(AppPaths.ConfigDir)),
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
        // UI styles spec §11.1: the typefaces the looks carry, and their licences.
        new SectionDef("app.about.fonts", new PageItem[]
        {
            new EditorItem(FontLicencesEditor.ItemId, _ => new FontLicencesEditor()),
        }),
    });

    private static PageDef Diagnostics() => new("app.diagnostics", NavGroup.App, new[]
    {
        // IDEAS #55: errors MultiPreview handled and kept running after, and a last session that didn't close.
        // Both lines read their value each time the page is built or refreshed, not once at startup.
        new SectionDef("app.diagnostics.errors", new PageItem[]
        {
            new InfoItem("app.diagnostics.errorCount", InfoSeverity.Warning)
            {
                ShowIf = _ => ErrorLog.Shared.Count > 0,
                Args = _ => new object[] { ErrorLog.Shared.Count },
            },
            new InfoItem("app.diagnostics.uncleanExit", InfoSeverity.Warning)
            {
                ShowIf = _ => RunMarker.PreviousUncleanStart != null,
                Args = _ => new object[] { RunMarker.PreviousUncleanStart?.ToString("g", CultureInfo.CurrentCulture) ?? "" },
            },
            new ActionRow("app.diagnostics.errorLog", new[]
            {
                new ActionDef("app.diagnostics.openErrorLog", _ => DiagnosticsService.OpenErrorLog()),
            }),
        }),
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
