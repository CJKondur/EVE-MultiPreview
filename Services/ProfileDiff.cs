using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EveMultiPreview.Models;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Services;

/// <summary>One setting that differs between two profiles. <see cref="Property"/> is the name <see cref="ProfileDiff.CopyOne"/>
/// takes; <see cref="LabelKey"/> is the "L.S.…" key of the card that shows it (null: none does, the name is shown).
/// The texts are for the player to read: a count for a list or table, the JSON text of anything else, cut at 80.</summary>
public sealed record ProfileDiffRow(string Property, string? LabelKey, string TextMine, string TextOther);

/// <summary>IDEAS #50: what differs between the active profile and another one, and taking one value across. Pure: it
/// reads and writes the Profile objects it is handed and nothing else.</summary>
public static class ProfileDiff
{
    /// <summary>The one row for both per-character position tables, which differ in every pair of profiles.</summary>
    public const string Positions = "Positions";
    public const string PositionsLabelKey = "L.S.Profile.ComparePositions";
    public const string EntriesKey = "L.S.Profile.CompareEntries";
    private const int MaxText = 80;

    private static readonly string[] PositionProperties = { nameof(Profile.ThumbnailPositions), nameof(Profile.ClientPositions) };

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The properties a row can be about: the ones ProfileCopy.CopyFull copies (read/write), except what it keeps
    /// (AutoSelectCharacters) and the position tables, which only the Positions row covers.</summary>
    private static bool IsCompared(PropertyInfo p) =>
        p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
        && p.Name != nameof(Profile.AutoSelectCharacters) && !PositionProperties.Contains(p.Name);

    /// <summary>The rows, by the settings page the card of each is on (the order of the window), the ones without a card
    /// last, and by name within a page. Equal means equal JSON text of the value, with every dictionary read as its
    /// key/value pairs sorted by key (see <see cref="Canonical"/>): the order two profiles happened to be filled in never
    /// makes a row; a list keeps its order, which is its meaning.</summary>
    public static IReadOnlyList<ProfileDiffRow> Compare(Profile mine, Profile other)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(other);
        var cards = Cards.Value;
        var found = new List<(int Page, ProfileDiffRow Row)>();
        foreach (var p in typeof(Profile).GetProperties().Where(IsCompared))
        {
            object? a = p.GetValue(mine), b = p.GetValue(other);
            if (Canonical(a, p.PropertyType) == Canonical(b, p.PropertyType)) continue;
            cards.TryGetValue(p.Name, out var card);
            found.Add((card.LabelKey == null ? int.MaxValue : card.Page,
                new ProfileDiffRow(p.Name, card.LabelKey, Show(a, p.PropertyType), Show(b, p.PropertyType))));
        }
        var tables = PositionProperties.Select(n => typeof(Profile).GetProperty(n)!).ToList();
        if (tables.Any(p => Canonical(p.GetValue(mine), p.PropertyType) != Canonical(p.GetValue(other), p.PropertyType)))
            found.Add((int.MaxValue, new ProfileDiffRow(Positions, PositionsLabelKey, Entries(tables.Sum(p => Count(p.GetValue(mine)))), Entries(tables.Sum(p => Count(p.GetValue(other)))))));
        return found.OrderBy(f => f.Page).ThenBy(f => f.Row.Property, StringComparer.Ordinal).Select(f => f.Row).ToList();
    }

    /// <summary>Takes one value from <paramref name="source"/> into <paramref name="dest"/> as a JSON clone, the way
    /// ProfileCopy.CopyFull clones a whole profile: the two profiles never share an object. <paramref name="property"/> is
    /// a row's Property; "Positions" copies both position tables. With the settings both profiles belong to, the alert
    /// choices of the groups that go with the copy are forgotten the way a full copy forgets them (GroupAlertChoices.Follow).
    /// An unknown name (or one no row has) throws ArgumentException and changes nothing.</summary>
    public static void CopyOne(Profile source, Profile dest, string property, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        var targets = property == Positions
            ? PositionProperties.Select(n => typeof(Profile).GetProperty(n)!).ToList()
            : new List<PropertyInfo> { Find(property) };
        var before = settings == null ? null : GroupAlertChoices.GroupNames(settings);
        foreach (var p in targets)
        {
            var json = JsonSerializer.Serialize(p.GetValue(source), p.PropertyType);
            p.SetValue(dest, JsonSerializer.Deserialize(json, p.PropertyType));
        }
        if (settings != null) GroupAlertChoices.Follow(settings, before!);
    }

    private static PropertyInfo Find(string property)
    {
        var p = string.IsNullOrEmpty(property) ? null : typeof(Profile).GetProperty(property);
        if (p == null || !IsCompared(p)) throw new ArgumentException("Not a comparable profile setting: " + property, nameof(property));
        return p;
    }

    // ── texts ────────────────────────────────────────────────────────────────

    private static string Show(object? value, Type type)
    {
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type)) return Entries(Count(value));
        var text = JsonSerializer.SerializeToNode(value, type)?.ToJsonString(Readable) ?? "null";
        if (text.Length <= MaxText) return text;
        int cut = MaxText - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    private static int Count(object? value) => value is ICollection c ? c.Count : 0;

    private static string Entries(int count) => string.Format(LocalizationService.Str(EntriesKey, "{0} entries"), count);

    // ── equality ─────────────────────────────────────────────────────────────

    private static string Canonical(object? value, Type type) =>
        Sorted(JsonSerializer.SerializeToNode(value, type))?.ToJsonString(Readable) ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Sorted(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Sorted).ToArray()),
        _ => node?.DeepClone(),
    };

    // ── titles ───────────────────────────────────────────────────────────────

    /// <summary>For each property name, the card that shows it and the page of that card: the item whose FIRST bind is
    /// "AppSettings.X" or "Profile.X" (the AppSettings proxies share Profile's names). The first item in page order wins.</summary>
    private static readonly Lazy<Dictionary<string, (string? LabelKey, int Page)>> Cards = new(() =>
    {
        var pages = SettingsRegistry.AllPages.ToList();
        var map = new Dictionary<string, (string? LabelKey, int Page)>(StringComparer.Ordinal);
        foreach (var item in SettingsRegistry.Items)
        {
            var (first, label) = item switch
            {
                SettingItem s => (s.Setting.Binds.FirstOrDefault(), s.Setting.LabelKey),
                EditorItem e => (e.Binds.FirstOrDefault(), e.LabelKey),
                _ => (null, ""),
            };
            int dot = first?.IndexOf('.') ?? -1;
            if (dot < 0) continue;
            var name = first![(dot + 1)..];
            if (map.ContainsKey(name)) continue;
            var page = SettingsRegistry.PageOf(item.Id);
            map[name] = (label, page == null ? int.MaxValue : pages.IndexOf(page));
        }
        return map;
    });
}
