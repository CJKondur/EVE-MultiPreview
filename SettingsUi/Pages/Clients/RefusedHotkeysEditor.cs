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
/// register, with what it is bound to (a click opens that card), and the programs that are likely to have it. It
/// shows the app's state, not a setting: nothing here is saved. Looked at again every two seconds while shown.</summary>
public sealed class RefusedHotkeysEditor : System.Windows.Controls.UserControl, IRefreshable
{
    public const string ItemId = "clients.hotkeys.refused";

    private readonly SettingsContext _ctx;
    private readonly RefusedHotkeyList? _list;
    private readonly Func<IReadOnlyList<string>>? _runningProcesses;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _owners = EditorCard.Value("", secondary: true);
    private string _signature = "";
    private int _look;

    /// <param name="runningProcesses">The names of the running processes. Tests hand one in, and it is asked at once.
    /// Without it the system's own list is read, on a background thread.</param>
    public RefusedHotkeysEditor(SettingsContext ctx, Func<IReadOnlyList<string>>? runningProcesses = null)
    {
        _ctx = ctx;
        _list = ctx.Services.Hotkeys?.Refused;      // null in a window opened without the app: nothing was asked
        _runningProcesses = runningProcesses;
        _summary.SetResourceReference(StyleProperty, "Fx.Label");
        _owners.Margin = new Thickness(0, 8, 0, 0);
        _owners.Visibility = Visibility.Collapsed;
        var below = new StackPanel();
        below.Children.Add(_summary);
        below.Children.Add(_rows);
        below.Children.Add(_owners);
        Content = EditorCard.Build(ItemId, null, below);
        LiveRefresh.Attach(this, () => { if (Signature() != _signature) Refresh(); }, seconds: 2);
        Refresh();
    }

    /// <summary>The refused hotkeys as shown, the pause key first.</summary>
    public IReadOnlyList<RefusedRow> Rows { get; private set; } = Array.Empty<RefusedRow>();

    /// <summary>The line above the rows.</summary>
    public string Summary => _summary.Text;

    /// <summary>The known programs found running (empty: none, and the usual ones are named). Null while no key is
    /// refused because another program has it: then nobody is asked.</summary>
    public IReadOnlyList<string>? Running { get; private set; }

    /// <summary>The line that names the likely owners as it is shown, or "" while it is hidden.</summary>
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
            foreach (var use in row.Uses) line.Children.Add(Link(use));
            line.Children.Add(EditorCard.Value(row.Reason, secondary: true));
            _rows.Children.Add(line);
        }
        Rows = rows;
        FindOwners(refused.Any(hotkey => hotkey.Taken));   // "who has it" is a question only for a key another program took
    }

    /// <summary>Opens the card that sets this use of the key.</summary>
    public void Open(HotkeyUse use)
    {
        if (HotkeyConflicts.Locate(use.Id, SettingsRegistry.Pages) is { } target) _ctx.Host.Navigate(target.PageId, target.ItemId);
    }

    /// <summary>A use of the key, as a button that opens its card. The label is a card's name, or a character's or a
    /// group's: it is shown as text, never read as markup or as an access key.</summary>
    private UIElement Link(HotkeyUse use)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = use.Label },
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = HotkeyConflicts.Locate(use.Id, SettingsRegistry.Pages) != null,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, use.Label);
        button.Click += (_, _) => Open(use);
        return button;
    }

    /// <summary>Who is likely to have the keys: asked only while a key is refused because another program has it
    /// (1409). The usual programs are named at once; the ones found running replace them when the answer is in. An
    /// answer to an older look is dropped.</summary>
    private void FindOwners(bool anyTaken)
    {
        int look = ++_look;
        if (!anyTaken)
        {
            Running = null;
            _owners.Visibility = Visibility.Collapsed;
            return;
        }
        if (_runningProcesses != null)
        {
            Say(HotkeyRefusals.Suspects(_runningProcesses()));
            return;
        }
        Say(Array.Empty<string>());
        Task.Run(() =>
        {
            try { return HotkeyRefusals.Suspects(HotkeyRefusals.RunningProcessNames()); }
            catch { return (IReadOnlyList<string>)Array.Empty<string>(); }
        }).ContinueWith(found => Dispatcher.BeginInvoke(new Action(() => { if (look == _look) Say(found.Result); })),
            TaskScheduler.Default);
    }

    private void Say(IReadOnlyList<string> running)
    {
        Running = running;
        _owners.Text = running.Count > 0
            ? string.Format(Str("L.S.Refused.Running"), string.Join(", ", running))
            : Str("L.S.Refused.Usual");
        _owners.Visibility = Visibility.Visible;
    }
}
