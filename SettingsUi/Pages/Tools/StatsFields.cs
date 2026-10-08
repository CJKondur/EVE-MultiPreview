using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using EveMultiPreview.Models;

namespace EveMultiPreview.SettingsUi;

/// <summary>One number the stats overlay can show, in its group.</summary>
public sealed record StatMetricInfo(StatMetrics Bit, string Group)
{
    public string LabelKey => "L.S.Stat." + Bit;
}

/// <summary>Rules of the stats overlay cards (spec §5 Tools › Stats overlay, character page › Choose numbers).
/// Every write changes one bit or one property. Reading never writes.</summary>
public static class StatsFields
{
    public const int FontMin = 6, FontMax = 16;

    /// <summary>The old opacity slider's floor, 10 of 255, in percent (OpacityPercent).</summary>
    public const int OpacityMin = 4;

    /// <summary>The four groups, with the character page's group switch labels and masks.</summary>
    public static IReadOnlyList<(string Id, string LabelKey, StatMetrics Mask)> Groups { get; } = new[]
    {
        ("damage", "L.S.character.statsDamage", StatMetrics.DpsMask),
        ("logi", "L.S.character.statsLogi", StatMetrics.LogiMask),
        ("mining", "L.S.character.statsMining", StatMetrics.MineMask),
        ("ratting", "L.S.character.statsRatting", StatMetrics.RatMask),
    };

    /// <summary>The 19 old numbers plus the damage-by-type breakdown no old box offered (StatTrackerService reads
    /// it). IncludeNpc is left out: nothing reads it (IDEAS P32); its stored bit is kept.</summary>
    public static IReadOnlyList<StatMetricInfo> Metrics { get; } = new StatMetricInfo[]
    {
        new(StatMetrics.DpsOut, "damage"), new(StatMetrics.DpsIn, "damage"), new(StatMetrics.Tdi, "damage"),
        new(StatMetrics.Tdo, "damage"), new(StatMetrics.DpsInByType, "damage"),
        new(StatMetrics.Arps, "logi"), new(StatMetrics.Srps, "logi"), new(StatMetrics.Ctps, "logi"), new(StatMetrics.Taro, "logi"),
        new(StatMetrics.Tari, "logi"), new(StatMetrics.Tsro, "logi"), new(StatMetrics.Tsri, "logi"),
        new(StatMetrics.Ompc, "mining"), new(StatMetrics.Omph, "mining"), new(StatMetrics.Gmpc, "mining"),
        new(StatMetrics.Gmph, "mining"), new(StatMetrics.Imph, "mining"),
        new(StatMetrics.Tipt, "ratting"), new(StatMetrics.Tiph, "ratting"), new(StatMetrics.Tips, "ratting"),
    };

    /// <summary>Every bit the lists show.</summary>
    public static StatMetrics Listed { get; } = Metrics.Aggregate(StatMetrics.None, (all, m) => all | m.Bit);

    /// <summary>The old global grid's 20 boxes (ChkGmIncludeNpc set the unread IncludeNpc bit).</summary>
    public static IReadOnlyList<string> GlobalControls { get; } = new[]
    {
        "ChkGmDpsOut", "ChkGmDpsIn", "ChkGmTdi", "ChkGmTdo", "ChkGmIncludeNpc",
        "ChkGmArps", "ChkGmSrps", "ChkGmCtps", "ChkGmTaro", "ChkGmTari", "ChkGmTsro", "ChkGmTsri",
        "ChkGmOmpc", "ChkGmOmph", "ChkGmGmpc", "ChkGmGmph", "ChkGmImph",
        "ChkGmTipt", "ChkGmTiph", "ChkGmTips",
    };

    public static bool IsOn(StatMetrics set, StatMetrics bit) => (set & bit) != 0;

    /// <summary>A number for every character: only its own bit changes.</summary>
    public static void SetGlobal(AppSettings s, StatMetrics bit, bool on) =>
        s.GlobalStatMetrics = on ? s.GlobalStatMetrics | bit : s.GlobalStatMetrics & ~bit;

    /// <summary>null = default (follows the numbers for every character), true = always shown, false = never.</summary>
    public static bool? Override(AppSettings s, string name, StatMetrics bit) =>
        s.PerCharacterStats.TryGetValue(name, out var c) ? c.GetOverrideState(bit) : null;

    /// <summary>One number for one character. An entry with nothing forced is removed, as the group switches leave it.</summary>
    public static void SetOverride(AppSettings s, string name, StatMetrics bit, bool? state)
    {
        if (!s.PerCharacterStats.TryGetValue(name, out var c))
        {
            if (state == null) return;
            s.PerCharacterStats[name] = c = new CharacterStatSettings();
        }
        c.SetOverrideState(bit, state);
        if (c.ForcedOn == StatMetrics.None && c.ForcedOff == StatMetrics.None) s.PerCharacterStats.Remove(name);
    }

    /// <summary>The old editor's Reset all: every number back to the default.</summary>
    public static void ClearOverrides(AppSettings s, string name) => s.PerCharacterStats.Remove(name);

    /// <summary>How many listed numbers the character always and never shows.</summary>
    public static (int Shown, int Hidden) Counts(AppSettings s, string name) =>
        s.PerCharacterStats.TryGetValue(name, out var c)
            ? (BitOperations.PopCount((uint)(c.ForcedOn & Listed)), BitOperations.PopCount((uint)(c.ForcedOff & Listed)))
            : (0, 0);

    /// <summary>What the overlay paints: StatOverlayWindow.ParseOverlayColor (Task 1).</summary>
    public static bool IsOverlayColour(string? text) => Views.StatOverlayWindow.ParseOverlayColor(text) != null;

    /// <summary>The stored byte (0–255) in whole percent, clamped to the slider so it never writes by itself.</summary>
    public static int OpacityPercentOf(int stored) => Math.Clamp(OpacityPercent.FromByte(stored), OpacityMin, 100);
}
