// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// Stand-ins for the WPF colour types the shared settings code names (AppSettings.ParseColor, the EVE-O import and
// Setting.IsColor). Only what that code uses.
namespace System.Windows.Media;

public record struct Color(byte A, byte R, byte G, byte B)
{
    public static Color FromRgb(byte r, byte g, byte b) => new(255, r, g, b);
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
}

public static class Colors
{
    /// <summary>WPF's Transparent: #00FFFFFF.</summary>
    public static Color Transparent { get; } = new(0, 255, 255, 255);
}

public static class ColorConverter
{
    /// <summary>"#RGB", "#RRGGBB", "#AARRGGBB" or a colour name, as WPF reads them. Throws FormatException otherwise.</summary>
    public static object ConvertFromString(string value)
    {
        if (!Avalonia.Media.Color.TryParse(value, out var c)) throw new FormatException($"Not a colour: {value}");
        return Color.FromArgb(c.A, c.R, c.G, c.B);
    }
}
