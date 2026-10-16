using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace EveMultiPreview.SettingsUi;

/// <summary>One look of the app (spec §5.1): its id, whether its standard controls are WPF's Fluent theme, how its
/// dictionary is made, and (for a Fluent look) what goes into slot 0 of its windows.</summary>
public sealed class UiStyle
{
    private readonly Func<ResourceDictionary> _load;
    private readonly Func<ResourceDictionary>? _controls;
    private readonly Func<ResourceDictionary>? _keyed;

    // One per thread: each test runs on its own STA thread and the app has one window thread. A dictionary holding
    // styles and templates belongs to the thread that made it (plan S1, ruling 17).
    [ThreadStatic] private static Dictionary<string, ResourceDictionary>? _loaded;

    public UiStyle(string id, bool usesFluent, Func<ResourceDictionary> load, Func<ResourceDictionary>? controls = null,
        Func<ResourceDictionary>? keyed = null)
    {
        if (usesFluent && controls == null) throw new ArgumentException("A look with Fluent needs the Fluent dictionary (controls)", nameof(controls));
        Id = id;
        UsesFluent = usesFluent;
        _load = load;
        _controls = controls;
        _keyed = keyed;
    }

    public string Id { get; }

    /// <summary>The window's standard controls are drawn by WPF's Fluent theme, merged by hand (spec §14).</summary>
    public bool UsesFluent { get; }

    /// <summary>The style's dictionary: made the first time this thread asks, then shared by every window of the thread
    /// (spec §5.2, rule 5). Every brush in it is frozen.</summary>
    public ResourceDictionary Dictionary => Loaded(Id, _load, freeze: true);

    /// <summary>Slot 0 of a window of a Fluent look: Fluent's own dictionary, or a dictionary that merges it and adds the
    /// look's keyed controls (plan S1, ruling 5). Null for a look without Fluent, whose windows take the kit. Not frozen:
    /// Fluent's dictionary is WPF's own, used as WPF uses it.</summary>
    public ResourceDictionary? Controls => _controls == null ? null : Loaded(Id + "#controls", _controls, freeze: false);

    /// <summary>The look's keyed controls that are Fluent's with other metrics, merged after slot 0 of a window. Kept out of
    /// slot 0 so that slot 0 stays a top-level dictionary whose Source is Fluent's: WPF finds Fluent only that way, and
    /// only then gives the window its dark caption and backdrop. Null when the look has none.</summary>
    public ResourceDictionary? Keyed => _keyed == null ? null : Loaded(Id + "#keyed", _keyed, freeze: false);

    private static ResourceDictionary Loaded(string key, Func<ResourceDictionary> make, bool freeze)
    {
        var loaded = _loaded ??= new Dictionary<string, ResourceDictionary>(StringComparer.Ordinal);
        if (!loaded.TryGetValue(key, out var dictionary))
        {
            dictionary = make();
            if (freeze) UiStyles.Freeze(dictionary);
            loaded[key] = dictionary;
        }
        return dictionary;
    }
}

/// <summary>The registry of the app's looks, and the applier (spec §5.3).</summary>
public static class UiStyles
{
    /// <summary>WPF's Fluent dictionary, dark (spec §14).</summary>
    public const string FluentSource = "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml";

    private static ResourceDictionary FluentDark() => new() { Source = new Uri(FluentSource, UriKind.Absolute) };

    public static UiStyle Windows11 { get; } = new("windows11", usesFluent: true, () => new Windows11Style(), FluentDark,
        () => new Windows11Controls(Windows11!.Controls!));

    /// <summary>The old window's navy look (spec §7.1). Its windows take the kit in slot 0, never Fluent.</summary>
    public static UiStyle Classic { get; } = new("classic", usesFluent: false, () => new ClassicStyle());

    /// <summary>Every look, in the order the picker shows them, Classic first (spec §9.2). S3 and S4 add the others.</summary>
    public static IReadOnlyList<UiStyle> All { get; } = new[] { Classic, Windows11 };

    /// <summary>The look with this id; a missing or unknown id gives Windows 11, the only look of S1.</summary>
    public static UiStyle Find(string? id) => All.FirstOrDefault(s => s.Id == id) ?? Windows11;

    /// <summary>The look the app shows. Plan S1 never changes it outside tests (ruling 1): nothing reads or writes a
    /// setting for it until S2.</summary>
    public static UiStyle Current { get; private set; } = Windows11;

    /// <summary>The look shown (spec §5.3): the current one. S4 makes it High contrast while a Windows contrast theme is
    /// on and the player wants that.</summary>
    public static UiStyle Effective => Current;

    /// <summary>Raised after a change of look, on the thread that made it. A handler that owns something of another
    /// thread leaves it alone (the menus, the hub and the settings window check).</summary>
    public static event Action? Changed;

