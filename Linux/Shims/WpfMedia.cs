// Stand-ins for the two WPF colour types the shared settings code names (AppSettings.ParseColor and the EVE-O
// import). Only what that code uses.
namespace System.Windows.Media;

public struct Color
{
    public byte A, R, G, B;

    public static Color FromRgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
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
