using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace EveMultiPreview.SettingsUi;

/// <summary>One look of the app (spec §5.1): its id, whether its standard controls are WPF's Fluent theme, and how its
/// dictionary is made.</summary>
public sealed class UiStyle
{
    private readonly Func<ResourceDictionary> _load;

    // One per thread: each test runs on its own STA thread and the app has one window thread. A dictionary holding
    // styles and templates belongs to the thread that made it (plan S1, ruling 17).
    [ThreadStatic] private static Dictionary<string, ResourceDictionary>? _loaded;

    public UiStyle(string id, bool usesFluent, Func<ResourceDictionary> load)
    {
        Id = id;
        UsesFluent = usesFluent;
        _load = load;
    }

    public string Id { get; }

    /// <summary>The window's standard controls are drawn by WPF's Fluent theme, merged by hand (spec §14).</summary>
    public bool UsesFluent { get; }

    /// <summary>The style's dictionary: made the first time this thread asks, then shared by every window of the thread
    /// (spec §5.2, rule 5). Every brush in it is frozen.</summary>
    public ResourceDictionary Dictionary
    {
        get
        {
            var loaded = _loaded ??= new Dictionary<string, ResourceDictionary>(StringComparer.Ordinal);
            if (!loaded.TryGetValue(Id, out var dictionary))
            {
                dictionary = _load();
                UiStyles.Freeze(dictionary);
                loaded[Id] = dictionary;
            }
            return dictionary;
        }
    }
}

/// <summary>The registry of the app's looks, and (from Task 5 on) the applier.</summary>
public static class UiStyles
{
    public static UiStyle Windows11 { get; } = new("windows11", usesFluent: true, () => new Windows11Style());

    /// <summary>Every look, in the order the picker shows them (S2 adds the others).</summary>
    public static IReadOnlyList<UiStyle> All { get; } = new[] { Windows11 };

    /// <summary>The look with this id; a missing or unknown id gives Windows 11, the only look of S1.</summary>
    public static UiStyle Find(string? id) => All.FirstOrDefault(s => s.Id == id) ?? Windows11;

    /// <summary>WPF's Fluent dictionary, dark (spec §14).</summary>
    public const string FluentSource = "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml";

    /// <summary>Makes a style dictionary whole (spec §5.2, rule 2): a frozen brush of each colour of the contract it holds,
    /// under the colour's brush key, then every freezable frozen.</summary>
    public static void Prepare(ResourceDictionary dictionary)
    {
        foreach (var colour in StyleContract.Colours)
        {
            if (!dictionary.Contains(colour) || dictionary[colour] is not System.Windows.Media.Color c) continue;
            var brush = new System.Windows.Media.SolidColorBrush(c);
            brush.Freeze();
            dictionary[StyleContract.BrushKey(colour)] = brush;
        }
        Freeze(dictionary);
    }

    /// <summary>Freezes every brush (and every other freezable) of a dictionary, and of its merged dictionaries. A frozen
    /// brush may be shared across threads, and a dynamic reference to it does not listen to it for changes.</summary>
    public static void Freeze(ResourceDictionary dictionary)
    {
        foreach (var key in dictionary.Keys.Cast<object>().ToList())
            if (dictionary[key] is Freezable { IsFrozen: false, CanFreeze: true } freezable) freezable.Freeze();
        foreach (var merged in dictionary.MergedDictionaries) Freeze(merged);
    }
}
