using System;
using System.Collections.Generic;
using System.Globalization;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Home (spec §5, D2): the essentials on one page; in Simple mode the only page. Its
/// cards are the only Simple ones.</summary>
public static class HomePage
{
    public const string Id = "home";

    public static IReadOnlyList<PageDef> All => new[] { Home() };

    /// <summary>Simple mode's "getting started" topics (spec §9).</summary>
    public static IReadOnlyList<HelpTopic> Help => new[]
    {
        new HelpTopic(Id, "clients", new[] { "home.tiles" }),
        new HelpTopic(Id, "switching", new[] { "home.next", "home.previous" }),
        new HelpTopic(Id, "thumbnails", new[] { "home.size", "home.opacity", "home.monitor" }),
        new HelpTopic(Id, "alerts", new[] { "home.alertAttack", "home.alertScram", "home.alertDecloak", "home.alertFleet", "home.alertSound" }),
        new HelpTopic(Id, "quick", new[] { "home.quickActions", "home.startWithWindows", "home.showAllRow" }),
    };

    private static SettingItem Simple(SettingDescriptor d) => new(d with { Simple = true });

    /// <summary>EnabledAlertTypes: a missing key is on (the alert list and LogMonitorService read it so).
    /// App re-syncs the log monitor on Applied, so Save is enough.</summary>
    private static SettingItem Alert(string id, string key) => Simple(Setting.Custom(id, SettingKind.Toggle,
        s => !s.EnabledAlertTypes.TryGetValue(key, out var on) || on,
        (s, v) => s.EnabledAlertTypes[key] = v is true,
        binds: new[] { "AppSettings.EnabledAlertTypes" }));

    private static PageDef Home() => new(Id, NavGroup.Home, new[]
    {
        new SectionDef("home.welcome", new PageItem[]
        {
            new InfoItem("home.simpleBanner", InfoSeverity.Info) { ShowIf = s => s.SimpleMode },
            new ActionRow("home.showAllRow", new[]
            {
                // Any page outside Home leaves Simple mode (ShellVM.Navigate).
                new ActionDef("home.showAll", ctx => ctx.Host.Navigate(ClientsPages.CharactersId)),
            }) { ShowIf = s => s.SimpleMode },
        }),
        new SectionDef("home.clients", new PageItem[]
        {
            new EditorItem("home.tiles", ctx => new HomeTilesEditor(ctx))
            {
                Binds = new[] { "Profile.Hotkeys", "AppSettings.ThumbnailVisibility" },
            },
        }),
        new SectionDef("home.switching", new PageItem[]
        {
            Simple(Setting.Hotkey("home.next", s => s.GlobalCycleForwardHotkey)),
            Simple(Setting.Hotkey("home.previous", s => s.GlobalCycleBackwardHotkey)),
        }),
        new SectionDef("home.thumbnails", new PageItem[]
        {
            Simple(Setting.Custom("home.size", SettingKind.Slider,
                s => s.ThumbnailStartLocation.Width,
                (s, v) => ThumbnailSize16x9.Store(s, Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.ThumbnailStartLocation" }) with
            {
                Min = ThumbnailSize16x9.MinWidth, Max = ThumbnailSize16x9.MaxWidth, Step = 1, UnitKey = "L.S.Unit.Px",
                OnChanged = ThumbnailSize16x9.Apply,
                Replaces = new[] { "TxtResizeAllW", "TxtResizeAllH", "OnApplySizeToAll" },
            }),
            Simple(Setting.Custom("home.opacity", SettingKind.Slider,
                s => OpacityPercent.FromByte(s.ThumbnailOpacity),
                (s, v) => s.ThumbnailOpacity = OpacityPercent.ToByte(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
                binds: new[] { "AppSettings.ThumbnailOpacity" }, apply: ApplyScope.Opacity) with
            {
                Min = 0, Max = 100, Step = 1, UnitKey = "L.S.Unit.Percent",
                OnChanged = ctx => ctx.Services.Thumbnails?.ApplyOpacityToAll(),   // live while dragging, as the old slider
                Replaces = new[] { "SliderOpacity" },
            }),
            new EditorItem("home.monitor", ctx => new MonitorPickerEditor(ctx, "home.monitor"))
            {
                Binds = new[] { "AppSettings.PreferredMonitor" },
                Replaces = new[] { "CmbPreferredMonitor" },
            },
        }),
        new SectionDef("home.alerts", new PageItem[]
        {
            Alert("home.alertAttack", "attack"),
            Alert("home.alertScram", "warp_scramble"),
            Alert("home.alertDecloak", "decloak"),
            Alert("home.alertFleet", "fleet_invite"),
            // Read by App.PlayAlertSound on every alert.
            Simple(Setting.Toggle("home.alertSound", s => s.EnableAlertSounds)),
        }),
        new SectionDef("home.quick", new PageItem[]
        {
            new EditorItem("home.quickActions", ctx => new QuickActionsEditor(ctx)) { Binds = new[] { "AppSettings.LockPositions" } },
        }),
        new SectionDef("home.startup", new PageItem[]
        {
            // Not in the settings file: the Windows Run key (Services/StartWithWindows), as App › General.
            Simple(Setting.Custom("home.startWithWindows", SettingKind.Toggle,
                _ => StartWithWindows.IsEnabled(),
                (_, v) => StartWithWindows.Set(v is true),
                binds: Array.Empty<string>()) with { External = true }),
        }),
    });
}

/// <summary>Home's one size slider (spec §5): a width, the height kept at 16:9, applied to every
/// thumbnail like the old "Apply to all" button.</summary>
public static class ThumbnailSize16x9
{
    public const double MinWidth = 80, MaxWidth = 1600;

    public static int HeightFor(double width) => (int)Math.Round(width * 9 / 16);

    public static void Store(AppSettings s, double width)
    {
        s.ThumbnailStartLocation.Width = Math.Round(width);
        s.ThumbnailStartLocation.Height = HeightFor(width);
    }

    private static IDebounceTimer? _timer;

    /// <summary>The slider commits every 150 ms; each ApplySizeToAll pushes an undo step and saves
    /// every position, so one drag would flood the undo history. Debounced to once per gesture.</summary>
    public static Func<IDebounceTimer> TimerFactory { get; set; } = () => new DispatcherDebounceTimer(TimeSpan.FromMilliseconds(500));

    /// <summary>Restarts the 500 ms timer; when it elapses, ThumbnailManager.ApplySizeToAll resizes
    /// every live thumbnail once, to the size stored at that moment (one undo step, saves).</summary>
    public static void Apply(SettingsContext ctx) =>
        Schedule(_timer ??= TimerFactory(), () => ((int)ctx.S.ThumbnailStartLocation.Width, (int)ctx.S.ThumbnailStartLocation.Height),
                 (w, h) => ctx.Services.Thumbnails?.ApplySizeToAll(w, h));

    public static void Schedule(IDebounceTimer timer, Func<(int W, int H)> size, Action<int, int> apply) =>
        timer.Restart(() => { var (w, h) = size(); apply(w, h); });
}
