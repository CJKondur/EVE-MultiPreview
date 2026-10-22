using System;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace EveMultiPreview.SettingsUi;

/// <summary>"Built-in…" next to Change… wherever a sound is picked (IDEAS P15): a menu of the five generated tones
/// (ToneSounds). The editor stores the file a tone is written to exactly as it stores a picked one, so the same rules
/// hold for it (CustomRuleCheck.LocalSound). Choosing a tone only stores it: nothing is played.</summary>
public static class ToneMenu
{
    private static string Str(string key) => SettingsContext.Str(key);

    /// <summary>A menu with one item for each tone, in ToneSounds.All's order, named from the catalog. Choosing an item
    /// calls <paramref name="picked"/>.</summary>
    public static ContextMenu Build(Action<Tone> picked)
    {
        var menu = new ContextMenu();
        foreach (var tone in ToneSounds.All)
        {
            var chosen = tone;
            var item = new MenuItem { Header = Str(ToneSounds.NameKey(chosen)) };
            item.Click += (_, _) => picked(chosen);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>The button of a row. <paramref name="name"/> is what the row is about (an event, a rule), for screen readers.</summary>
    public static Button Button(string name, Action<Tone> picked)
    {
        Button? button = null;
        button = EditorCard.Button("L.S.Sounds.Tone", () =>
        {
            var menu = Build(picked);
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        });
        AutomationProperties.SetName(button, string.Format(Str("L.S.Sounds.ToneFor"), name));
        return button;
    }

    /// <summary>Writes the tone into the settings folder (ToneSounds.Ensure) and gives the path to store, or null
    /// after saying why not. A folder the sound rule refuses is given back unwritten, so that the editor's own refusal
    /// of a picked file says why, in its own words.</summary>
    public static string? Write(ISettingsHost host, Tone tone, string configDir)
    {
        if (ToneSounds.Ensure(tone, configDir) is { } path) return path;
        var wanted = ToneSounds.PathOf(tone, configDir);
        if (CustomRuleCheck.LocalSound(wanted).Length == 0) return wanted;
        host.Toast(Str("L.S.Sounds.ToneFailed"));
        return null;
    }
}
