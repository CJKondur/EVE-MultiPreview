using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace EveMultiPreview.Services;

/// <summary>A small mark on a button that tells a pair of verbs with one glyph apart: Off is a slash across the glyph, On a dot in
/// the lower-right corner, Thirty the number 30 there (a 30-minute mute). A toggle has none.</summary>
public enum IconBadge { None, On, Off, Thirty }

/// <summary>IDEAS #15 and Plan 38: the pictures on the Stream Deck shortcuts MultiPreview writes. A character's button is its
/// initials on its group colour, an action's button is one glyph of the Segoe icon font; both are drawn here, locally, into a
/// PNG and wrapped in a one-entry .ico (no network, no package). Drawing is deterministic: the same text, colour and size give
/// the same bytes, so a test can pin them and the shortcut folder can name an icon by a hash of what it shows. A name is player
/// text: it is only ever drawn, never used as a format string, and never logged.</summary>
public static class ButtonIcon
{
    private const string DefaultBackground = "#3A3A3A";
    private const string Dark = "#111111";
    private const string Light = "#FFFFFF";
    private const int MaxElements = 8;

    private static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };

    /// <summary>The two letters a character or profile name wears on its button: the first letter of the first two words
    /// (split at white space and hyphens), or, for one word, its first two letters. Letters and digits only, upper-cased
    /// invariantly; a surrogate pair or a letter with its accent marks is one letter. Nothing usable gives "?".</summary>
    public static string Initials(string? name)
    {
        var words = new System.Collections.Generic.List<System.Collections.Generic.List<string>>(2);
        var current = new System.Collections.Generic.List<string>(2);
        void EndWord()
        {
            if (current.Count > 0 && words.Count < 2) words.Add(current);
            current = new System.Collections.Generic.List<string>(2);
        }

        if (!string.IsNullOrEmpty(name))
        {
            var elements = StringInfo.GetTextElementEnumerator(name);
            while (elements.MoveNext() && words.Count < 2)
            {
                var element = (string)elements.Current;
                if (element.Length == 1 && (char.IsWhiteSpace(element[0]) || element[0] == '-')) { EndWord(); continue; }
                if (!Rune.TryGetRuneAt(element, 0, out var first) || !Rune.IsLetterOrDigit(first)) continue;
                if (current.Count < 2) current.Add(element);
            }
            EndWord();
        }

        if (words.Count == 0) return "?";
        var letters = words.Count >= 2 ? new[] { words[0][0], words[1][0] } : words[0].ToArray();
        return string.Concat(letters).ToUpperInvariant();
    }

    /// <summary>A one-entry ICO around a PNG: the 6-byte header, one 16-byte entry (256 pixels is stored as 0; a smaller picture
    /// states its size), then the PNG at offset 22.</summary>
    public static byte[] Ico(byte[] png)
    {
        if (png == null || png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            throw new ArgumentException("Not a PNG.", nameof(png));
        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        var ico = new byte[22 + png.Length];
        ico[2] = 1; ico[4] = 1;                                  // type icon, one image
        ico[6] = width is > 0 and < 256 ? (byte)width : (byte)0; // 0 = 256
        ico[7] = height is > 0 and < 256 ? (byte)height : (byte)0;
        ico[10] = 1;                                             // planes
        ico[12] = 32;                                            // bits per pixel
        BitConverter.GetBytes(png.Length).CopyTo(ico, 14);
        BitConverter.GetBytes(22).CopyTo(ico, 18);
        png.CopyTo(ico, 22);
        return ico;
    }

    /// <summary>The name of the icon file for these pictures: twelve hex characters of a hash of what is drawn (the text, glyph or
    /// not, the colour as upper-case #RRGGBB with a bad or missing one as the default grey, and the badge), so it is a safe file
    /// name whatever a character is called, and the same picture is the same file. The hash is of the inputs, not of the bytes:
    /// a change to how icons are drawn keeps the name, so the folder writer overwrites rather than skips an existing file.</summary>
    public static string FileName(string text, bool glyph, string? backgroundHex, IconBadge badge = IconBadge.None)
    {
        var bg = ParseHex(backgroundHex) ?? ParseHex(DefaultBackground)!.Value;
        var key = $"v2|{text}|{(glyph ? 1 : 0)}|#{bg.R:X2}{bg.G:X2}{bg.B:X2}|{badge}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant() + ".ico";
    }

    /// <summary>The text colour for a background: white or #111111, whichever has the higher contrast. A background that is no
    /// #RRGGBB is the default grey.</summary>
    public static string TextColourFor(string? backgroundHex)
    {
        var bg = ParseHex(backgroundHex) ?? ParseHex(DefaultBackground)!.Value;
        double onLight = Contrast(Luminance(bg), Luminance(ParseHex(Light)!.Value));
        double onDark = Contrast(Luminance(bg), Luminance(ParseHex(Dark)!.Value));
        return onDark > onLight ? Dark : Light;
    }

    /// <summary>A rounded square in <paramref name="backgroundHex"/> (#RRGGBB; anything else is #3A3A3A) with the text centred
    /// on it, as the bytes of a PNG. <paramref name="glyph"/> draws the text in the Segoe icon font, otherwise in Segoe UI
    /// Semibold; <paramref name="badge"/> adds its mark. WPF drawing needs an STA thread: a caller on another kind of thread gets one made for the call.</summary>
    public static byte[] RenderPng(string text, bool glyph, string? backgroundHex, int size = 256, IconBadge badge = IconBadge.None)
    {
        size = Math.Clamp(size, 16, 256);
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return Draw(text, glyph, backgroundHex, size, badge);
        byte[]? bytes = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { bytes = Draw(text, glyph, backgroundHex, size, badge); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return bytes!;
    }

    private static byte[] Draw(string? text, bool glyph, string? backgroundHex, int size, IconBadge badge)
    {
        var bg = ParseHex(backgroundHex) ?? ParseHex(DefaultBackground)!.Value;
        var fg = ParseHex(TextColourFor(backgroundHex))!.Value;
        var shown = Clip(string.IsNullOrEmpty(text) ? "?" : text);

        var family = glyph ? new FontFamily(string.Join(", ", IconFonts)) : new FontFamily("Segoe UI Semibold");
        var typeface = new Typeface(family, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        FormattedText Format(double fontSize) => new(shown, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            typeface, fontSize, Solid(fg), 1.0);

        double elements = new StringInfo(shown).LengthInTextElements;
        // A corner badge needs room: the picture is a little smaller and moves up and to the left.
        bool corner = badge is IconBadge.On or IconBadge.Thirty;
        double fontSize = size * (glyph ? (corner ? 0.44 : 0.52) : elements <= 1 ? (corner ? 0.48 : 0.56) : (corner ? 0.38 : 0.44));
        double shift = corner ? -size * 0.05 : 0;
        var formatted = Format(fontSize);
        double room = size * (corner ? 0.62 : 0.78);
        if (formatted.Width > room) formatted = Format(fontSize * room / formatted.Width);

        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);
        using (var dc = visual.RenderOpen())
        {
            double radius = size * 0.18;
            dc.DrawRoundedRectangle(Solid(bg), null, new System.Windows.Rect(0, 0, size, size), radius, radius);
            dc.DrawText(formatted, new Point((size - formatted.Width) / 2 + shift, (size - formatted.Height) / 2 + shift));
            DrawBadge(dc, badge, size, bg, fg);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The badge, each part ringed in the background colour so it stays legible over the picture.</summary>
    private static void DrawBadge(DrawingContext dc, IconBadge badge, int size, Color bg, Color fg)
    {
        if (badge == IconBadge.None) return;
        Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        System.Windows.Media.Pen Line(Color c, double width)
        {
            var pen = new System.Windows.Media.Pen(Solid(c), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            return pen;
        }
        if (badge == IconBadge.Off)
        {
            var from = new Point(size * 0.22, size * 0.22);
            var to = new Point(size * 0.78, size * 0.78);
            dc.DrawLine(Line(bg, size * 0.115), from, to);
            dc.DrawLine(Line(fg, size * 0.05), from, to);
        }
        else if (badge == IconBadge.On)
        {
            var centre = new Point(size * 0.73, size * 0.73);
            dc.DrawEllipse(Solid(bg), null, centre, size * 0.13, size * 0.13);
            dc.DrawEllipse(Solid(fg), null, centre, size * 0.1, size * 0.1);
        }
        else
        {
            var typeface = new Typeface(new FontFamily("Segoe UI Semibold"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            var number = new FormattedText("30", CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, typeface, size * 0.27, Solid(fg), 1.0);
            var shape = number.BuildGeometry(new Point(size * 0.89 - number.Width, size * 0.89 - number.Height));
            dc.DrawGeometry(null, Line(bg, size * 0.045), shape);
            dc.DrawGeometry(Solid(fg), null, shape);
        }
    }

    /// <summary>At most <see cref="MaxElements"/> text elements, never cutting a pair or an accented letter in half.</summary>
    private static string Clip(string text)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= MaxElements ? text : info.SubstringByTextElements(0, MaxElements);
    }

    /// <summary>What a verb's button shows. Verbs with a glyph give the glyph (when the icon font has it: a font that lacks it
    /// gives <see cref="LabelOf"/> instead, as plain text); a verb with no glyph of its own (Focus and Profile, whose button wears
    /// the name's <see cref="Initials"/>, and the pipe-only queries) gives that label too. Pair it with <see cref="BadgeOf"/>.</summary>
    public static (string Text, bool Glyph) For(RemoteVerb verb, Func<string, bool>? fontHas = null)
    {
        var glyph = GlyphOf(verb);
        Func<string, bool> has = fontHas ?? IconFontHas;
        if (glyph != null && has(glyph)) return (glyph, true);
        return (LabelOf(verb), false);
    }

    /// <summary>The mark that tells a verb from the others with its glyph: Off on the "off" verbs, On on the "on" verbs, 30 on the
    /// 30-minute mute; none on a toggle or a verb with a glyph of its own.</summary>
    public static IconBadge BadgeOf(RemoteVerb verb) => verb switch
    {
        RemoteVerb.DoNotDisturbOn or RemoteVerb.FocusModeOn or RemoteVerb.NameBarOn => IconBadge.On,
        RemoteVerb.DoNotDisturbOff or RemoteVerb.FocusModeOff or RemoteVerb.NameBarOff => IconBadge.Off,
        RemoteVerb.Mute30 => IconBadge.Thirty,
        _ => IconBadge.None,
    };

    /// <summary>The capital letters (and digits) of a verb's name: CycleGroupBack is CGB, MarkAlertsSeen MAS, Focus F. What a
    /// button shows when the icon font lacks its glyph.</summary>
    public static string LabelOf(RemoteVerb verb) => new(verb.ToString().Where(c => char.IsUpper(c) || char.IsDigit(c)).ToArray());

    /// <summary>The glyph of a verb, or null when it has none.</summary>
    public static string? GlyphOf(RemoteVerb verb) => verb switch
    {
        RemoteVerb.CycleGroup => "\uE72A",
        RemoteVerb.CycleGroupBack => "\uE72B",
        RemoteVerb.PreviousClient => "\uE7A7",
        RemoteVerb.LastAlert => "\uEA8F",
        RemoteVerb.DoNotDisturb or RemoteVerb.DoNotDisturbOn or RemoteVerb.DoNotDisturbOff => "\uE708",
        RemoteVerb.FocusMode or RemoteVerb.FocusModeOn or RemoteVerb.FocusModeOff => "\uE71D",
        RemoteVerb.OpenSettings => "\uE713",
        RemoteVerb.OpenFleetBoard => "\uE716",
        RemoteVerb.OpenAlertHistory => "\uE81C",
        RemoteVerb.NameBar or RemoteVerb.NameBarOn or RemoteVerb.NameBarOff => "\uE8A9",
        RemoteVerb.MarkAlertsSeen => "\uE8FB",
        RemoteVerb.MinimizeClients => "\uE921",
        RemoteVerb.RestoreClients => "\uE923",
        // Verbs the plan's list does not name: the Symbol enum's own code points (Previous, Next, Mute, Volume, View) and chevrons.
        RemoteVerb.ToggleThumbnails => "\uE7B3",
        RemoteVerb.CycleAll => "\uE76C",
        RemoteVerb.CycleAllBack => "\uE76B",
        RemoteVerb.NextProfile => "\uE101",
        RemoteVerb.PreviousProfile => "\uE100",
        RemoteVerb.Mute or RemoteVerb.Mute30 => "\uE74F",
        RemoteVerb.Unmute => "\uE767",
        _ => null,
    };

    /// <summary>Does Windows have this glyph in the Segoe icon font (Fluent on Windows 11, MDL2 before)? Never throws.</summary>
    public static bool IconFontHas(string glyph)
    {
        try
        {
            if (!Rune.TryGetRuneAt(glyph, 0, out var rune)) return false;
            foreach (var name in IconFonts)
            {
                var typeface = new Typeface(new FontFamily(name), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out var gt) && gt.CharacterToGlyphMap.ContainsKey(rune.Value)) return true;
            }
        }
        catch (Exception) { /* a missing or broken font is "no" */ }
        return false;
    }

    private static Color? ParseHex(string? hex)
    {
        if (hex == null) return null;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
}
