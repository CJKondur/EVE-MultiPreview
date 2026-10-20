using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>IDEAS P123: what a profile code with characters (<see cref="ShareCode.ProfilePrefix"/>) holds: the profile's look
/// (<see cref="ProfileLook"/>), a list of character NAMES (the roles, 64 at most), and the per-character data the profile
/// keeps for them, each entry pointing at a role by its index in that list. Reading one is as strict as a look code: a fixed
/// shape, every number and text inside its range, no repeated role in a store, no repeated name.
///
/// Carried: <see cref="CarriedStores"/>. NOT carried, by ruling (they are the machine's or the player's own, and a code
/// from someone else must never bind a key or pin a CPU core): <see cref="NotCarriedStores"/>. A test holds the two lists
/// against <c>CharacterForget.Purge</c>, the list of every per-character store, so a store added later cannot be forgotten.
///
/// Applying (<see cref="ApplyTo"/>) puts the data on a profile that is a copy of the player's current one, for the
/// characters the player mapped (a role to a local character, or skipped). Nothing in a code creates a hotkey, names a file
/// or is logged; a character name is player text and only ever a dictionary key or a list entry.</summary>
public sealed class ProfileCode
{
    /// <summary>The per-character stores a code carries (the property names of Profile and AppSettings, as Purge names them).</summary>
    public static readonly string[] CarriedStores =
    {
        "ThumbnailVisibility", "ThumbnailPositions", "CustomColors", "SecondaryThumbnails", "Crops", "ClientPositions",
        "DontMinimizeClients", "ClientSlotExcluded", "AutoSelectCharacters", "ClientSlots", "HotkeyGroups", "Groups",
        "ThumbnailAnnotations", "ThumbnailLabelStyles", "ThumbnailGroups",
    };

    /// <summary>Per-character stores a code never carries: hotkeys (a key of someone else's), CPU masks and audio volume
    /// (this machine's), the statistics and where their windows sit, the order of the quick-switch cards.</summary>
    public static readonly string[] NotCarriedStores =
    {
        "Hotkeys", "PerClientCores", "PerClientAudioVolume", "PerCharacterStats", "StatWindowPositions", "QuickSwitchCardOrder",
    };

    public const int MaxCharacters = ShareCode.MaxSlots, MaxGroups = 64, MaxCropsPerCharacter = 8, MaxLabel = 128, MaxLabelSize = 200;
    // Positions are screen pixels (the Win32 range); sizes from 0 up to the same.
    public const double MinPixel = -32768, MaxPixel = 32767;

    [JsonPropertyName("l")] public required ProfileLook Look { get; init; }
    /// <summary>The roles: character names, each one CleanName-clean, each once (ignoring case).</summary>
    [JsonPropertyName("n")] public required List<string> Characters { get; init; }
    [JsonPropertyName("v")] public required List<HiddenCode> Visibility { get; init; }
    [JsonPropertyName("p")] public required List<RectCode> Positions { get; init; }
    [JsonPropertyName("c")] public required List<ColoursCode> Colours { get; init; }
    [JsonPropertyName("s")] public required List<SecondCode> Second { get; init; }
    [JsonPropertyName("x")] public required List<CropCode> Crops { get; init; }
    [JsonPropertyName("q")] public required List<ClientCode> ClientPositions { get; init; }
    [JsonPropertyName("m")] public required List<int> DontMinimize { get; init; }
    [JsonPropertyName("e")] public required List<int> SlotExcluded { get; init; }
    [JsonPropertyName("a")] public required List<int> Auto { get; init; }
    [JsonPropertyName("sl")] public required List<GroupCode> Slots { get; init; }
    [JsonPropertyName("hg")] public required List<GroupCode> HotkeyGroups { get; init; }
    [JsonPropertyName("g")] public required List<GroupCode> Groups { get; init; }
    [JsonPropertyName("tg")] public required List<GroupCode> ThumbnailGroups { get; init; }
    [JsonPropertyName("an")] public required List<LabelCode> Labels { get; init; }
    [JsonPropertyName("ls")] public required List<LabelStyleCode> LabelStyles { get; init; }

