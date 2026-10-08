using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §5 Thumbnails: Appearance, Text &amp; borders, Layout &amp; screens, Visibility, Crop windows.</summary>
public static class ThumbnailsPages
{
    public const string AppearanceId = "thumbs.appearance";
    public const string TextId = "thumbs.text";
    public const string LayoutId = "thumbs.layout";
    public const string VisibilityId = "thumbs.visibility";
    /// <summary>The page the character page's crop link opens.</summary>
    public const string CropsId = CharacterPage.CropsPageId;

    public static IReadOnlyList<PageDef> All => new[] { Appearance() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(AppearanceId, "size", new[] { "thumbs.appearance.resizeAll", "thumbs.appearance.individualResize" }),
        new HelpTopic(AppearanceId, "look", new[]
        {
            "thumbs.appearance.opacity", "thumbs.appearance.opacityOnHover", "thumbs.appearance.hoverZoom",
            "thumbs.appearance.hoverScale", "thumbs.appearance.background",
        }),
    };

    private static SettingItem Toggle(string id, Expression<Func<AppSettings, bool>> prop, ApplyScope apply, params string[] replaces) =>
        new(Setting.Toggle(id, prop, apply) with { Replaces = replaces });

    /// <summary>A thumbnail colour: only what ThumbnailManager.ParseColor paints, stored the way the old
    /// window stored it (ThumbnailFields.StoreColour). <paramref name="allowEmpty"/> where empty means none.</summary>
    private static SettingDescriptor Colour(string id, Expression<Func<AppSettings, string>> prop, ApplyScope apply, bool allowEmpty = false)
    {
        var d = Setting.Color(id, prop, apply);
        return d with
        {
            Set = (s, v) => d.Set(s, ThumbnailFields.StoreColour(v as string)),
            Validate = v => ThumbnailFields.IsPaintable(v as string, allowEmpty) ? null : Setting.ErrColor,
        };
    }

    private static PageDef Appearance() => new(AppearanceId, NavGroup.Thumbnails, new[]
    {
        new SectionDef("thumbs.appearance.size", new PageItem[]
        {
            new EditorItem("thumbs.appearance.resizeAll", ctx => new ResizeAllEditor(ctx))
            {
                Binds = new[] { "AppSettings.ThumbnailStartLocation" },
                // TxtMinWidth/TxtMinHeight set ThumbnailMinimumSize, which nothing reads: the drag-resize
                // floor is fixed at 80×50 (thumbnails.md 0.4). No card; the setting is a parity exclusion.
                Replaces = new[] { "TxtResizeAllW", "TxtResizeAllH", "OnApplySizeToAll", "TxtMinWidth", "TxtMinHeight" },
            },
            // Read at each resize gesture: Save is enough.
            Toggle("thumbs.appearance.individualResize", s => s.IndividualThumbnailResize, ApplyScope.Save, "ChkIndividualResize"),
        }),
        new SectionDef("thumbs.appearance.seeThrough", new PageItem[]
        {
            // Home's card again: live while dragging, as the old slider.
            new SettingItem(Setting.Custom("thumbs.appearance.opacity", SettingKind.Slider,
                s => OpacityPercent.FromByte(s.ThumbnailOpacity),
                (s, v) => s.ThumbnailOpacity = OpacityPercent.ToByte(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.ThumbnailOpacity" }, apply: ApplyScope.Opacity) with
            {
                Min = 0, Max = 100, Step = 1, UnitKey = "L.S.Unit.Percent",
                OnChanged = ctx => ctx.Services.Thumbnails?.ApplyOpacityToAll(),
                Replaces = new[] { "SliderOpacity" },
            }),
            // ApplyOpacityToAll reads it too.
            Toggle("thumbs.appearance.opacityOnHover", s => s.OpacityOnHover, ApplyScope.Opacity, "ChkOpacityOnHover"),
        }),
        new SectionDef("thumbs.appearance.zoom", new PageItem[]
        {
            // ApplySettings sets every thumbnail's zoom; off gives 1.0 (Task 1).
            Toggle("thumbs.appearance.hoverZoom", s => s.ResizeThumbnailsOnHover, ApplyScope.Thumbnails, "ChkHoverZoom"),
            new SettingItem(Setting.Custom("thumbs.appearance.hoverScale", SettingKind.Slider,
                s => ThumbnailFields.ZoomPercent(s.HoverScale),
                (s, v) => s.HoverScale = ThumbnailFields.ZoomScale(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.HoverScale" }, apply: ApplyScope.Thumbnails) with
            {
                Min = ThumbnailFields.ZoomMin, Max = ThumbnailFields.ZoomMax, Step = 1, UnitKey = "L.S.Unit.Percent",
                ShowIf = s => s.ResizeThumbnailsOnHover,
                Replaces = new[] { "TxtHoverScale" },
            }),
        }),
        new SectionDef("thumbs.appearance.backdrop", new PageItem[]
        {
            new SettingItem(Colour("thumbs.appearance.background", s => s.ThumbnailBackgroundColor, ApplyScope.Thumbnails) with
            {
                Replaces = new[] { "TxtBackgroundColor" },
            }),
        }),
    });
}