    /// <summary>Raised before a change of look is applied, on the thread that makes it: a window keeps what a new template
    /// would lose (the settings window: the page's scroll and the keyboard's place) and gives it back on Changed.</summary>
    public static event Action? Changing;

    public static void Set(string? id) => Set(Find(id));

    /// <summary>Makes <paramref name="style"/> the look of the app at once (spec §5.3): the palette of the surfaces drawn in
    /// code, slot 0 and the style dictionary of every window this thread attached (the base is the same for every look),
    /// the title bars; then Changed. Nothing closes, nothing is rebuilt. A window that fails to take the look does not stop
    /// the others, nor Changed; the first failure is thrown at the end.</summary>
    public static void Set(UiStyle style)
    {
        if (style == Current) return;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        try
        {
            // Inside the try: a Changing handler (or the palette) that throws still gets Changed raised, so a window that
            // kept its place gives it back (Task 7 review, Minor 5).
            Changing?.Invoke();
            Current = style;
            UiPalette.Use(UiPalette.From(style.Dictionary));
            foreach (var window in AttachedWindows())
            {
                try { Restyle(window); }
                catch (Exception e) { failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            }
        }
        finally { Changed?.Invoke(); }
        failure?.Throw();
    }

    /// <summary>How many live windows this thread has attached (tests).</summary>
    public static int AttachedHere => AttachedWindows().Count();

    private static IEnumerable<Window> AttachedWindows()
    {
        if (_attached == null) return Array.Empty<Window>();
        _attached.RemoveAll(r => !r.TryGetTarget(out _));
        return _attached.Select(r => r.TryGetTarget(out var w) ? w : null).OfType<Window>().ToList();
    }

    /// <summary>Puts the current look's slots where the old ones are. Only a slot that changes is touched: a switch between
    /// two looks that share slot 0 swaps the style dictionary alone, so the page's lists keep their cards. A Fluent window
    /// has four slots (the keyed buttons after the style), a window of a look without Fluent three: the fourth comes and
    /// goes with Fluent.</summary>
    private static void Restyle(Window window)
    {
        var old = (ResourceDictionary[])window.GetValue(SlotsProperty);
        var kind = (StyledAs)window.GetValue(KindProperty);
        var slots = SlotsFor(Current, kind);
        var merged = window.Resources.MergedDictionaries;
        for (int i = 0; i < Math.Max(old.Length, slots.Length); i++)
        {
            var was = i < old.Length ? old[i] : null;
            var now = i < slots.Length ? slots[i] : null;
            if (ReferenceEquals(was, now)) continue;
            int at = was == null ? -1 : IndexOfSlot(merged, was);
            if (now == null) { if (at >= 0) merged.RemoveAt(at); }
            else if (at >= 0) merged[at] = now;
            else merged.Insert(Math.Min(i, merged.Count), now);
        }
        window.SetValue(SlotsProperty, slots);
        TextColour(window);
        if (kind == StyledAs.Window) TitleBar.Apply(window);
    }

    /// <summary>The text colour of a window or a surface (toast, hub, picker, wheel) of a look without Fluent: the look's
    /// (Fluent's window style gives a Windows 11 window its own, and a look without Fluent has nothing to give it, so text
    /// without a style of its own would be black). A window of a Fluent look keeps Fluent's: the local value goes. A
    /// surface of a Fluent look never had one: only the look's own reference is taken away, not a colour the surface set.</summary>
    private static void TextColour(Window window)
    {
        if (Current.UsesFluent)
        {
            if (DependencyPropertyHelper.GetValueSource(window, System.Windows.Controls.Control.ForegroundProperty).IsExpression)
                window.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
        else window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Fx.TextBrush");
    }

    /// <summary>Where a slot is merged: the dictionary itself, or for slot 0 of a Fluent look the dictionary with Fluent's
    /// Source. WPF replaces the applier's Fluent dictionary with a copy of its own in a shown window (measured, Task 8), so
    /// the applier's is no longer there to be found by reference.</summary>
    private static int IndexOfSlot(IList<ResourceDictionary> merged, ResourceDictionary slot)
    {
        int at = merged.IndexOf(slot);
        if (at >= 0 || slot.Source == null) return at;
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].Source?.OriginalString == slot.Source.OriginalString) return i;
        return -1;
    }

    [ThreadStatic] private static ResourceDictionary? _base, _kit, _empty;

    /// <summary>The base, one per thread, shared by every window of the thread.</summary>
    public static ResourceDictionary Base => _base ??= new BaseTheme();

    /// <summary>The kit, one per thread: slot 0 of a window of a look without Fluent.</summary>
    public static ResourceDictionary Kit => _kit ??= new KitTheme();