    public sealed class HiddenCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("v")] public required int Hidden { get; init; }
    }

    public sealed class RectCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("x")] public required double X { get; init; }
        [JsonPropertyName("y")] public required double Y { get; init; }
        [JsonPropertyName("w")] public required double W { get; init; }
        [JsonPropertyName("h")] public required double H { get; init; }
    }

    public sealed class ColoursCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("b")] public required string Border { get; init; }
        [JsonPropertyName("t")] public required string Text { get; init; }
        [JsonPropertyName("i")] public required string Inactive { get; init; }
    }

    public sealed class SecondCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("e")] public required int Enabled { get; init; }
        [JsonPropertyName("o")] public required int Opacity { get; init; }
        [JsonPropertyName("x")] public required int X { get; init; }
        [JsonPropertyName("y")] public required int Y { get; init; }
        [JsonPropertyName("w")] public required int W { get; init; }
        [JsonPropertyName("h")] public required int H { get; init; }
    }

    public sealed class CropCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("i")] public required string Id { get; init; }
        [JsonPropertyName("n")] public required string Name { get; init; }
        [JsonPropertyName("sx")] public required int SourceX { get; init; }
        [JsonPropertyName("sy")] public required int SourceY { get; init; }
        [JsonPropertyName("sw")] public required int SourceW { get; init; }
        [JsonPropertyName("sh")] public required int SourceH { get; init; }
        [JsonPropertyName("px")] public required int PopupX { get; init; }
        [JsonPropertyName("py")] public required int PopupY { get; init; }
        [JsonPropertyName("pw")] public required int PopupW { get; init; }
        [JsonPropertyName("ph")] public required int PopupH { get; init; }
        [JsonPropertyName("l")] public required bool ShowLabel { get; init; }
        [JsonPropertyName("o")] public required int Opacity { get; init; }
        [JsonPropertyName("t")] public required bool ClickThrough { get; init; }
    }

    public sealed class ClientCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("x")] public required double X { get; init; }
        [JsonPropertyName("y")] public required double Y { get; init; }
        [JsonPropertyName("w")] public required double W { get; init; }
        [JsonPropertyName("h")] public required double H { get; init; }
        [JsonPropertyName("m")] public required int Maximized { get; init; }
    }

    /// <summary>A slot, cycle group, colour group or thumbnail group: its name, its colour (groups only, else ""), and the
    /// roles in it.</summary>
    public sealed class GroupCode
    {
        [JsonPropertyName("n")] public required string Name { get; init; }
        [JsonPropertyName("c")] public required string Colour { get; init; }
        [JsonPropertyName("r")] public required List<int> Roles { get; init; }
    }

    public sealed class LabelCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("t")] public required string Text { get; init; }
    }

    public sealed class LabelStyleCode
    {
        [JsonPropertyName("r")] public required int Role { get; init; }
        [JsonPropertyName("c")] public required string Colour { get; init; }
        [JsonPropertyName("s")] public required int Size { get; init; }
    }

    // ── Reading ─────────────────────────────────────────────────────────

    /// <summary>What a pasted code must pass. Every list, count, number and text is bounded; a null anywhere refuses it.</summary>
    public bool IsValid()
    {
        if (Look == null || !Look.IsValid() || Characters == null || Characters.Count > MaxCharacters) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Characters)
            if (name == null || ShareCode.CleanName(name) != name || !seen.Add(name)) return false;
        int n = Characters.Count;
        return OnePerRole(Visibility, n, v => v.Role, v => v.Hidden is 0 or 1)
            && OnePerRole(Positions, n, p => p.Role, p => Pixel(p.X) && Pixel(p.Y) && Size(p.W) && Size(p.H))
            && OnePerRole(Colours, n, c => c.Role, c => ProfileLook.IsStoredColour(c.Border) && ProfileLook.IsStoredColour(c.Text) && ProfileLook.IsStoredColour(c.Inactive))
            && OnePerRole(Second, n, s => s.Role, s => s.Enabled is 0 or 1 && s.Opacity is >= 0 and <= 255
                && Pixel(s.X) && Pixel(s.Y) && Size(s.W) && Size(s.H))
            && Crops != null && ValidCrops(n)
            && OnePerRole(ClientPositions, n, c => c.Role, c => Pixel(c.X) && Pixel(c.Y) && Size(c.W) && Size(c.H) && c.Maximized is 0 or 1)
            && Roles(DontMinimize, n) && Roles(SlotExcluded, n) && Roles(Auto, n)
            && ValidGroups(Slots, n, withColour: false) && ValidGroups(HotkeyGroups, n, withColour: false)
            && ValidGroups(Groups, n, withColour: true) && ValidGroups(ThumbnailGroups, n, withColour: true)
            && OnePerRole(Labels, n, l => l.Role, l => IsText(l.Text, MaxLabel))
            && OnePerRole(LabelStyles, n, l => l.Role, l => ProfileLook.IsStoredColour(l.Colour) && l.Size is >= 0 and <= MaxLabelSize);
    }

    private bool ValidCrops(int n)
    {
        var perRole = new int[n];
        foreach (var c in Crops)
        {
            if (c == null || c.Role < 0 || c.Role >= n || ++perRole[c.Role] > MaxCropsPerCharacter) return false;
            if (c.Id == null || c.Id.Length is 0 or > 32 || !c.Id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return false;
            if (c.Name == null || !IsTextOrEmpty(c.Name, ShareCode.MaxNameLength)) return false;
            if (!Pixel(c.SourceX) || !Pixel(c.SourceY) || !Size(c.SourceW) || !Size(c.SourceH)
                || !Pixel(c.PopupX) || !Pixel(c.PopupY) || !Size(c.PopupW) || !Size(c.PopupH) || c.Opacity is < 0 or > 100) return false;
        }
        return Crops.Select(c => (c.Role, c.Id)).Distinct().Count() == Crops.Count;
    }

    private static bool Pixel(double v) => double.IsFinite(v) && v >= MinPixel && v <= MaxPixel;
    private static bool Size(double v) => double.IsFinite(v) && v >= 0 && v <= MaxPixel;
    private static bool IsText(string? t, int max) => !string.IsNullOrEmpty(t) && PlainText.OneLine(t, max) == t;
    private static bool IsTextOrEmpty(string? t, int max) => t != null && (t.Length == 0 || PlainText.OneLine(t, max) == t);

    /// <summary>A store with one entry per role at most, every role inside the list and every entry inside its range.</summary>
    private static bool OnePerRole<T>(List<T>? list, int roles, Func<T, int> role, Func<T, bool> ok) where T : class
    {
        if (list == null || list.Count > roles) return false;
        var seen = new HashSet<int>();
        foreach (var item in list)
            if (item == null || role(item) < 0 || role(item) >= roles || !seen.Add(role(item)) || !ok(item)) return false;
        return true;
    }

    private static bool Roles(List<int>? list, int roles)
    {
        if (list == null || list.Count > roles) return false;
        var seen = new HashSet<int>();
        return list.All(r => r >= 0 && r < roles && seen.Add(r));
    }

    private static bool ValidGroups(List<GroupCode>? list, int roles, bool withColour)
    {
        if (list == null || list.Count > MaxGroups) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in list)
        {
            if (g == null || g.Colour == null || !IsText(g.Name, ShareCode.MaxNameLength) || !names.Add(g.Name)) return false;
            bool colourOk = withColour ? ProfileLook.IsStoredColour(g.Colour) && g.Colour.Length > 0 : g.Colour.Length == 0;
            if (!colourOk || !Roles(g.Roles, roles)) return false;
        }
        return true;
    }

    // ── Making ──────────────────────────────────────────────────────────

    /// <summary>The code of a profile, or null: more than 64 characters, or more crops on one than a code holds. Every value is
    /// held to the range a code reads (a value out of it is put inside it, an entry that cannot be made valid is left out), so a
    /// code made here always reads back. A character whose name is not a clean name (CleanName) is left out with its data.</summary>
    public static ProfileCode? Of(AppSettings s, Profile p)
    {
        var names = Names(p);
        if (names == null) return null;
        var role = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++) role[names[i]] = i;

        // One entry per role: the first spelling in the store's order wins (a store may hold "Alpha" and "alpha").
        List<TOut> Each<TIn, TOut>(Dictionary<string, TIn> store, Func<int, TIn, TOut?> make) where TOut : class
        {
            var done = new HashSet<int>();
            var result = new List<TOut>();
            foreach (var (key, value) in store.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                if (value != null && role.TryGetValue(key, out var r) && done.Add(r) && make(r, value) is { } item) result.Add(item);
            return result.OrderBy(x => RoleOf(x)).ToList();
        }
        List<int> Listed(IEnumerable<string> members) =>
            members.Where(m => role.ContainsKey(m)).Select(m => role[m]).Distinct().OrderBy(r => r).ToList();
        List<GroupCode> Named(IEnumerable<(string Name, string Colour, IEnumerable<string> Members)> groups, bool withColour, bool onlyWithMembers)
        {
            var result = new List<GroupCode>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, colour, members) in groups)
            {
                if (!IsText(name, ShareCode.MaxNameLength) || !seen.Add(name)) continue;
                var roles = Listed(members);
                if (onlyWithMembers && roles.Count == 0) continue;
                var c = withColour ? (ProfileLook.IsStoredColour(colour) && colour.Length > 0 ? colour : "#4fc3f7") : "";
                result.Add(new GroupCode { Name = name, Colour = c, Roles = roles });
            }
            return result.Take(MaxGroups).ToList();
        }

        var crops = new List<CropCode>();
        foreach (var (key, list) in p.Crops.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (list == null || !role.TryGetValue(key, out var r) || crops.Any(c => c.Role == r)) continue;
            var made = list.Where(c => c != null).Select(c => Crop(r, c)).Where(c => c != null).Cast<CropCode>()
                .GroupBy(c => c.Id).Select(g => g.First()).ToList();
            if (made.Count > MaxCropsPerCharacter) return null;
            crops.AddRange(made);
        }

        var code = new ProfileCode
        {
            Look = ProfileLook.Of(p),
            Characters = names,
            Visibility = Each(p.ThumbnailVisibility, (r, v) => new HiddenCode { Role = r, Hidden = v != 0 ? 1 : 0 }),
            Positions = Each(p.ThumbnailPositions, (r, t) => Finite(t.X, t.Y, t.Width, t.Height)
                ? new RectCode { Role = r, X = Pixel2(t.X), Y = Pixel2(t.Y), W = Size2(t.Width), H = Size2(t.Height) } : null),
            Colours = Each(p.CustomColors, (r, c) => ProfileLook.IsStoredColour(c.Border) && ProfileLook.IsStoredColour(c.Text) && ProfileLook.IsStoredColour(c.InactiveBorder)
                ? new ColoursCode { Role = r, Border = c.Border, Text = c.Text, Inactive = c.InactiveBorder } : null),
            Second = Each(p.SecondaryThumbnails, (r, t) => new SecondCode
            {
                Role = r, Enabled = t.Enabled != 0 ? 1 : 0, Opacity = Math.Clamp(t.Opacity, 0, 255),
                X = Math.Clamp(t.X, -32768, 32767), Y = Math.Clamp(t.Y, -32768, 32767),
                W = Math.Clamp(t.Width, 0, 32767), H = Math.Clamp(t.Height, 0, 32767),
            }),
            Crops = crops.OrderBy(c => c.Role).ToList(),
            ClientPositions = Each(p.ClientPositions, (r, c) => Finite(c.X, c.Y, c.Width, c.Height)
                ? new ClientCode { Role = r, X = Pixel2(c.X), Y = Pixel2(c.Y), W = Size2(c.Width), H = Size2(c.Height), Maximized = c.IsMaximized != 0 ? 1 : 0 } : null),
            DontMinimize = Listed(p.DontMinimizeClients),
            SlotExcluded = Listed(p.ClientSlotExcluded),
            Auto = Listed(AutoProfile.Characters(p.AutoSelectCharacters)),
            Slots = Named(p.ClientSlots.Select(x => (x.Name, "", (IEnumerable<string>)x.Characters)), false, true),
            HotkeyGroups = Named(p.HotkeyGroups.Select(kv => (kv.Key, "", (IEnumerable<string>)kv.Value.Characters)), false, false),
            Groups = Named(p.Groups.Select(kv => (kv.Key, kv.Value.Color, (IEnumerable<string>)kv.Value.Members)), true, false),
            ThumbnailGroups = Named(s.ThumbnailGroups.Select(g => (g.Name, g.Color, (IEnumerable<string>)g.Members)), true, true),
            Labels = Each(s.ThumbnailAnnotations, (r, t) =>
            {
                var text = PlainText.OneLine(t, MaxLabel);
                return text.Length == 0 ? null : new LabelCode { Role = r, Text = text };
            }),
            LabelStyles = Each(s.ThumbnailLabelStyles, (r, t) => ProfileLook.IsStoredColour(t.Color)
                ? new LabelStyleCode { Role = r, Colour = t.Color ?? "", Size = Math.Clamp(t.Size, 0, MaxLabelSize) } : null),
        };
        return code.IsValid() ? code : null;
    }

    private static int RoleOf(object x) => x switch
    {
        HiddenCode a => a.Role, RectCode a => a.Role, ColoursCode a => a.Role, SecondCode a => a.Role,
        ClientCode a => a.Role, LabelCode a => a.Role, LabelStyleCode a => a.Role, _ => 0,
    };

    private static CropCode? Crop(int role, CropDefinition c)
    {
        var id = c.Id ?? "";
        if (id.Length is 0 or > 32 || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return null;
        return new CropCode
        {
            Role = role, Id = id, Name = PlainText.OneLine(c.Name, ShareCode.MaxNameLength),
            SourceX = Math.Clamp(c.SourceX, -32768, 32767), SourceY = Math.Clamp(c.SourceY, -32768, 32767),
            SourceW = Math.Clamp(c.SourceWidth, 0, 32767), SourceH = Math.Clamp(c.SourceHeight, 0, 32767),
            PopupX = Math.Clamp(c.PopupX, -32768, 32767), PopupY = Math.Clamp(c.PopupY, -32768, 32767),
            PopupW = Math.Clamp(c.PopupWidth, 0, 32767), PopupH = Math.Clamp(c.PopupHeight, 0, 32767),
            ShowLabel = c.ShowLabel, Opacity = Math.Clamp(c.Opacity, 0, 100), ClickThrough = c.ClickThrough,
        };
    }

    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static double Pixel2(double v) => Math.Round(Math.Clamp(v, MinPixel, MaxPixel), 2);
    private static double Size2(double v) => Math.Round(Math.Clamp(v, 0, MaxPixel), 2);

    /// <summary>The characters a profile names in the stores a code carries: clean names, each once (ignoring case), in name
    /// order. Null when there are more than <see cref="MaxCharacters"/>.</summary>
    private static List<string>? Names(Profile p)
    {
        var all = p.ThumbnailVisibility.Keys
            .Concat(p.ThumbnailPositions.Keys).Concat(p.CustomColors.Keys).Concat(p.SecondaryThumbnails.Keys).Concat(p.Crops.Keys)
            .Concat(p.ClientPositions.Keys).Concat(p.DontMinimizeClients).Concat(p.ClientSlotExcluded)
            .Concat(AutoProfile.Characters(p.AutoSelectCharacters))
            .Concat(p.ClientSlots.SelectMany(x => x.Characters)).Concat(p.HotkeyGroups.Values.SelectMany(x => x.Characters))
            .Concat(p.Groups.Values.SelectMany(x => x.Members));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = all.Where(n => n != null && ShareCode.CleanName(n) == n && seen.Add(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count > MaxCharacters ? null : names;
    }

    // ── Applying ────────────────────────────────────────────────────────

    /// <summary>The role of each code character that the local list holds under the same name (ignoring case), with the local
    /// spelling: what the mapping starts as. A role without one is left out (skipped).</summary>
    public Dictionary<int, string> DefaultMapping(IEnumerable<string> local)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in local) if (!string.IsNullOrWhiteSpace(name)) byName.TryAdd(name, name);
        var map = new Dictionary<int, string>();
        for (int i = 0; i < Characters.Count; i++)
            if (byName.TryGetValue(Characters[i], out var mine)) map[i] = mine;
        return map;
    }

    /// <summary>Puts the code on <paramref name="made"/> (a profile that is a copy of the current one, already made): the look,
    /// and, for each mapped role (<paramref name="mapping"/>: role index to a local character; a role left out is skipped), the
    /// data of the carried per-character stores under the local name. For a mapped character the code's data REPLACES what the
    /// copy held in the carried per-profile stores, and nothing else of the copy changes (its hotkeys, CPU masks and audio
    /// volumes stay; no other character is touched; no other profile is touched). Two roles mapped to one local character: the
    /// first role wins. The settings-wide stores (labels, label styles, thumbnail groups) only ever GAIN what the character did
    /// not have, since they are shared by every profile. Returns the number of roles applied.</summary>
    public int ApplyTo(AppSettings s, Profile made, IReadOnlyDictionary<int, string> mapping)
    {
        Look.ApplyTo(made);
        var target = new string?[Characters.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, name) in mapping.OrderBy(kv => kv.Key))
            if (role >= 0 && role < target.Length && !string.IsNullOrWhiteSpace(name) && taken.Add(name.Trim())) target[role] = name.Trim();
        var mapped = target.Where(t => t != null).Cast<string>().ToList();
        bool Mine(string other) => mapped.Any(m => string.Equals(m, other, StringComparison.OrdinalIgnoreCase));
        void Drop<T>(Dictionary<string, T> d) { foreach (var key in d.Keys.Where(Mine).ToList()) d.Remove(key); }
        string? Target(int role) => role >= 0 && role < target.Length ? target[role] : null;

        // The copy's own data for the mapped characters goes; the code's comes.
        Drop(made.ThumbnailVisibility); Drop(made.ThumbnailPositions); Drop(made.CustomColors); Drop(made.SecondaryThumbnails);
        Drop(made.Crops); Drop(made.ClientPositions);
        made.DontMinimizeClients.RemoveAll(Mine);
        made.ClientSlotExcluded.RemoveAll(Mine);
        foreach (var g in made.HotkeyGroups.Values) g.Characters.RemoveAll(Mine);
        foreach (var g in made.Groups.Values) g.Members.RemoveAll(Mine);

        foreach (var v in Visibility) if (Target(v.Role) is { } t) made.ThumbnailVisibility[t] = v.Hidden;
        foreach (var r in Positions) if (Target(r.Role) is { } t) made.ThumbnailPositions[t] = new ThumbnailRect { X = r.X, Y = r.Y, Width = r.W, Height = r.H };
        foreach (var c in Colours) if (Target(c.Role) is { } t) made.CustomColors[t] = new CustomColorEntry { Border = c.Border, Text = c.Text, InactiveBorder = c.Inactive };
        foreach (var c in Second) if (Target(c.Role) is { } t)
            made.SecondaryThumbnails[t] = new SecondaryThumbnailSettings { Enabled = c.Enabled, Opacity = c.Opacity, X = c.X, Y = c.Y, Width = c.W, Height = c.H };
        foreach (var c in Crops) if (Target(c.Role) is { } t)
        {
            if (!made.Crops.TryGetValue(t, out var list)) made.Crops[t] = list = new List<CropDefinition>();
            list.Add(new CropDefinition
            {
                Id = c.Id, Name = c.Name, SourceX = c.SourceX, SourceY = c.SourceY, SourceWidth = c.SourceW, SourceHeight = c.SourceH,
                PopupX = c.PopupX, PopupY = c.PopupY, PopupWidth = c.PopupW, PopupHeight = c.PopupH,
                ShowLabel = c.ShowLabel, Opacity = c.Opacity, ClickThrough = c.ClickThrough,
            });
        }
        foreach (var c in ClientPositions) if (Target(c.Role) is { } t)
            made.ClientPositions[t] = new ClientPosition { X = c.X, Y = c.Y, Width = c.W, Height = c.H, IsMaximized = c.Maximized };
        foreach (var r in DontMinimize) if (Target(r) is { } t) made.DontMinimizeClients.Add(t);
        foreach (var r in SlotExcluded) if (Target(r) is { } t) made.ClientSlotExcluded.Add(t);
        made.AutoSelectCharacters = AutoProfile.Join(Auto.Select(Target).Where(t => t != null).Cast<string>());

        // A client slot is the player's own geometry on this machine's monitors: never made from a code. A character joins the
        // local slot of the same name (and leaves its other slots: a character is in at most one).
        foreach (var slot in Slots)
        {
            var local = made.ClientSlots.FirstOrDefault(x => string.Equals(x.Name, slot.Name, StringComparison.OrdinalIgnoreCase));
            if (local == null) continue;
            foreach (var r in slot.Roles)
                if (Target(r) is { } t)
                {
                    foreach (var other in made.ClientSlots) other.Characters.RemoveAll(c => string.Equals(c, t, StringComparison.OrdinalIgnoreCase));
                    local.Characters.Add(t);
                }
        }
        // A cycle group made from a code has no keys: the code never binds one.
        foreach (var group in HotkeyGroups)
        {
            var roles = group.Roles.Select(Target).Where(t => t != null).Cast<string>().ToList();
            if (roles.Count == 0) continue;
            var key = made.HotkeyGroups.Keys.FirstOrDefault(k => string.Equals(k, group.Name, StringComparison.OrdinalIgnoreCase)) ?? group.Name;
            if (!made.HotkeyGroups.TryGetValue(key, out var hg)) made.HotkeyGroups[key] = hg = new HotkeyGroup();
            hg.Characters.AddRange(roles);
        }
        foreach (var group in Groups)
        {
            var members = group.Roles.Select(Target).Where(t => t != null).Cast<string>().ToList();
            if (members.Count == 0) continue;                                    // no mapped character is in it: no group is made
            var key = made.Groups.Keys.FirstOrDefault(k => string.Equals(k, group.Name, StringComparison.OrdinalIgnoreCase)) ?? group.Name;
            if (!made.Groups.TryGetValue(key, out var g)) made.Groups[key] = g = new ThumbnailGroup { Name = group.Name, Color = group.Colour };
            g.Members.AddRange(members);
        }

        // Shared by every profile: only what the character does not have yet.
        bool Has<T>(Dictionary<string, T> d, string name) => d.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        foreach (var l in Labels) if (Target(l.Role) is { } t && !Has(s.ThumbnailAnnotations, t)) s.ThumbnailAnnotations[t] = l.Text;
        foreach (var l in LabelStyles) if (Target(l.Role) is { } t && !Has(s.ThumbnailLabelStyles, t))
            s.ThumbnailLabelStyles[t] = new ThumbnailLabelStyle { Color = l.Colour, Size = l.Size };
        foreach (var group in ThumbnailGroups)
        {
            var add = group.Roles.Select(Target).Where(t => t != null).Cast<string>()
                .Where(t => !s.ThumbnailGroups.Any(g => g.Members.Any(m => string.Equals(m, t, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (add.Count == 0) continue;
            var shared = s.ThumbnailGroups.FirstOrDefault(x => string.Equals(x.Name, group.Name, StringComparison.OrdinalIgnoreCase));
            if (shared == null) s.ThumbnailGroups.Add(shared = new ThumbnailGroup { Name = group.Name, Color = group.Colour });
            shared.Members.AddRange(add);
        }
        return mapped.Count;
    }
}
