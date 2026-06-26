using System.Collections.Generic;

namespace EveMultiPreview.Models;

/// <summary>
/// A named, monitor-agnostic thumbnail-wall layout. Each slot stores a character
/// and its position/size as FRACTIONS of a monitor's work area, so a preset
/// survives resolution changes and transfers across machines (export/import).
/// </summary>
public class LayoutPreset
{
    public string Name { get; set; } = "";
    public List<LayoutSlot> Slots { get; set; } = new();

    /// <summary>True if this preset also captured show/hide + crop state (set on
    /// capture). Old position-only presets leave it false so applying them never
    /// unexpectedly shows/hides thumbnails or toggles crops.</summary>
    public bool IncludesVisibility { get; set; } = false;

    /// <summary>Crop master-toggle state captured with the preset (only honored when
    /// IncludesVisibility is true).</summary>
    public bool CropsEnabled { get; set; } = false;
}

public class LayoutSlot
{
    /// <summary>Character this slot was captured for (used for exact-match on apply;
    /// shared presets fall back to positional mapping).</summary>
    public string Character { get; set; } = "";

    /// <summary>Index into Screen.AllScreens at capture time. Falls back to the
    /// primary monitor when the machine has fewer screens.</summary>
    public int Monitor { get; set; }

    public double Fx { get; set; }   // fractional X within the monitor work area (0..1)
    public double Fy { get; set; }   // fractional Y
    public double Fw { get; set; }   // fractional width
    public double Fh { get; set; }   // fractional height

    /// <summary>Whether this character's thumbnail was hidden when captured (only
    /// honored when the preset's IncludesVisibility is true).</summary>
    public bool Hidden { get; set; }
}
