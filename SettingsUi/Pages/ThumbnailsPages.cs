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

    public static IReadOnlyList<PageDef> All => new[] { Appearance(), Text(), Layout() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(AppearanceId, "size", new[] { "thumbs.appearance.resizeAll", "thumbs.appearance.individualResize" }),
        new HelpTopic(AppearanceId, "look", new[]
        {
            "thumbs.appearance.opacity", "thumbs.appearance.opacityOnHover", "thumbs.appearance.hoverZoom",
            "thumbs.appearance.hoverScale", "thumbs.appearance.background",
        }),
        new HelpTopic(TextId, "text", new[]
        {
            "thumbs.text.showText", "thumbs.text.showSystem", "thumbs.text.showTimer", "thumbs.text.color",
            "thumbs.text.font", "thumbs.text.size", "thumbs.text.marginX", "thumbs.text.marginY",
        }),
        new HelpTopic(TextId, "stats", new[] { "thumbs.text.showStats", "thumbs.text.statsSize" }),
        new HelpTopic(TextId, "borders", new[]
        {
            "thumbs.text.highlight", "thumbs.text.highlightColor", "thumbs.text.highlightWidth", "thumbs.text.highlightInGroup",
            "thumbs.text.inactiveColor", "thumbs.text.inactiveWidth", "thumbs.text.groupBorders",
        }),
        new HelpTopic(TextId, "marks", new[] { "thumbs.text.badgePosition" }),
        new HelpTopic(LayoutId, "screens", new[]
        {
            "thumbs.layout.monitor", "thumbs.layout.identifyRow", "thumbs.layout.startX", "thumbs.layout.startY",
            "thumbs.layout.startWidth", "thumbs.layout.startHeight",
        }),
        new HelpTopic(LayoutId, "lining", new[] { "thumbs.layout.snap", "thumbs.layout.snapDistance", "thumbs.layout.confine", "thumbs.layout.lock" }),
        new HelpTopic(LayoutId, "history", new[] { "thumbs.layout.history" }),
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

    /// <summary>A size stored as text (ThumbnailTextSize, ProcessStatsTextSize): shown as a number, stored as
    /// a whole number (ThumbnailFields.WriteSize). Unreadable text shows <paramref name="fallback"/>.</summary>
    private static SettingDescriptor TextSize(string id, Expression<Func<AppSettings, string>> prop, double fallback,
        double min, double max, ApplyScope apply)
    {
        var text = Setting.Text(id, prop, apply);   // the Get, Set and Binds of the text property
        return Setting.Custom(id, SettingKind.Number,
            s => ThumbnailFields.ReadSize(text.Get(s) as string, fallback),
            (s, v) => text.Set(s, ThumbnailFields.WriteSize(v)),
            text.Binds, apply) with
        {
            Min = min, Max = max, UnitKey = "L.S.Unit.Pt",
            Validate = v => Setting.RangeError(v, min, max),
        };
    }

    private static SettingItem Number<T>(string id, Expression<Func<AppSettings, T>> prop, double min, double max,
        ApplyScope apply, params string[] replaces) =>
        new(Setting.Number(id, prop, min, max, "L.S.Unit.Px", apply) with { Replaces = replaces });

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

    private static PageDef Text() => new(TextId, NavGroup.Thumbnails, new[]
    {
        new SectionDef("thumbs.text.labels", new PageItem[]
        {
            // ApplySettings shows or hides the name and the system on every thumbnail.
            Toggle("thumbs.text.showText", s => s.ShowThumbnailTextOverlay, ApplyScope.Thumbnails, "ChkShowName"),
            Toggle("thumbs.text.showSystem", s => s.ShowSystemName, ApplyScope.Thumbnails, "ChkShowSystem"),
            // Spec D11: moved here from General. Read every second (and hidden while off, Task 1).
            Toggle("thumbs.text.showTimer", s => s.ShowSessionTimer, ApplyScope.Save, "ChkShowTimer"),
            // Crop window names use the same colour, font and size (CropManager re-reads them on Refresh).
            new SettingItem(Colour("thumbs.text.color", s => s.ThumbnailTextColor, ApplyScope.Thumbnails | ApplyScope.Crops) with
            {
                Replaces = new[] { "TxtTextColor" },
            }),
            new SettingItem(Setting.Text("thumbs.text.font", s => s.ThumbnailTextFont, ApplyScope.Thumbnails | ApplyScope.Crops) with
            {
                Validate = v => string.IsNullOrWhiteSpace(v as string) ? "L.S.Err.FontName" : null,
                Replaces = new[] { "TxtTextFont" },
            }),
            new SettingItem(TextSize("thumbs.text.size", s => s.ThumbnailTextSize, 12, 6, 48, ApplyScope.Thumbnails | ApplyScope.Crops) with
            {
                Replaces = new[] { "TxtTextSize" },
            }),
            Number("thumbs.text.marginX", s => s.ThumbnailTextMargins.X, 0, 500, ApplyScope.Thumbnails, "TxtTextMarginX"),
            Number("thumbs.text.marginY", s => s.ThumbnailTextMargins.Y, 0, 500, ApplyScope.Thumbnails, "TxtTextMarginY"),
        }),
        new SectionDef("thumbs.text.stats", new PageItem[]
        {
            // A re-apply clears the numbers when this goes off; on, they are read at each stats tick.
            Toggle("thumbs.text.showStats", s => s.ShowProcessStats, ApplyScope.Thumbnails, "ChkShowStats"),
            new SettingItem(TextSize("thumbs.text.statsSize", s => s.ProcessStatsTextSize, 9, 6, 36, ApplyScope.Thumbnails) with
            {
                ShowIf = s => s.ShowProcessStats,
                Replaces = new[] { "TxtStatsTextSize" },
            }),
        }),
        new SectionDef("thumbs.text.active", new PageItem[]
        {
            // Painted by the focus sweep when the client in front changes. While settings is in front no
            // client is, so a change shows when the player goes back to EVE: Save is enough.
            Toggle("thumbs.text.highlight", s => s.ShowClientHighlightBorder, ApplyScope.Save, "ChkShowHighlightBorder"),
            new SettingItem(Colour("thumbs.text.highlightColor", s => s.ClientHighlightColor, ApplyScope.Save) with
            {
                ShowIf = s => s.ShowClientHighlightBorder,
                Replaces = new[] { "TxtActiveColor" },
            }),
            new SettingItem(Setting.Number("thumbs.text.highlightWidth", s => s.ClientHighlightBorderThickness, 1, 30, "L.S.Unit.Px") with
            {
                ShowIf = s => s.ShowClientHighlightBorder,
                Replaces = new[] { "TxtActiveBorderThickness" },
            }),
            // Matters only while group borders are on (the old switch was greyed out otherwise).
            new SettingItem(Setting.Toggle("thumbs.text.highlightInGroup", s => s.HighlightOverridesGroupColor) with
            {
                ShowIf = s => s.ShowClientHighlightBorder && s.ShowAllColoredBorders,
                Replaces = new[] { "ChkHighlightOverridesGroup" },
            }),
        }),
        new SectionDef("thumbs.text.others", new PageItem[]
        {
            // ApplySettings repaints every thumbnail with the not-in-front look at once.
            new SettingItem(Colour("thumbs.text.inactiveColor", s => s.InactiveClientBorderColor, ApplyScope.Thumbnails, allowEmpty: true) with
            {
                Replaces = new[] { "TxtInactiveColor" },
            }),
            Number("thumbs.text.inactiveWidth", s => s.InactiveClientBorderThickness, 0, 30, ApplyScope.Thumbnails, "TxtFrameThickness"),
            // ShowAllColoredBorders has one switch, on Groups (spec §5; Plan 3a claimed ChkShowAllBorders there).
            new ActionRow("thumbs.text.groupBorders", new[]
            {
                new ActionDef("thumbs.text.openGroups", ctx => ctx.Host.Navigate(ClientsPages.GroupsId, "clients.groups.showBorders")),
            }),
        }),
        new SectionDef("thumbs.text.marks", new PageItem[]
        {
            new SettingItem(Setting.Custom("thumbs.text.badgePosition", SettingKind.Choice,
                s => ThumbnailFields.Anchor(s.CycleExclusionBadgePosition),
                (s, v) => s.CycleExclusionBadgePosition = v as string ?? "TopLeft",
                binds: new[] { "AppSettings.CycleExclusionBadgePosition" }, apply: ApplyScope.Thumbnails) with
            {
                Options = ThumbnailFields.AnchorOptions,
                Replaces = new[] { "CmbExclusionBadgePos" },
            }),
        }),
    });

    private static PageDef Layout() => new(LayoutId, NavGroup.Thumbnails, new[]
    {
        new SectionDef("thumbs.layout.screens", new PageItem[]
        {
            // Home's editor again (MultiPreview's own 1-based order); it repaints thumbnails.
            new EditorItem("thumbs.layout.monitor", ctx => new MonitorPickerEditor(ctx, "thumbs.layout.monitor"))
            {
                Binds = new[] { "AppSettings.PreferredMonitor" },
                Replaces = new[] { "CmbPreferredMonitor" },
            },
            new ActionRow("thumbs.layout.identifyRow", new[]
            {
                // Shows a badge on every real monitor for 2.5 s: never run it in a test.
                new ActionDef("thumbs.layout.identify", _ => MonitorIdentify.Show(MonitorBadge)) { Replaces = new[] { "OnIdentifyMonitors" } },
            }),
        }),
        new SectionDef("thumbs.layout.newThumbnails", new PageItem[]
        {
            // Used for a thumbnail with no saved place (a new client, Reset); a re-apply moves those at once.
            Number("thumbs.layout.startX", s => s.ThumbnailStartLocation.X, -10000, 10000, ApplyScope.Thumbnails, "TxtStartX"),
            Number("thumbs.layout.startY", s => s.ThumbnailStartLocation.Y, -10000, 10000, ApplyScope.Thumbnails, "TxtStartY"),
            Number("thumbs.layout.startWidth", s => s.ThumbnailStartLocation.Width, CharacterSize.Min, CharacterSize.Max, ApplyScope.Thumbnails, "TxtThumbWidth"),
            Number("thumbs.layout.startHeight", s => s.ThumbnailStartLocation.Height, CharacterSize.Min, CharacterSize.Max, ApplyScope.Thumbnails, "TxtThumbHeight"),
        }),
        new SectionDef("thumbs.layout.lining", new PageItem[]
        {
            // Read when a drag ends: Save is enough.
            Toggle("thumbs.layout.snap", s => s.ThumbnailSnap, ApplyScope.Save, "ChkSnap"),
            // TxtGutter set ThumbnailGutter, which nothing reads since snapping went flush-only (a parity exclusion).
            new SettingItem(Setting.Number("thumbs.layout.snapDistance", s => s.ThumbnailSnapDistance, 1, 200, "L.S.Unit.Px") with
            {
                ShowIf = s => s.ThumbnailSnap,
                Replaces = new[] { "TxtSnapDistance", "TxtGutter" },
            }),
            // ReapplySettings passes these to every thumbnail (lock also to second thumbnails and stats windows).
            Toggle("thumbs.layout.confine", s => s.ConfineDragsToMonitor, ApplyScope.Thumbnails, "ChkConfineMonitor"),
            Toggle("thumbs.layout.lock", s => s.LockPositions, ApplyScope.Thumbnails, "ChkLockPositions"),
        }),
        new SectionDef("thumbs.layout.undo", new PageItem[]
        {
            new EditorItem("thumbs.layout.history", ctx => new LayoutHistoryEditor(ctx))
            {
                Replaces = new[] { "OnUndoLayout", "OnRedoLayout", "OnResetThumbnailPositions" },
            },
        }),
    });

    /// <summary>"Monitor 2" (and "(primary)" below it) on Identify's badges, in the settings language.</summary>
    private static string MonitorBadge(int number, bool primary) =>
        string.Format(SettingsContext.Str("L.S.Monitor.Badge"), number)
        + (primary ? "\n" + SettingsContext.Str("L.S.Monitor.Primary") : "");
}
