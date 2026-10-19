using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>What reading a code gave: the value, or the catalog key of what to tell the player ("" when it read).</summary>
public sealed record ShareCodeRead<T>(T? Value, string ErrorKey) where T : class;

/// <summary>IDEAS #12: a saved layout, or the look of a profile, as one line of text a player can paste in Discord: a
/// prefix, then standard base64 (Discord turns base64url's "_" into italics) of deflated JSON. Reading one is strict: a
/// size cap before and after inflating (read up to the cap, so a "zip bomb" stops there), a fixed shape (an unknown,
/// missing or null member refuses the code), every number and text inside its range, nothing that is a path. A layout
/// code holds the places only: never hidden thumbnails or the crop switch.</summary>
public static class ShareCode
{
    public const string LayoutPrefix = "EMP-L1:", LookPrefix = "EMP-P1:";
    public const int MaxCodeLength = 8192, MaxJsonBytes = 16384, MaxSlots = 64, MaxNameLength = 64, MaxMonitor = 15;
    /// <summary>A message in Discord takes 2,000 characters; a code longer than this is flagged after the copy (leaving room for the words around it).</summary>
    public const int DiscordSafeLength = 1900;
    public const string NotACode = "L.S.Code.NotACode", IsLayout = "L.S.Code.IsLayout", IsLook = "L.S.Code.IsLook", Damaged = "L.S.Code.Damaged";

