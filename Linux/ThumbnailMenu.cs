using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>What a thumbnail's menu shows when it opens (the Windows ThumbnailMenuState).</summary>
public sealed record ThumbnailMenuState(string Character, string? System, DateTime? MutedUntil,
    IReadOnlyList<EventSnooze> Snoozes, int Volume, long? CharacterId);

/// <summary>What the menu's items do.</summary>
/// <param name="Mute">Minutes: &gt; 0 for that long, int.MaxValue until unmuted, 0 unmutes.</param>
/// <param name="EndSnooze">Ends one snoozed event (its key), or all of this character's (null).</param>
/// <param name="Audio">-1 mutes the client's sound; 0–100 sets its volume.</param>
public sealed record ThumbnailMenuActions(Action EditLabel, Action<int> Mute, Action<string?> EndSnooze, Action<int> Audio,
    Action OpenSettings, Action<string> CopyText, Action<string> OpenLink, Action CloseClient);

/// <summary>A thumbnail's right-click menu, with the Windows menu's items and order (Views/ThumbnailMenu): the character and
/// system as a caption, Edit label…, Mute alerts ›, Snoozed alerts ›, Game volume ›, Open ‹character›'s settings, the lookup
/// links, and below a line Close client. Built each time it opens, from the state at that moment, so a language change
/// or a snooze that ran out shows at once.</summary>
public static class ThumbnailMenu
{
    /// <summary>The longest timed item of Mute alerts › (the Windows ThumbnailMenu.LongestTimedMuteMinutes).</summary>
    public const int LongestTimedMuteMinutes = 60;

    private static string T(string key, string english) => LocalizationService.Str(key, english);

    /// <summary>The login screen ("EVE", or no name yet) has no character: no settings page, no links.</summary>
    public static bool HasCharacter(string? name) => !string.IsNullOrWhiteSpace(name) && name != "EVE";

    public static ContextMenu Build(ThumbnailMenuState state, ThumbnailMenuActions act, DateTime utcNow)
    {
        bool character = HasCharacter(state.Character);
        // Streamer mode: the caption and "Open {0}'s settings" draw the masked name and system; the state keeps the real ones.
        string shownName = StreamerMask.Name(StreamerKind.Character, state.Character);
        string shownSystem = StreamerMask.Name(StreamerKind.System, state.System);
        var items = new List<Control>();

        items.Add(new MenuItem
        {
            Header = !character ? T("L.Ctx.LoginScreen", "Character select")
                : string.IsNullOrWhiteSpace(shownSystem) ? shownName : $"{shownName} · {shownSystem}",
            IsEnabled = false,
            FontWeight = FontWeight.SemiBold,
        });
        if (character) items.Add(Item(T("L.Ctx.EditLabel", "Edit label…"), act.EditLabel));

        // Per-character alert mute: silences one client's border, notification, sound and voice.
        if (character)
        {
            bool muted = state.MutedUntil is { } until && (until == DateTime.MaxValue || until > utcNow);
            var mute = new MenuItem
            {
                Header = !muted ? T("L.Ctx.MuteAlerts", "Mute alerts")
                    : state.MutedUntil == DateTime.MaxValue ? T("L.Ctx.MutedUntilUnmuted", "Alerts muted until you unmute")
                    : string.Format(T("L.Ctx.MutedMinsLeft", "Alerts muted ({0} min left)"),
                        Math.Max(1, (int)Math.Ceiling((state.MutedUntil!.Value - utcNow).TotalMinutes))),
            };
            mute.Items.Add(Item(T("L.Ctx.Mute10", "For 10 minutes"), () => act.Mute(10)));
            mute.Items.Add(Item(T("L.Ctx.Mute30", "For 30 minutes"), () => act.Mute(30)));
            mute.Items.Add(Item(T("L.Ctx.Mute60", "For 1 hour"), () => act.Mute(LongestTimedMuteMinutes)));
            mute.Items.Add(Item(T("L.Ctx.MuteUntil", "Until I unmute"), () => act.Mute(int.MaxValue)));
            mute.Items.Add(new Separator());
            mute.Items.Add(Item(T("L.Ctx.Unmute", "Unmute"), () => act.Mute(0)));
            items.Add(mute);
        }

        // The events snoozed for this character alone: shown only while there are some.
        if (character && state.Snoozes.Count > 0)
        {
            var snoozed = new MenuItem { Header = string.Format(T("L.Ctx.SnoozedFmt", "Snoozed alerts ({0})"), state.Snoozes.Count) };
            snoozed.Items.Add(new MenuItem { Header = T("L.Ctx.SnoozeEndHint", "Click one to end its snooze:"), IsEnabled = false });
            foreach (var snooze in state.Snoozes)
            {
                var key = snooze.EventKey;
                snoozed.Items.Add(Item(SnoozeLine(snooze, utcNow), () => act.EndSnooze(key)));
            }
            snoozed.Items.Add(new Separator());
            snoozed.Items.Add(Item(T("L.Ctx.SnoozeEndAll", "End all"), () => act.EndSnooze(null)));
            items.Add(snoozed);
        }

        items.Add(Volume(state.Volume, act.Audio));

        if (character)
            items.Add(Item(string.Format(T("L.Ctx.OpenSettingsFmt", "Open {0}'s settings"), shownName), act.OpenSettings));

        // Lookup links: each only when the thumbnail knows what it needs. The login screen has none.
        var system = character && !string.IsNullOrWhiteSpace(state.System) ? state.System!.Trim() : null;
        var id = character ? state.CharacterId : null;
        var links = new List<Control>();
        if (system != null) links.Add(Item(T("L.Ctx.CopySystem", "Copy system name"), () => act.CopyText(system)));
        if (LookupLinks.DotlanSystem(system) is { } dotlan) links.Add(Item(T("L.Ctx.OpenDotlan", "Open system on Dotlan"), () => act.OpenLink(dotlan)));
        if (id is { } known)
        {
            links.Add(Item(T("L.Ctx.OpenZKill", "Open character on zKillboard"), () => act.OpenLink(LookupLinks.ZKillboardCharacter(known))));
            links.Add(Item(T("L.Ctx.OpenEveWho", "Open character on EveWho"), () => act.OpenLink(LookupLinks.EveWhoCharacter(known))));
        }
        if (links.Count > 0)
        {
            items.Add(new Separator());
            items.AddRange(links);
        }

        // Last and below a line, away from the everyday items: it ends the client.
        items.Add(new Separator());
        var close = Item(T("L.Ctx.CloseClient", "Close client"), act.CloseClient);
        close.Foreground = Brushes.IndianRed;
        items.Add(close);

        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        foreach (var item in items) menu.Items.Add(item);
        return menu;
    }

