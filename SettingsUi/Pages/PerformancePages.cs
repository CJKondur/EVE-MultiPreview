using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Spec §5 Performance: CPU, GPU, FPS limiter.</summary>
public static class PerformancePages
{
    public const string CpuId = "perf.cpu";
    public const string GpuId = "perf.gpu";
    public const string FpsId = "perf.fps";

    public static IReadOnlyList<PageDef> All => new[] { Cpu(), Gpu(), Fps() };

    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(CpuId, "priority", new[] { "perf.cpu.manage", "perf.cpu.autoBalance", "perf.cpu.coreTable" }),
        new HelpTopic(GpuId, "gpu", new[] { "perf.gpu.presetRow", "perf.gpu.static", "perf.gpu.suspend" }),
        new HelpTopic(FpsId, "limit", new[] { "perf.fps.rtssMissing", "perf.fps.rtssFound", "perf.fps.background", "perf.fps.applyRow" }),
        new HelpTopic(FpsId, "counter", new[] { "perf.fps.show", "perf.fps.insetX", "perf.fps.insetY", "perf.fps.size" }),
        new HelpTopic(FpsId, "manual", new[] { "perf.fps.target", "perf.fps.manualRow" }),
    };

    private static SettingItem Toggle(string id, Expression<Func<AppSettings, bool>> prop, params string[] replaces) =>
        new(Setting.Toggle(id, prop) with { Replaces = replaces });

    private static PageDef Cpu() => new(CpuId, NavGroup.Performance, new[]
    {
        new SectionDef("perf.cpu.priority", new PageItem[]
        {
            // Per profile; the affinity pass reads both every tick, and turning management off resets every client once.
            Toggle("perf.cpu.manage", s => s.ManageAffinity, "ChkManageAffinity"),
            new SettingItem(Setting.Toggle("perf.cpu.autoBalance", s => s.AutoBalanceCores) with
            {
                ShowIf = s => s.ManageAffinity,
                Replaces = new[] { "ChkAutoBalanceCores" },
            }),
        }),
        new SectionDef("perf.cpu.pins", new PageItem[]
        {
            new EditorItem("perf.cpu.coreTable")
            {
                ShowIf = s => s.ManageAffinity,
                Binds = new[] { "AppSettings.PerClientCores" },
                Replaces = new[] { "LvPerClientCores" },
            },
        }),
    });

    private static PageDef Gpu() => new(GpuId, NavGroup.Performance, new[]
    {
        new SectionDef("perf.gpu.preset", new PageItem[]
        {
            new ActionRow("perf.gpu.presetRow", new[]
            {
                new ActionDef("perf.gpu.lowGpu", PerformanceActions.LowGpuPreset) { Replaces = new[] { "BtnLowGpuPreset" } },
            }),
        }),
        new SectionDef("perf.gpu.thumbnails", new PageItem[]
        {
            // The thumbnail tick reads both.
            Toggle("perf.gpu.static", s => s.StaticThumbnails, "ChkStaticThumbnails"),
            Toggle("perf.gpu.suspend", s => s.SuspendThumbnailsWhenBackground, "ChkSuspendThumbsBackground"),
        }),
    });

    private static PageDef Fps() => new(FpsId, NavGroup.Performance, new[]
    {
        new SectionDef("perf.fps.rtss", new PageItem[]
        {
            new InfoItem("perf.fps.rtssMissing", InfoSeverity.Warning) { ShowIf = _ => !PerformanceActions.RtssInstalled() },
            new InfoItem("perf.fps.rtssFound", InfoSeverity.Info) { ShowIf = _ => PerformanceActions.RtssInstalled() },
        }),
        new SectionDef("perf.fps.limit", new PageItem[]
        {
            // Remembered for Apply and Copy; RTSS itself reads the profile they write.
            new SettingItem(Setting.Choice("perf.fps.background", s => s.RtssFpsLimit, PerformanceActions.FpsOptions) with
            {
                Replaces = new[] { "CmbFpsLimit" },
            }),
            new ActionRow("perf.fps.applyRow", new[]
            {
                new ActionDef("perf.fps.apply", PerformanceActions.ApplyRtssProfile) { Replaces = new[] { "OnApplyRtssProfile" } },
            }),
        }),
        new SectionDef("perf.fps.counter", new PageItem[]
        {
            // The FPS tick reads the switch.
            Toggle("perf.fps.show", s => s.ShowRtssFps, "ChkShowFps"),
            // The per-thumbnail apply (ReapplySettings) places and sizes the counter.
            new SettingItem(Setting.Number("perf.fps.insetX", s => s.FpsOverlayMarginX, -1, 500, "L.S.Unit.Px", ApplyScope.Thumbnails) with
            {
                ShowIf = s => s.ShowRtssFps,
                Replaces = new[] { "TxtFpsMarginX" },
            }),
            new SettingItem(Setting.Number("perf.fps.insetY", s => s.FpsOverlayMarginY, -1, 500, "L.S.Unit.Px", ApplyScope.Thumbnails) with
            {
                ShowIf = s => s.ShowRtssFps,
                Replaces = new[] { "TxtFpsMarginY" },
            }),
            new SettingItem(Setting.Number("perf.fps.size", s => s.FpsOverlayTextSize, 0, 72, "L.S.Unit.Pt", ApplyScope.Thumbnails) with
            {
                ShowIf = s => s.ShowRtssFps,
                Replaces = new[] { "TxtFpsTextSize" },
            }),
        }),
        new SectionDef("perf.fps.manual", new PageItem[]
        {
            new InfoItem("perf.fps.target", InfoSeverity.Info) { Args = _ => new object[] { PerformanceActions.TargetFolderText() } },
            new ActionRow("perf.fps.manualRow", new[]
            {
                new ActionDef("perf.fps.copyFile", PerformanceActions.CopyRtssProfile) { Replaces = new[] { "OnCopyRtssProfile" } },
                new ActionDef("perf.fps.openFolder", PerformanceActions.OpenRtssFolder) { Replaces = new[] { "OnOpenRtssFolder" } },
            }),
        }),
    });
}