    internal static readonly JsonSerializerOptions Strict = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        AllowDuplicateProperties = false,
        MaxDepth = 4,
    };

    internal sealed class LayoutCode
    {
        [JsonPropertyName("n")] public required string Name { get; init; }
        [JsonPropertyName("s")] public required List<SlotCode> Slots { get; init; }
    }

    internal sealed class SlotCode
    {
        [JsonPropertyName("c")] public required string Character { get; init; }
        [JsonPropertyName("m")] public required int Monitor { get; init; }
        [JsonPropertyName("x")] public required double X { get; init; }
        [JsonPropertyName("y")] public required double Y { get; init; }
        [JsonPropertyName("w")] public required double W { get; init; }
        [JsonPropertyName("h")] public required double H { get; init; }
    }

    /// <summary>A layout as a code: its name and each place's character, monitor and share of the work area, rounded to four
    /// places. Null when it has no place or more than a code holds.</summary>
    public static string? Layout(LayoutPreset preset)
    {
        if (preset.Slots.Count is 0 or > MaxSlots) return null;
        var name = SafeName(preset.Name);
        var code = new LayoutCode
        {
            Name = name.Length > 0 ? name : "Layout",
            Slots = preset.Slots.Select(s => new SlotCode
            {
                Character = CleanName(PlainText.OneLine(s.Character, MaxNameLength).Trim()) ?? "",
                Monitor = s.Monitor is >= 0 and <= MaxMonitor ? s.Monitor : 0,
                X = Fraction(s.Fx, MinPosition, MaxPosition, 0),
                Y = Fraction(s.Fy, MinPosition, MaxPosition, 0),
                W = Fraction(s.Fw, MinSize, MaxSize, 0.1),
                H = Fraction(s.Fh, MinSize, MaxSize, 0.1),
            }).ToList(),
        };
        return Pack(LayoutPrefix, JsonSerializer.SerializeToUtf8Bytes(code, Strict));
    }

    public static ShareCodeRead<LayoutPreset> ReadLayout(string? text)
    {
        var json = Unpack(text, LayoutPrefix, out var error);
        if (json == null) return new(null, error);
        LayoutCode? code;
        try { code = JsonSerializer.Deserialize<LayoutCode>(json, Strict); }
        catch (JsonException) { return new(null, Damaged); }
        if (code?.Slots == null || code.Slots.Count is 0 or > MaxSlots || code.Name == null || CleanName(code.Name) != code.Name)
            return new(null, Damaged);
        var preset = new LayoutPreset { Name = code.Name, IncludesVisibility = false, CropsEnabled = false };
        foreach (var slot in code.Slots)
        {
            if (slot == null || !ValidSlot(slot.Character, slot.Monitor, slot.X, slot.Y, slot.W, slot.H)) return new(null, Damaged);
            preset.Slots.Add(new LayoutSlot { Character = slot.Character, Monitor = slot.Monitor, Fx = slot.X, Fy = slot.Y, Fw = slot.W, Fh = slot.H });
        }
        return new(preset, "");
    }

    /// <summary>The rule a place must pass, in a code or a layout file: a character that is empty or passes
    /// <see cref="CleanName"/>, a monitor 0 to 15, a position from -1 to 2 and a size above 0 up to 2 (shares of the work
    /// area; NaN passes none of them).</summary>
    public static bool ValidSlot(string? character, int monitor, double x, double y, double w, double h) =>
        character != null && (character.Length == 0 || CleanName(character) == character)
        && monitor is >= 0 and <= MaxMonitor
        && Within(x, -1, 2) && Within(y, -1, 2) && w > 0 && w <= 2 && h > 0 && h <= 2;

    /// <summary>A profile's look as a code (ProfileLook: no character, position, hotkey or path).</summary>
    public static string Look(Profile profile) => Pack(LookPrefix, JsonSerializer.SerializeToUtf8Bytes(ProfileLook.Of(profile), Strict));

    public static ShareCodeRead<ProfileLook> ReadLook(string? text)
    {
        var json = Unpack(text, LookPrefix, out var error);
        if (json == null) return new(null, error);
        try
        {
            var look = JsonSerializer.Deserialize<ProfileLook>(json, Strict);
            return look != null && look.IsValid() ? new(look, "") : new(null, Damaged);
        }
        catch (JsonException) { return new(null, Damaged); }
    }

    // A layout's name becomes a file name when it is saved from the Your layouts card: nothing that makes a path.
    private static readonly char[] PathCharacters = "\\/:*?\"<>|".ToCharArray();

    /// <summary>A name a code may hold: 1 to 64 characters, no control, format or line-separator character, no lone
    /// surrogate, no space at either end, none of <c>\ / : * ? " &lt; &gt; |</c>, no dot at the end (Windows drops it from a
    /// file name). Null otherwise.</summary>
    internal static string? CleanName(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxNameLength || text.Trim().Length != text.Length) return null;
        if (text[^1] == '.' || text.IndexOfAny(PathCharacters) >= 0) return null;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar) return null;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return null;
        }
        return text;
    }

    /// <summary>A layout's name made safe to hold in a code or a file name, keeping its words: one line, path characters
    /// inside it become "-", path characters at its ends and dots at its end are dropped, no more than 64 characters. "" when nothing is
    /// left; what it returns otherwise always passes <see cref="CleanName"/>.</summary>
    public static string SafeName(string? name)
    {
        var text = PlainText.OneLine(name, MaxNameLength).Trim();
        text = text.TrimStart(PathCharacters.Append(' ').ToArray()).TrimEnd(PathCharacters.Append(' ').Append('.').ToArray());
        foreach (var c in PathCharacters) text = text.Replace(c, '-');
        return CleanName(text) ?? "";
    }

    private static bool Within(double v, double min, double max) => v >= min && v <= max;

    // The range a code holds a place in; a layout file's places are clamped to the same one.
    public const double MinPosition = -1, MaxPosition = 2, MinSize = 0.0001, MaxSize = 2;

    /// <summary>A finite position or size held to the code's range (<see cref="MinPosition"/>..<see cref="MaxPosition"/> for a
    /// position, <see cref="MinSize"/>..<see cref="MaxSize"/> for a size), unrounded.</summary>
    public static double ClampPosition(double v) => Math.Clamp(v, MinPosition, MaxPosition);
    public static double ClampSize(double v) => Math.Clamp(v, MinSize, MaxSize);

    private static double Fraction(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Round(Math.Clamp(v, min, max), 4) : fallback;

    private static string Pack(string prefix, byte[] json)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(json);
        return prefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>The JSON inside a pasted code, or null with the reason: white space and backticks around and inside it are
    /// taken out (Discord's code formatting, line breaks of a message); a code of the other kind says so.</summary>
    private static byte[]? Unpack(string? text, string prefix, out string error)
    {
        error = NotACode;
        if (text == null) return null;
        var compact = new StringBuilder(Math.Min(text.Length, MaxCodeLength + 1));
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == '`') continue;
            compact.Append(c);
            if (compact.Length > MaxCodeLength + prefix.Length) break;
        }
        var t = compact.ToString();
        var other = prefix == LayoutPrefix ? LookPrefix : LayoutPrefix;
        if (t.StartsWith(other, StringComparison.Ordinal)) { error = other == LookPrefix ? IsLook : IsLayout; return null; }
        if (!t.StartsWith(prefix, StringComparison.Ordinal)) return null;
        error = Damaged;
        if (t.Length > MaxCodeLength) return null;
        var body = t[prefix.Length..];
        var packed = new byte[body.Length];
        if (!Convert.TryFromBase64String(body, packed, out int length)) return null;
        try { return Inflate(packed.AsSpan(0, length).ToArray(), MaxJsonBytes); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Inflates at most <paramref name="cap"/> bytes: one byte more means the code is refused.</summary>
    private static byte[]? Inflate(byte[] packed, int cap)
    {
        using var deflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        var buffer = new byte[cap + 1];
        int total = 0, read;
        while (total < buffer.Length && (read = deflate.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        return total > cap ? null : buffer.AsSpan(0, total).ToArray();
    }
}

/// <summary>IDEAS #12: how a profile's thumbnails look and when they hide: twenty values of its Thumbnail Settings, the
/// part of a profile a code carries. Nothing in it names a character, a file or a key. Of holds every value to its range
/// (or a new profile's default), so a code MultiPreview makes always reads back; IsValid is what a pasted one must pass.</summary>
public sealed class ProfileLook
{
    public const int MaxOpacity = 255, MinHighlight = 1, MaxBorder = 30, MinTextSize = 6, MaxTextSize = 48;
    public const double MaxMargin = 500;
    // \z, not $: "$" also matches before a final line break, and "Arial" + a line break must not pass.
    private static readonly Regex Colour = new(@"^(#|0x)([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\z", RegexOptions.CultureInvariant);
    private static readonly Regex Font = new(@"^[\p{L}\p{N} ._'&()\-]{1,64}\z", RegexOptions.CultureInvariant);
    private static readonly string[] NotLoggedInMarks = { "none", "text", "border", "dim" };

    public required bool ShowAllColoredBorders { get; init; }
    public required bool HideThumbnailsOnLostFocus { get; init; }
    public required bool ShowThumbnailsAlwaysOnTop { get; init; }
    public required bool KeepThumbnailsAboveClients { get; init; }
    public required int ThumbnailOpacity { get; init; }
    public required bool OpacityOnHover { get; init; }
    public required int ClientHighlightBorderThickness { get; init; }
    public required string ClientHighlightColor { get; init; }
    public required bool ShowClientHighlightBorder { get; init; }
    public required bool HighlightOverridesGroupColor { get; init; }
    public required string ThumbnailTextFont { get; init; }
    public required int ThumbnailTextSize { get; init; }
    public required string ThumbnailTextColor { get; init; }
    public required bool ShowThumbnailTextOverlay { get; init; }
    public required double TextMarginX { get; init; }
    public required double TextMarginY { get; init; }
    public required int InactiveClientBorderThickness { get; init; }
    public required string InactiveClientBorderColor { get; init; }
    public required string NotLoggedInIndicator { get; init; }
    public required string NotLoggedInColor { get; init; }

    public static ProfileLook Of(Profile p)
    {
        var d = new Profile();
        return new ProfileLook
        {
            ShowAllColoredBorders = p.ShowAllColoredBorders,
            HideThumbnailsOnLostFocus = p.HideThumbnailsOnLostFocus,
            ShowThumbnailsAlwaysOnTop = p.ShowThumbnailsAlwaysOnTop,
            KeepThumbnailsAboveClients = p.KeepThumbnailsAboveClients,
            ThumbnailOpacity = Math.Clamp(p.ThumbnailOpacity, 0, MaxOpacity),
            OpacityOnHover = p.OpacityOnHover,
            ClientHighlightBorderThickness = Math.Clamp(p.ClientHighlightBorderThickness, MinHighlight, MaxBorder),
            ClientHighlightColor = ColourOr(p.ClientHighlightColor, d.ClientHighlightColor),
            ShowClientHighlightBorder = p.ShowClientHighlightBorder,
            HighlightOverridesGroupColor = p.HighlightOverridesGroupColor,
            ThumbnailTextFont = IsFont(p.ThumbnailTextFont) ? p.ThumbnailTextFont : d.ThumbnailTextFont,
            ThumbnailTextSize = int.TryParse(p.ThumbnailTextSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                ? Math.Clamp(size, MinTextSize, MaxTextSize)
                : int.Parse(d.ThumbnailTextSize, CultureInfo.InvariantCulture),
            ThumbnailTextColor = ColourOr(p.ThumbnailTextColor, d.ThumbnailTextColor),
            ShowThumbnailTextOverlay = p.ShowThumbnailTextOverlay,
            TextMarginX = Margin(p.ThumbnailTextMargins?.X, d.ThumbnailTextMargins.X),
            TextMarginY = Margin(p.ThumbnailTextMargins?.Y, d.ThumbnailTextMargins.Y),
            InactiveClientBorderThickness = Math.Clamp(p.InactiveClientBorderThickness, 0, MaxBorder),
            InactiveClientBorderColor = string.IsNullOrEmpty(p.InactiveClientBorderColor) ? "" : ColourOr(p.InactiveClientBorderColor, d.InactiveClientBorderColor),
            NotLoggedInIndicator = NotLoggedInMarks.Contains(p.NotLoggedInIndicator) ? p.NotLoggedInIndicator : d.NotLoggedInIndicator,
            NotLoggedInColor = ColourOr(p.NotLoggedInColor, d.NotLoggedInColor),
        };
    }

    /// <summary>Puts the look on a profile and nothing else of it.</summary>
    public void ApplyTo(Profile p)
    {
        p.ShowAllColoredBorders = ShowAllColoredBorders;
        p.HideThumbnailsOnLostFocus = HideThumbnailsOnLostFocus;
        p.ShowThumbnailsAlwaysOnTop = ShowThumbnailsAlwaysOnTop;
        p.KeepThumbnailsAboveClients = KeepThumbnailsAboveClients;
        p.ThumbnailOpacity = ThumbnailOpacity;
        p.OpacityOnHover = OpacityOnHover;
        p.ClientHighlightBorderThickness = ClientHighlightBorderThickness;
        p.ClientHighlightColor = ClientHighlightColor;
        p.ShowClientHighlightBorder = ShowClientHighlightBorder;
        p.HighlightOverridesGroupColor = HighlightOverridesGroupColor;
        p.ThumbnailTextFont = ThumbnailTextFont;
        p.ThumbnailTextSize = ThumbnailTextSize.ToString(CultureInfo.InvariantCulture);
        p.ThumbnailTextColor = ThumbnailTextColor;
        p.ShowThumbnailTextOverlay = ShowThumbnailTextOverlay;
        p.ThumbnailTextMargins = new ThumbnailMargins { X = TextMarginX, Y = TextMarginY };
        p.InactiveClientBorderThickness = InactiveClientBorderThickness;
        p.InactiveClientBorderColor = InactiveClientBorderColor;
        p.NotLoggedInIndicator = NotLoggedInIndicator;
        p.NotLoggedInColor = NotLoggedInColor;
    }

    public bool IsValid() =>
        ThumbnailOpacity is >= 0 and <= MaxOpacity
        && ClientHighlightBorderThickness is >= MinHighlight and <= MaxBorder
        && InactiveClientBorderThickness is >= 0 and <= MaxBorder
        && ThumbnailTextSize is >= MinTextSize and <= MaxTextSize
        && TextMarginX >= 0 && TextMarginX <= MaxMargin && TextMarginY >= 0 && TextMarginY <= MaxMargin
        && IsColour(ClientHighlightColor) && IsColour(ThumbnailTextColor) && IsColour(NotLoggedInColor)
        && (InactiveClientBorderColor?.Length == 0 || IsColour(InactiveClientBorderColor))
        && IsFont(ThumbnailTextFont) && NotLoggedInMarks.Contains(NotLoggedInIndicator);

    private static bool IsColour(string? c) => c != null && Colour.IsMatch(c);
    private static bool IsFont(string? f) => f != null && Font.IsMatch(f);
    private static string ColourOr(string? c, string fallback) => IsColour(c) ? c! : fallback;

    private static double Margin(double? v, double fallback) =>
        v is double m && double.IsFinite(m) ? Math.Round(Math.Clamp(m, 0, MaxMargin), 2) : fallback;
}