    /// <summary>"Asteroid depleted · 12 min left", or "… · until MultiPreview restarts" (the Windows SnoozeText.Line).</summary>
    public static string SnoozeLine(EventSnooze snooze, DateTime utcNow)
    {
        var name = EveMultiPreview.Views.AlertHub.EventDisplayName(snooze.EventKey);
        return snooze.UntilRestart
            ? string.Format(T("L.Snooze.UntilRestartFmt", "{0} · until MultiPreview restarts"), name)
            : string.Format(T("L.Snooze.MinsLeftFmt", "{0} · {1} min left"), name, Math.Max(1, (int)Math.Ceiling((snooze.UntilUtc - utcNow).TotalMinutes)));
    }

    /// <summary>Game volume › with a slider, Mute and 100% (unmute). A drag sets the volume once the slider rests for a
    /// moment (each change asks the sound server, so not at every pixel of the drag).</summary>
    private static MenuItem Volume(int volume, Action<int> audio)
    {
        volume = Math.Clamp(volume, 0, 100);
        string Text(int v) => string.Format(T("L.Ctx.AudioFmt", "Game volume: {0}%"), v);
        var item = new MenuItem { Header = Text(volume) };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = volume, Width = 176, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(4, 0) };
        int pending = volume;
        var rest = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        rest.Tick += (_, _) => { rest.Stop(); audio(pending); };
        Ui.Watch(slider, Avalonia.Controls.Primitives.RangeBase.ValueProperty, v =>
        {
            pending = (int)v;
            item.Header = Text(pending);
            rest.Stop();
            rest.Start();
        });
        item.Items.Add(new MenuItem { Header = slider, StaysOpenOnClick = true });
        item.Items.Add(new Separator());
        item.Items.Add(Item(T("L.Ctx.AudioMute", "Mute"), () => { rest.Stop(); audio(-1); }));
        item.Items.Add(Item(T("L.Ctx.Audio100", "100% (unmute)"), () => { rest.Stop(); audio(100); }));
        return item;
    }

    /// <summary>A menu item. Its text is a TextBlock, not the string: a MenuItem reads "_" as an access key and drops it,
    /// and an event's name is the player's own text.</summary>
    private static MenuItem Item(string text, Action click)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        item.Click += (_, _) => click();
        return item;
    }
}
