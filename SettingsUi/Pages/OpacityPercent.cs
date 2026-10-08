using System;

namespace EveMultiPreview.SettingsUi;

/// <summary>Opacities are stored as bytes, 0–255 (ThumbnailOpacity, SecondaryThumbnailSettings.Opacity),
/// and shown as 0–100 %. Rounding both ways makes every whole percentage round-trip exactly.</summary>
public static class OpacityPercent
{
    public static int FromByte(int stored) => (int)Math.Round(stored * 100 / 255.0);
    public static int ToByte(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0);
}
