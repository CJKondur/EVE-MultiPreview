using System;
using System.Collections.Generic;
using System.Globalization;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>The per-character page (spec §5, §8.4, D4), reached as "character/&lt;name&gt;" from the
/// Characters table, Home tiles, search and (Plan 4) the thumbnail menu. An aggregation view: each
/// card reads and writes the store the old window's editor used.</summary>
public static class CharacterPage
{
    public const string Prefix = "character/";
    public const string TemplateId = "character";
    /// <summary>The name the registry template is built for. EVE names can't contain '&lt;'.</summary>
    public const string TemplateName = "<character>";

    /// <summary>Thumbnails › Crop windows (Plan 3b). The crop link shows once that page exists.</summary>
    public const string CropsPageId = "thumbs.crops";

    public static string IdFor(string name) => Prefix + name;

    public static PageDef? TryBuild(string id) =>
        id.StartsWith(Prefix, StringComparison.Ordinal) && id.Length > Prefix.Length ? Build(id[Prefix.Length..]) : null;

    /// <summary>Checked by the registry, coverage, string and help tests; never navigated.</summary>
    public static PageDef Template { get; } = Build(TemplateName);

    public static PageDef Build(string name) => new(IdFor(name), NavGroup.Clients, Sections(name))
    {
        Title = name,
        ParentId = ClientsPages.CharactersId,
        Template = TemplateId,
    };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(TemplateId, "switching", new[] { "character.hotkey", "character.cycle", "character.groups", "character.noGroup", "character.groupsLink" }),
        new HelpTopic(TemplateId, "thumbnail", new[]
        {
            "character.show", "character.label", "character.labelColor", "character.labelSize", "character.size", "character.pip", "character.pipOpacity",
        }),
        new HelpTopic(TemplateId, "colors", new[] { "character.colorsOff", "character.colorActive", "character.colorInactive", "character.colorsReset" }),
        new HelpTopic(TemplateId, "clients", new[] { "character.neverMinimize", "character.core", "character.volume" }),
        new HelpTopic(TemplateId, "stats", new[]
        {
            "character.statsOff", "character.statsNote", "character.statsDamage", "character.statsLogi", "character.statsMining", "character.statsRatting",
        }),
        new HelpTopic(TemplateId, "numbers", new[] { "character.statsPicker" }),
        new HelpTopic(TemplateId, "crops", new[] { "character.cropCount", "character.cropsLink" }),
        new HelpTopic(TemplateId, "remove", new[] { "character.forgetRow" }),
    };

    private static IReadOnlyList<SectionDef> Sections(string name) => new[]
    {
        Switching(name), Thumbnail(name), Colors(name), ClientWindow(name), Stats(name), Crops(name), Remove(name),
    };

    private static SectionDef Switching(string name) => new("character.switching", new PageItem[]
    {
        new SettingItem(Setting.Custom("character.hotkey", SettingKind.Hotkey,
            s => CharacterStores.HotkeyOf(s, name),
            (s, v) => CharacterStores.SetHotkey(s, name, v as string ?? ""),
            binds: new[] { "Profile.Hotkeys" }) with { UseId = HotkeyUses.CharacterId(name) }),
        new EditorItem("character.cycle", ctx => new CharacterCycleEditor(ctx, name)),
        new InfoItem("character.groups", InfoSeverity.Info)
        {
            Args = ctx => new object[] { string.Join(", ", CharacterStores.GroupsOf(ctx.S, name)) },
            ShowIf = s => CharacterStores.GroupsOf(s, name).Count > 0,
        },
        new InfoItem("character.noGroup", InfoSeverity.Info) { ShowIf = s => CharacterStores.GroupsOf(s, name).Count == 0 },
        new ActionRow("character.groupsLink", new[]
        {
            new ActionDef("character.openGroups", ctx => ctx.Host.Navigate(ClientsPages.GroupsId)),
        }),
    });

    private static SectionDef Thumbnail(string name) => new("character.thumbnail", new PageItem[]
    {
        // As the Characters table: store 0/1, show or hide that thumbnail now, only save (#50, #51).
        new SettingItem(Setting.Custom("character.show", SettingKind.Toggle,
            s => CharacterStores.IsShown(s, name),
            (s, v) => CharacterStores.StoreShown(s, name, v is true),
            binds: new[] { "AppSettings.ThumbnailVisibility" }) with
        {
            OnChanged = ctx => ctx.Services.Thumbnails?.SetCharacterVisibility(name, CharacterStores.IsShown(ctx.S, name)),
        }),
        new SettingItem(Setting.Custom("character.label", SettingKind.Text,
            s => CharacterFields.Label(s, name),
            (s, v) => CharacterFields.SetLabel(s, name, v as string),
            binds: new[] { "AppSettings.ThumbnailAnnotations" }) with
        {
            OnChanged = ctx => CharacterFields.PushLabel(ctx, name),
            Replaces = new[] { "LstAnnotations", "OnEditAnnotation", "OnClearAnnotation", "OnEditVisibilityLabel" },
        }),
        new SettingItem(Setting.Custom("character.labelColor", SettingKind.Color,
            s => CharacterFields.LabelColor(s, name),
            (s, v) => CharacterFields.SetLabelColor(s, name, v as string),
            binds: new[] { "AppSettings.ThumbnailLabelStyles" }) with
        {
            Validate = v => v is string text && (text.Trim().Length == 0 || Setting.IsColor(text)) ? null : Setting.ErrColor,
            OnChanged = ctx => CharacterFields.PushLabel(ctx, name),
        }),
        new SettingItem(Setting.Custom("character.labelSize", SettingKind.Number,
            s => CharacterFields.LabelSize(s, name),
            (s, v) => CharacterFields.SetLabelSize(s, name, Convert.ToInt32(v, CultureInfo.InvariantCulture)),
            binds: new[] { "AppSettings.ThumbnailLabelStyles" }) with
        {
            Min = 0, Max = 72, UnitKey = "L.S.Unit.Pt", Validate = v => Setting.RangeError(v, 0, 72),
            OnChanged = ctx => CharacterFields.PushLabel(ctx, name),
        }),
        new EditorItem("character.size", ctx => new CharacterSizeEditor(ctx, name))
        {
            Binds = new[] { "Profile.ThumbnailPositions" },
            Replaces = new[]
            {
                "CmbActiveChars", "TxtActiveCharW", "TxtActiveCharH", "OnApplyActiveCharSize", "OnRefreshActiveChars",
                "CmbResizeChar", "TxtResizeCharW", "TxtResizeCharH", "OnApplySizeToChar", "OnRefreshResizeChars",
            },
        },
        // ReapplySettings syncs second thumbnails; create/destroy at once as the old Add/Remove did.
        new SettingItem(Setting.Custom("character.pip", SettingKind.Toggle,
            s => CharacterFields.PipOn(s, name),
            (s, v) => CharacterFields.SetPip(s, name, v is true),
            binds: new[] { "AppSettings.SecondaryThumbnails" }, apply: ApplyScope.Thumbnails) with
        {
            OnChanged = ctx =>
            {
                if (CharacterFields.PipOn(ctx.S, name)) ctx.Services.Thumbnails?.CreateSecondaryForCharacter(name);
                else ctx.Services.Thumbnails?.DestroySecondaryForCharacter(name);
            },
        }),
        new SettingItem(Setting.Custom("character.pipOpacity", SettingKind.Slider,
            s => CharacterFields.PipOpacity(s, name),
            (s, v) => CharacterFields.SetPipOpacity(s, name, Convert.ToDouble(v, CultureInfo.InvariantCulture)),
            binds: new[] { "AppSettings.SecondaryThumbnails" }, apply: ApplyScope.Opacity) with
        {
            Min = 8, Max = 100, Step = 1, UnitKey = "L.S.Unit.Percent",
            ShowIf = s => CharacterFields.PipOn(s, name),
        }),
    });

    private static SectionDef Colors(string name) => new("character.colors", new PageItem[]
    {
        new InfoItem("character.colorsOff", InfoSeverity.Info) { ShowIf = s => !s.CustomColorsActive },
        ColorCard("character.colorActive", name, CharacterFields.ColorPart.Border, new[] { "LvColors", "OnColorAdd", "OnColorEdit", "OnPickColor" }),
        ColorCard("character.colorInactive", name, CharacterFields.ColorPart.Inactive, Array.Empty<string>()),
        new ActionRow("character.colorsReset", new[]
        {
            new ActionDef("character.resetColors", ctx =>
            {
                ctx.S.CustomColors.Remove(name);
                ctx.Applier.Request(ApplyScope.Thumbnails);
                ctx.Host.Reload();   // the two colour cards show the defaults again
            }) { Replaces = new[] { "OnColorDelete" } },
        }) { ShowIf = s => s.CustomColors.ContainsKey(name) },
    });

    private static SettingItem ColorCard(string id, string name, CharacterFields.ColorPart part, IReadOnlyList<string> replaces) =>
        new(Setting.Custom(id, SettingKind.Color,
            s => CharacterFields.Color(s, name, part),
            (s, v) => CharacterFields.SetColor(s, name, part, v as string ?? ""),
            binds: new[] { "AppSettings.CustomColors" }, apply: ApplyScope.Thumbnails) with
        {
            Validate = v => Setting.IsColor(v as string) ? null : Setting.ErrColor,
            Replaces = replaces,
        });

    private static SectionDef ClientWindow(string name) => new("character.clients", new PageItem[]
    {
        // Read on every switch by MinimizeInactiveClients: Save is enough.
        new SettingItem(Setting.Custom("character.neverMinimize", SettingKind.Toggle,
            s => CharacterFields.NeverMinimize(s, name),
            (s, v) => CharacterFields.SetNeverMinimize(s, name, v is true),
            binds: new[] { "Profile.DontMinimizeClients" })),
        // Read by the affinity pass while ManageAffinity is on: Save is enough.
        new SettingItem(Setting.Custom("character.core", SettingKind.Choice,
            s => CharacterFields.Core(s, name),
            (s, v) => CharacterFields.SetCore(s, name, Convert.ToInt32(v, CultureInfo.InvariantCulture)),
            binds: new[] { "AppSettings.PerClientCores" }) with { Options = CharacterFields.CoreOptions() }),
        new SettingItem(Setting.Custom("character.volume", SettingKind.Slider,
            s => CharacterFields.Volume(s, name),
            (s, v) => CharacterFields.SetVolume(s, name, (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture))),
            binds: new[] { "AppSettings.PerClientAudioVolume" }) with
        {
            Min = 0, Max = 100, Step = 1, UnitKey = "L.S.Unit.Percent",
            // Sets the running client's volume now (and stores the same value).
            OnChanged = ctx => ctx.Services.Thumbnails?.SetClientVolume(name, CharacterFields.Volume(ctx.S, name)),
        }),
    });

    private static SectionDef Stats(string name) => new("character.stats", new PageItem[]
    {
        new InfoItem("character.statsOff", InfoSeverity.Info) { ShowIf = s => !s.StatOverlayEnabled },
        new InfoItem("character.statsNote", InfoSeverity.Info),
        StatsCard("character.statsDamage", name, StatMetrics.DpsMask),
        StatsCard("character.statsLogi", name, StatMetrics.LogiMask),
        StatsCard("character.statsMining", name, StatMetrics.MineMask),
        StatsCard("character.statsRatting", name, StatMetrics.RatMask),
        // The full per-number picker (the old CharacterStatEditorWindow), under the group switches (Plan 3c).
        new EditorItem("character.statsPicker", ctx => new StatPickerEditor(ctx, name))
        {
            Binds = new[] { "AppSettings.PerCharacterStats" },
            Replaces = new[] { "OnStatCharEditClick" },
        },
    });

    // A re-apply creates or removes the character's stats window when its numbers change.
    private static SettingItem StatsCard(string id, string name, StatMetrics mask) =>
        new(Setting.Custom(id, SettingKind.Segmented,
            s => CharacterFields.StatsCategory(s, name, mask),
            (s, v) => CharacterFields.SetStatsCategory(s, name, mask, Convert.ToInt32(v, CultureInfo.InvariantCulture)),
            binds: new[] { "AppSettings.PerCharacterStats" }, apply: ApplyScope.Thumbnails) with
        {
            Options = CharacterFields.StatsOptions,
        });

    private static SectionDef Crops(string name) => new("character.crops", new PageItem[]
    {
        new InfoItem("character.cropCount", InfoSeverity.Info)
        {
            Args = ctx => new object[] { CropEdits.Of(ctx.S, name).Count },
        },
        new ActionRow("character.cropsLink", new[]
        {
            new ActionDef("character.openCrops", ctx =>
            {
                CropsEditor.Request(name);   // the Crop windows page opens on this character (Plan 3b)
                ctx.Host.Navigate(CropsPageId);
            }),
        }) { ShowIf = _ => SettingsRegistry.Page(CropsPageId) != null },
    });

    private static SectionDef Remove(string name) => new("character.remove", new PageItem[]
    {
        new ActionRow("character.forgetRow", new[]
        {
            // ForgetInteractive asks itself, naming the character (spec §6: destructive actions confirm).
            new ActionDef("character.forget", ctx => CharacterForget.ForgetInteractive(ctx, name)) { Destructive = true },
        }),
    });
}
