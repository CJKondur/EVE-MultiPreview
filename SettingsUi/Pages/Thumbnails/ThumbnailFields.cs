using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EveMultiPreview.SettingsUi;

/// <summary>Rules of the Thumbnails cards that aren't a plain property (spec §5 Thumbnails).
/// Reading never writes.</summary>
public static class ThumbnailFields
{
    // ── Colours ──

    private static readonly Regex Hex6 = new("^(#|0[xX])?[0-9A-Fa-f]{6}$");

    /// <summary>ThumbnailManager.ParseColor paints only #RRGGBB, 0xRRGGBB and RRGGBB; anything else
    /// (rgb(), #RGB, #AARRGGBB, names) comes out grey. Empty is allowed where it means "none".</summary>
    public static bool IsPaintable(string? colour, bool allowEmpty = false)
    {
        var text = colour?.Trim() ?? "";
        return text.Length == 0 ? allowEmpty : Hex6.IsMatch(text);
    }

    /// <summary>Trimmed (ParseColor doesn't trim), and the picker's #RRGGBB stored as 0xrrggbb, the
    /// form the old window stored.</summary>
    public static string StoreColour(string? colour) => CharacterFields.Normalize(colour?.Trim() ?? "");

    // ── Sizes stored as text (ThumbnailTextSize, ProcessStatsTextSize) ──

    /// <summary>The size in the text, or <paramref name="fallback"/>, the size ThumbnailManager uses
    /// when it can't read it.</summary>
    public static double ReadSize(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v
        : double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) && v > 0 ? v
        : fallback;

    /// <summary>A whole number, written the same in every language. ThumbnailManager reads these with
    /// the current culture and CropManager with the invariant one; "12" is 12 to both.</summary>
    public static string WriteSize(object? value) =>
        ((int)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture);

    // ── Hover zoom (HoverScale 1.5 = 150 %) ──

    /// <summary>ThumbnailWindow treats a scale of 1.0 or less as no zoom; the old box had no limit.</summary>
    public const double ZoomMin = 110, ZoomMax = 300;

    /// <summary>Whole percent, clamped to the slider, so the slider never moves a stored value itself.</summary>
    public static double ZoomPercent(double scale) => Math.Clamp(Math.Round(scale * 100), ZoomMin, ZoomMax);

    public static double ZoomScale(double percent) => Math.Round(Math.Clamp(percent, ZoomMin, ZoomMax)) / 100.0;

    // ── Typed whole numbers ──

    /// <summary>The player's language first, then the invariant form.</summary>
    public static bool TryWhole(string? text, out int value)
    {
        var t = text?.Trim() ?? "";
        var current = Grouped(t, CultureInfo.CurrentCulture, out var currentStyle);
        var invariant = Grouped(t, CultureInfo.InvariantCulture, out var invariantStyle);
        return int.TryParse(current, NumberStyles.Integer | currentStyle, CultureInfo.CurrentCulture, out value)
            || int.TryParse(invariant, NumberStyles.Integer | invariantStyle, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>AllowThousands only when the text really is grouped: every group after the first is exactly three digits
    /// and the integer part ends there or at the decimal separator. .NET doesn't check group sizes, so "1.5" would
    /// otherwise read as 15 in de-DE. The one rule for every number box.</summary>
    public static string Grouped(string text, CultureInfo culture, out NumberStyles style)
    {
        style = NumberStyles.None;
        var sep = culture.NumberFormat.NumberGroupSeparator;
        // fr groups with U+202F and ru with U+00A0, but players type a plain space: all three spaces are one separator.
        if (sep.Length == 1 && sep[0] is ' ' or ' ' or ' ')
            text = text.Replace(' ', sep[0]).Replace(' ', sep[0]).Replace(' ', sep[0]);
        if (sep.Length == 0 || !text.Contains(sep, StringComparison.Ordinal)) return text;
        var s = System.Text.RegularExpressions.Regex.Escape(sep);
        var d = System.Text.RegularExpressions.Regex.Escape(culture.NumberFormat.NumberDecimalSeparator);
        if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"^[+-]?\d{{1,3}}({s}\d{{3}})+($|{d})")) style = NumberStyles.AllowThousands;
        return text;
    }

    // ── The nine anchor points (CycleExclusionBadgePosition; Plan 3c reuses them for AlertTextPosition) ──

    private static readonly string[] Anchors = { "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight" };

    public static IReadOnlyList<ChoiceOption> AnchorOptions { get; } =
        Anchors.Select(a => new ChoiceOption(a, "L.S.Opt.Anchor" + a)).ToList();

    /// <summary>The old list showed an empty or unknown position as top left (and wrote nothing).</summary>
    public static string Anchor(string? stored) => stored != null && Array.IndexOf(Anchors, stored) >= 0 ? stored : "TopLeft";

    // ── Login mark (NotLoggedInIndicator) ──

    /// <summary>ThumbnailWindow.SetNotLoggedIn uses the colour only for "border" (and the older "color").</summary>
    public static bool UsesLoginColour(string? style) =>
        string.Equals(style, "border", StringComparison.OrdinalIgnoreCase) || string.Equals(style, "color", StringComparison.OrdinalIgnoreCase);
}
