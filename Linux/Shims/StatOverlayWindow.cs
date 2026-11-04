// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The stats overlay window of the Windows app. The settings read one rule from it: which text is a colour (the Validate of
// the stats colour cards, run as the player types), so that rule is copied unchanged, not a throwing stub.
using System.Windows.Media;
using EveMultiPreview.Models;

namespace EveMultiPreview.Views;

public static class StatOverlayWindow
{
    /// <summary>The colour in a stats colour setting: what WPF reads (#RRGGBB, #AARRGGBB, a name), as before,
    /// then 0xRRGGBB, RRGGBB and rgb(r,g,b) (AppSettings.ParseColor). Null when it is none of these.</summary>
    public static Color? ParseOverlayColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return (Color)ColorConverter.ConvertFromString(text.Trim()); }
        catch { }
        try
        {
            var c = AppSettings.ParseColor(text, Colors.Transparent);
            return c == Colors.Transparent ? null : c;
        }
        catch { return null; }   // rgb(300,0,0) throws inside ParseColor
    }
}
