using System;

namespace EveMultiPreview.Services;

/// <summary>What a settings change needs re-applied live.</summary>
[Flags]
public enum ApplyScope
{
    None = 0,
    /// <summary>Only thumbnail opacity changed (cheap path).</summary>
    Opacity = 1,
    /// <summary>Anything else that affects thumbnails: full ReapplySettings.</summary>
    Thumbnails = 2,
    /// <summary>Crop definitions or visibility changed: CropManager.Refresh.</summary>
    Crops = 4,
    /// <summary>Only save. Nothing to re-apply: the setting is read where it is used,
    /// or App re-reads it when Applied fires (hotkeys, alert hub).</summary>
    Save = 8,
}
