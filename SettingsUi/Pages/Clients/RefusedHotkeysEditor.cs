using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;

namespace EveMultiPreview.SettingsUi;

/// <summary>One refused hotkey as the card shows it: the key ("Ctrl + F5"), why, and what it is bound to.</summary>
public sealed record RefusedRow(string KeyText, string Reason, IReadOnlyList<HotkeyUse> Uses);

/// <summary>Clients › Hotkeys &amp; switching › Keys Windows refused (IDEAS #42): each hotkey Windows would not
/// register, with what it is bound to (a click opens that card), and the programs that are likely to have it (for a
/// key bound with the Win key: Windows itself first). While a key is refused, "Check again" asks Windows for the hotkeys
/// once more. It shows the app's state, not a setting: nothing here is saved. Looked at again every two seconds
/// while shown.</summary>
public sealed class RefusedHotkeysEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = EditorIds.RefusedHotkeysEditor;
    private const string AgainKey = "L.S.Refused.Again";

    /// <summary>A click on "Check again" within this many milliseconds of the click before it is ignored: a held
    /// Enter clicks at the keyboard's repeat rate, and every check releases and registers every hotkey.</summary>
    public const int AgainEveryMs = 500;

    private readonly SettingsContext _ctx;
    private readonly RefusedHotkeyList? _list;
    private readonly Func<IReadOnlyList<string>>? _runningProcesses;
    private readonly Func<long> _milliseconds;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _owners = EditorCard.Value("", secondary: true);
    private readonly Button _again;
    private string _signature = "";
    private int _look;
    private long? _clicked;     // when "Check again" was last clicked, by _milliseconds

    /// <param name="runningProcesses">The names of the running processes. Tests hand one in, and it is asked at once.
    /// Without it the system's own list is read, on a background thread.</param>
    /// <param name="milliseconds">A clock that only goes forward, in milliseconds, for <see cref="AgainEveryMs"/>.
    /// Tests hand one in. Without it: the time since Windows started.</param>
    public RefusedHotkeysEditor(SettingsContext ctx, Func<IReadOnlyList<string>>? runningProcesses = null, Func<long>? milliseconds = null)
    {
        _ctx = ctx;
        _list = ctx.Services.Hotkeys?.Refused;      // null in a window opened without the app: nothing was asked
        _runningProcesses = runningProcesses;
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
        _summary.SetResourceReference(StyleProperty, "Fx.Label");
        // The summary line can take the keyboard, but only when "Check again" goes away under it (Refresh): it is
        // no stop for the Tab key.
        _summary.Focusable = true;
        System.Windows.Input.KeyboardNavigation.SetIsTabStop(_summary, false);
        _owners.Margin = new Thickness(0, 8, 0, 0);
        _owners.Visibility = Visibility.Collapsed;
        _again = EditorCard.Button(AgainKey, CheckAgain);
        _again.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _again.Margin = new Thickness(0, 12, 0, 0);
        _again.Visibility = Visibility.Collapsed;
        var below = new StackPanel();
        below.Children.Add(_summary);
        below.Children.Add(_rows);
        below.Children.Add(_owners);
        below.Children.Add(_again);
        Content = EditorCard.Build(ItemId, null, below);
        LiveRefresh.Attach(this, Look, seconds: 2);
        Refresh();
    }

    /// <summary>What the card does every two seconds while it is shown: it is built again only when what it shows is
    /// not what it was (what Windows refused, whether that was asked at all, or what a key is bound to).</summary>
    public void Look()
    {
        if (Signature() != _signature) Refresh();
    }

    /// <summary>The refused hotkeys as shown, the pause key first.</summary>
    public IReadOnlyList<RefusedRow> Rows { get; private set; } = Array.Empty<RefusedRow>();

    /// <summary>The line above the rows.</summary>
    public string Summary => _summary.Text;

    /// <summary>The known programs found running (empty: none; the usual ones are named then, where a key without
    /// the Win key is taken). Null while no key is refused because it is taken: then nobody is asked.</summary>
    public IReadOnlyList<string>? Running { get; private set; }

    /// <summary>What is shown about the likely owners, or "" while it is hidden: Windows itself for a key bound with
    /// the Win key, then the known programs that are running (or, for a key without Win, the usual ones while none of
    /// them runs); each on its own line where both are said.</summary>
    public string Owners => _owners.Visibility == Visibility.Visible ? _owners.Text : "";

    /// <summary>The uses that are a refused key: the same key with the same modifiers, in whatever order and case the
    /// two are written ("^!F5" and "!^F5" are one key). A bare key is not that key with a modifier held: Windows
    /// refused the one combination. The card's rows and the tray's note both ask here.</summary>
    public static IReadOnlyList<HotkeyUse> UsesOf(string hotkey, IEnumerable<HotkeyUse> uses) =>
        uses.Where(u => HotkeyText.Clashes(u.Hotkey, hotkey, wildcard: false)).ToList();

    private static string Str(string key) => SettingsContext.Str(key);

    private IReadOnlyList<RefusedHotkey> Refused() => _list?.Current ?? Array.Empty<RefusedHotkey>();

    private IReadOnlyList<HotkeyUse> Uses() => HotkeyUses.All(_ctx.S, SettingsRegistry.Settings).ToList();

    private string Signature() =>
        (_list?.Asked == true ? "asked|" : "not|") + string.Join("|", Refused().Select(r => r.Hotkey + ":" + r.Error))
        + "\n" + string.Join("\n", Uses().Select(u => u.Id + "=" + u.Hotkey));

    public void Refresh()
    {
        _signature = Signature();
        var refused = Refused();
        var uses = Uses();
        _summary.Text = refused.Count > 0 ? string.Format(Str("L.S.Refused.Count"), refused.Count)
            : _list?.Asked == true ? Str("L.S.Refused.None")
            : Str("L.S.Refused.NotYet");
        var rows = new List<RefusedRow>();
        _rows.Children.Clear();
        foreach (var hotkey in refused)
        {
            var row = new RefusedRow(HotkeyText.ToDisplay(hotkey.Hotkey),
                hotkey.Taken ? Str("L.S.Refused.Taken") : string.Format(Str("L.S.Refused.Error"), hotkey.Error),
                UsesOf(hotkey.Hotkey, uses));
            rows.Add(row);
            var line = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var key = EditorCard.Value(row.KeyText);
            key.SetResourceReference(TextBlock.FontFamilyProperty, "Fx.MonoFont");
            key.Margin = new Thickness(0, 0, 12, 0);
            line.Children.Add(key);
            foreach (var use in row.Uses) line.Children.Add(Link(row.KeyText, use));
            line.Children.Add(EditorCard.Value(row.Reason, secondary: true));
            _rows.Children.Add(line);
        }
        Rows = rows;
        // Offered while a key is refused, and only where there is an app to ask.
        bool offered = refused.Count > 0 && _ctx.Services.CheckHotkeysAgain != null;
        // The button goes away under the keyboard (after a good check, mostly): the keyboard goes to the summary
        // line, which says what was found, and stays in the card. Left alone, it would fall back to the window.
        // "Under the keyboard" is asked of the focus scope, which is the window: true as well while the window is
        // not in front, where it is the place the keyboard comes back to.
        if (!offered && _again.IsFocused)
            System.Windows.Input.FocusManager.SetFocusedElement(System.Windows.Input.FocusManager.GetFocusScope(_summary), _summary);
        _again.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        // "Who has it" is a question only for a key that is taken (1409): Windows itself, most likely, where the
        // player bound it with the Win key, and another program for any other key; a known program that is running
        // is named for either.
        var taken = refused.Where(hotkey => hotkey.Taken).ToList();
        FindOwners(windowsItself: taken.Any(hotkey => HotkeyRefusals.BoundWithWin(hotkey.Hotkey)),
            anotherProgram: taken.Any(hotkey => !HotkeyRefusals.BoundWithWin(hotkey.Hotkey)));
    }

    /// <summary>"Check again": the app asks Windows for every hotkey once more, the way a settings change does, and
    /// the card shows the answer at once. It is for a key the player has just freed in the other program: with
    /// hotkeys that work everywhere a key is asked for once and stays as it was answered, so the freed key would
    /// stay listed, and dead, until some setting changed. Nothing is saved. A click within
    /// <see cref="AgainEveryMs"/> of the click before it does nothing, so a held Enter checks once.</summary>
    private void CheckAgain()
    {
        long now = _milliseconds();
        bool held = _clicked is { } before && now - before < AgainEveryMs;
        _clicked = now;
        if (held) return;
        _ctx.Services.CheckHotkeysAgain?.Invoke();
        Refresh();
    }

    /// <summary>Opens the card that sets this use of the key.</summary>
    public void Open(HotkeyUse use)
    {
        if (HotkeyConflicts.Locate(use.Id, SettingsRegistry.Pages) is { } target) _ctx.Host.Navigate(target.PageId, target.ItemId);
    }

    /// <summary>A use of the key, as a button that opens its card. The label is a card's name, or a character's or a
    /// group's: it is shown as text, never read as markup or as an access key. A screen reader hears the key first
    /// ("Ctrl + F5: Next client"): two rows can show the same label.</summary>
    private UIElement Link(string keyText, HotkeyUse use)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = use.Label },
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = HotkeyConflicts.Locate(use.Id, SettingsRegistry.Pages) != null,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, string.Format(Str("L.S.A11y.ForName"), keyText, use.Label));
        button.Click += (_, _) => Open(use);
        return button;
    }

    /// <summary>Who is likely to have the keys: asked only while a key is refused because it is taken (1409). For
    /// a key bound with the Win key the line says that Windows keeps many of those for itself, and the known
    /// programs found running are named under it: PowerToys and AutoHotkey hold Win combinations mostly. For any
    /// other key the usual programs are named at once, and the ones found running replace them when the answer is
    /// in. The running programs are read only while a key is taken. An answer to an older look is dropped.</summary>
    private void FindOwners(bool windowsItself, bool anotherProgram)
    {
        int look = ++_look;
        if (!windowsItself && !anotherProgram)
        {
            Say(null, windowsItself, anotherProgram);
            return;
        }
        if (_runningProcesses != null)
        {
            Say(HotkeyRefusals.Suspects(_runningProcesses()), windowsItself, anotherProgram);
            return;
        }
        Say(Array.Empty<string>(), windowsItself, anotherProgram);
        Task.Run(() =>
        {
            try { return HotkeyRefusals.Suspects(HotkeyRefusals.RunningProcessNames()); }
            catch { return (IReadOnlyList<string>)Array.Empty<string>(); }
        }).ContinueWith(found => Dispatcher.BeginInvoke(new Action(() => { if (look == _look) Say(found.Result, windowsItself, anotherProgram); })),
            TaskScheduler.Default);
    }

    /// <param name="running">The known programs found running, or null where nobody was asked.</param>
    /// <param name="windowsItself">Whether a taken key is bound with the Win key: said first, on a line of its own.</param>
    /// <param name="anotherProgram">Whether a taken key is bound without it: only then are the usual programs named
    /// while none of the known ones is found running. Under the line about Windows they would be a second guess.</param>
    private void Say(IReadOnlyList<string>? running, bool windowsItself, bool anotherProgram)
    {
        Running = running;
        var lines = new List<string>();
        if (windowsItself) lines.Add(Str("L.S.Refused.WinKey"));
        if (running is { Count: > 0 }) lines.Add(string.Format(Str("L.S.Refused.Running"), string.Join(", ", running)));
        else if (running != null && anotherProgram) lines.Add(Str("L.S.Refused.Usual"));
        _owners.Text = string.Join("\n", lines);
        _owners.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