    /// <summary>Slot 0 of a surface of a Fluent look: nothing (a surface never takes Fluent, ruling 7).</summary>
    private static ResourceDictionary Empty => _empty ??= new ResourceDictionary();

    [ThreadStatic] private static ResourceDictionary? _blank;

    /// <summary>An empty implicit style for every type Fluent's dictionary styles and the kit does not (a TextBlock, a
    /// Label, a Window …): merged first into a scope of a look without Fluent, it keeps the window's Fluent from reaching
    /// in (spec §5.3; measured in the prototype: without it, it leaks). One per thread.</summary>
    private static ResourceDictionary Blank
    {
        get
        {
            if (_blank != null) return _blank;
            var blank = new ResourceDictionary();
            foreach (var type in Windows11.Controls!.Keys.OfType<Type>())
                if (!Kit.Contains(type)) blank[type] = new Style(type);
            return _blank = blank;
        }
    }

    /// <summary>Gives one subtree the look of <paramref name="style"/>, whatever its window shows (spec §5.3: a gallery
    /// card's miniature): a look with Fluent takes Fluent's dictionary, a look without it the blank styles and the kit;
    /// then the base, the look's own dictionary, and a Fluent look's keyed buttons: the base again, so a template the
    /// window's look overrides (Classic's cards, its navigation entry) is the base's again here, unless this look overrides
    /// it too. The subtree's text takes the look's colour and font.</summary>
    public static void Scope(FrameworkElement element, UiStyle style)
    {
        var merged = element.Resources.MergedDictionaries;
        if (style.UsesFluent) merged.Add(style.Controls!);
        else { merged.Add(Blank); merged.Add(Kit); }
        merged.Add(Base);
        merged.Add(style.Dictionary);
        if (style.UsesFluent && style.Keyed is { } keyed) merged.Add(keyed);
        element.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Fx.TextBrush");
        UiFont.Apply(element, Services.LocalizationService.CurrentLanguage);
    }

    private static readonly DependencyProperty SlotsProperty = DependencyProperty.RegisterAttached(
        "Slots", typeof(ResourceDictionary[]), typeof(UiStyles), new PropertyMetadata(null));

    private static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(StyledAs), typeof(UiStyles), new PropertyMetadata(StyledAs.Window));

    [ThreadStatic] private static List<WeakReference<Window>>? _attached;

    /// <summary>Gives a window the look of the current style (spec §5.3): slot 0 (the look's Fluent dictionary, the kit,
    /// or nothing for a surface of a Fluent look), the base and the style, as its first three merged dictionaries. Call it
    /// in the window's constructor BEFORE InitializeComponent: the window's XAML holds static references (a converter, a
    /// BasedOn) that must find the base while it is read (ruling 3). Calling it again does nothing.</summary>
    public static void Attach(Window window, StyledAs kind = StyledAs.Window)
    {
        if (IsAttached(window)) return;
        var slots = SlotsFor(Current, kind);
        var merged = window.Resources.MergedDictionaries;
        for (int i = 0; i < slots.Length; i++) merged.Insert(i, slots[i]);
        window.SetValue(SlotsProperty, slots);
        window.SetValue(KindProperty, kind);
        if (!Current.UsesFluent) TextColour(window);   // a window or surface of a Fluent look is left exactly as before
        var remembered = new WeakReference<Window>(window);
        _attached ??= new List<WeakReference<Window>>();
        _attached.RemoveAll(r => !r.TryGetTarget(out _));   // windows the collector took (one is added per toast)
        _attached.Add(remembered);
        window.Closed += (_, _) => _attached?.Remove(remembered);   // a closed window is never restyled
        if (kind == StyledAs.Window) window.SourceInitialized += (_, _) => TitleBar.Apply(window);   // a surface has no title bar
    }

    public static bool IsAttached(Window window) => window.GetValue(SlotsProperty) != null;

    /// <summary>The three or four dictionaries the applier merged into the window, or none.</summary>
    public static IReadOnlyList<ResourceDictionary> SlotsOf(Window window) =>
        (ResourceDictionary[]?)window.GetValue(SlotsProperty) ?? Array.Empty<ResourceDictionary>();

    private static ResourceDictionary[] SlotsFor(UiStyle style, StyledAs kind)
    {
        var fluentWindow = style.UsesFluent && kind == StyledAs.Window;
        var slots = new[]
        {
            style.UsesFluent ? (fluentWindow ? style.Controls ?? Empty : Empty) : Kit,
            Base,
            style.Dictionary,
        };
        return fluentWindow && style.Keyed is { } keyed ? slots.Append(keyed).ToArray() : slots;
    }

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
