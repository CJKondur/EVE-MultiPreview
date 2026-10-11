using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>The thumbnail right-click menu's command names (ToolStripItem.Name). The parity test checks the old menu's
/// commands are all still here (Plan 4).</summary>
public static class ThumbnailCommands
{
    public const string Header = "thumb.header";
    public const string EditLabel = "thumb.editLabel";
    public const string Mute = "thumb.mute";
    public const string Mute10 = "thumb.mute10";
    public const string Mute30 = "thumb.mute30";
    public const string Mute60 = "thumb.mute60";
    public const string MuteUntil = "thumb.muteUntil";
    public const string Unmute = "thumb.unmute";
    /// <summary>The events snoozed for this character alone (IDEAS #30): the submenu, its one line of help, one item
    /// per event, and End all.</summary>
    public const string Snoozed = "thumb.snoozed";
    public const string SnoozedHint = "thumb.snoozedHint";
    public const string SnoozedEvent = "thumb.snoozedEvent";
    public const string SnoozedEndAll = "thumb.snoozedEndAll";
    public const string Audio = "thumb.audio";
    public const string AudioSlider = "thumb.audioSlider";
    public const string AudioMute = "thumb.audioMute";
    public const string Audio100 = "thumb.audio100";
    public const string OpenSettings = "thumb.openSettings";
    public const string CloseClient = "thumb.closeClient";
}

/// <summary>What the menu shows when it opens: whose thumbnail it is, the system it was last seen in, the alert mute and
/// the client's volume. <paramref name="Now"/> is passed in so the minutes left can be tested.</summary>
public sealed record ThumbnailMenuState(string CharacterName, string? SystemName, DateTime? MutedUntil, int Volume, DateTime Now)
{
    /// <summary>The events snoozed for this character alone (IDEAS #30).</summary>
    public IReadOnlyList<EventSnooze> Snoozes { get; init; } = Array.Empty<EventSnooze>();
    /// <summary>The time their minutes left are counted from.</summary>
    public DateTime UtcNow { get; init; } = DateTime.UtcNow;
}

/// <summary>A thumbnail's right-click menu (Plan 4, the F2 reference): the character and system as a caption, Edit
/// label…, Mute alerts ›, Game volume ›, Open ‹character›'s settings (spec D6) and, below a line, Close client. Every item
/// raises what the old menu raised. Built once per thumbnail and refreshed each time it opens.</summary>
public sealed class ThumbnailMenu : IDisposable
{
    private readonly Action<int> _audio;
    private readonly List<(ToolStripItem Item, string Key, string English)> _labels = new();
    private readonly ToolStripLabel _header;
    private readonly ToolStripMenuItem _mute, _volume, _openSettings, _snoozed;
    private readonly Action<string?> _endSnooze;
    private readonly TrackBar _slider;
    private bool _settingSlider;

    /// <param name="editLabel">Edit label… (ThumbnailWindow.LabelEditRequested).</param>
    /// <param name="mute">Minutes to mute alerts: &gt;0 for that long, int.MaxValue until unmuted, 0 to unmute.</param>
    /// <param name="audio">-1 mutes the client; 0–100 sets its volume.</param>
    /// <param name="openSettings">Open ‹character›'s settings (D6).</param>
    /// <param name="closeClient">Close client.</param>
    /// <param name="endSnooze">Ends one snoozed event of this character (its key), or all of them (null) (IDEAS #30).</param>
    public ThumbnailMenu(Action editLabel, Action<int> mute, Action<int> audio, Action openSettings, Action closeClient,
        Action<string?>? endSnooze = null)
    {
        _endSnooze = endSnooze ?? (_ => { });
        _audio = audio;
        Strip = new ContextMenuStrip();
        FluentMenu.Style(Strip, showIcons: false);

        _header = new ToolStripLabel { Name = ThumbnailCommands.Header };
        Strip.Items.Add(_header);
        Strip.Items.Add(Item(ThumbnailCommands.EditLabel, "L.Ctx.EditLabel", "Edit label…", editLabel));

        // Per-character alert mute / snooze: silences one client's flash, badge, toast and sound.
        _mute = Item(ThumbnailCommands.Mute, null, "", null);
        FluentMenu.Style(_mute.DropDown, showIcons: false);
        _mute.DropDownItems.Add(Item(ThumbnailCommands.Mute10, "L.Ctx.Mute10", "For 10 minutes", () => mute(10)));
        _mute.DropDownItems.Add(Item(ThumbnailCommands.Mute30, "L.Ctx.Mute30", "For 30 minutes", () => mute(30)));
        _mute.DropDownItems.Add(Item(ThumbnailCommands.Mute60, "L.Ctx.Mute60", "For 1 hour", () => mute(60)));
        _mute.DropDownItems.Add(Item(ThumbnailCommands.MuteUntil, "L.Ctx.MuteUntil", "Until I unmute", () => mute(int.MaxValue)));
        _mute.DropDownItems.Add(new ToolStripSeparator());
        _mute.DropDownItems.Add(Item(ThumbnailCommands.Unmute, "L.Ctx.Unmute", "Unmute", () => mute(0)));
        Strip.Items.Add(_mute);

        // The events snoozed for this character alone (IDEAS #30): shown only while there are some, rebuilt on every open.
        _snoozed = Item(ThumbnailCommands.Snoozed, null, "", null);
        FluentMenu.Style(_snoozed.DropDown, showIcons: false);
        _snoozed.Available = false;
        Strip.Items.Add(_snoozed);

        // Per-client audio (Windows per-process volume, matched by process): a real slider in the submenu.
        _volume = Item(ThumbnailCommands.Audio, null, "", null);
        FluentMenu.Style(_volume.DropDown, showIcons: false);
        _slider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Width = 176,
            Height = 30,
            BackColor = FluentPalette.Drawing("Fx.Flyout"),
        };
        _slider.ValueChanged += (_, _) =>
        {
            if (_settingSlider) return;
            ShowVolume(_slider.Value);
            _audio(_slider.Value);
        };
        _volume.DropDownItems.Add(new ToolStripControlHost(_slider) { Name = ThumbnailCommands.AudioSlider, AutoSize = false, Width = 182, Height = 34 });
        _volume.DropDownItems.Add(new ToolStripSeparator());
        _volume.DropDownItems.Add(Item(ThumbnailCommands.AudioMute, "L.Ctx.AudioMute", "Mute", () => _audio(-1)));
        _volume.DropDownItems.Add(Item(ThumbnailCommands.Audio100, "L.Ctx.Audio100", "100% (unmute)", () => _audio(100)));
        Strip.Items.Add(_volume);

        _openSettings = Item(ThumbnailCommands.OpenSettings, null, "", openSettings);
        Strip.Items.Add(_openSettings);

        // Last and below a line, away from the everyday items: it ends the client.
        Strip.Items.Add(new ToolStripSeparator());
        var close = Item(ThumbnailCommands.CloseClient, "L.Ctx.CloseClient", "Close client", closeClient);
        FluentMenu.MarkDestructive(close);
        Strip.Items.Add(close);
    }

    public ContextMenuStrip Strip { get; }

    /// <summary>The volume slider's position (tests).</summary>
    public int SliderValue => _slider.Value;

    /// <summary>Moves the volume slider as the player would, raising the volume change (tests).</summary>
    public void DragSlider(int value) => _slider.Value = Math.Clamp(value, 0, 100);

    /// <summary>A login-screen thumbnail ("EVE", or no name yet) has no character, so no settings page.</summary>
    public static bool HasCharacter(string? name) => !string.IsNullOrWhiteSpace(name) && name != "EVE";

    /// <summary>Re-reads every label (a language change applies at once, issue #86) and the state. Runs before each open.</summary>
    public void Refresh(ThumbnailMenuState state)
    {
        foreach (var (item, key, english) in _labels) item.Text = LocalizationService.Str(key, english);
        bool character = HasCharacter(state.CharacterName);
        _header.Text = !character ? LocalizationService.Str("L.Ctx.LoginScreen", "Character select")
            : string.IsNullOrWhiteSpace(state.SystemName) ? state.CharacterName
            : $"{state.CharacterName} · {state.SystemName}";

        bool muted = state.MutedUntil.HasValue && (state.MutedUntil.Value == DateTime.MaxValue || state.MutedUntil.Value > state.Now);
        _mute.Text = !muted ? LocalizationService.Str("L.Ctx.MuteAlerts", "Mute alerts")
            : state.MutedUntil!.Value == DateTime.MaxValue ? LocalizationService.Str("L.Ctx.MutedUntilUnmuted", "Alerts muted until you unmute")
            : string.Format(LocalizationService.Str("L.Ctx.MutedMinsLeft", "Alerts muted ({0} min left)"),
                Math.Max(1, (int)Math.Ceiling((state.MutedUntil.Value - state.Now).TotalMinutes)));

        RebuildSnoozed(character ? state.Snoozes : Array.Empty<EventSnooze>(), state.UtcNow);

        int volume = Math.Clamp(state.Volume, 0, 100);
        _settingSlider = true;
        _slider.Value = volume;
        _settingSlider = false;
        ShowVolume(volume);

        _openSettings.Available = character;
        _openSettings.Text = string.Format(LocalizationService.Str("L.Ctx.OpenSettingsFmt", "Open {0}'s settings"), state.CharacterName);
    }

    /// <summary>The Snoozed alerts submenu: a line of help, one item per snoozed event (a click ends it) and End all.
    /// Old items are disposed; with nothing snoozed the submenu is hidden.</summary>
    private void RebuildSnoozed(IReadOnlyList<EventSnooze> snoozes, DateTime utcNow)
    {
        var old = _snoozed.DropDownItems.Cast<ToolStripItem>().ToList();
        _snoozed.DropDownItems.Clear();
        foreach (var item in old) item.Dispose();
        _snoozed.Available = snoozes.Count > 0;
        if (snoozes.Count == 0) return;

        _snoozed.Text = string.Format(LocalizationService.Str("L.Ctx.SnoozedFmt", "Snoozed alerts ({0})"), snoozes.Count);
        _snoozed.DropDownItems.Add(new ToolStripLabel
        {
            Name = ThumbnailCommands.SnoozedHint,
            Text = LocalizationService.Str("L.Ctx.SnoozeEndHint", "Click one to end its snooze:"),
        });
        foreach (var snooze in snoozes)
        {
            var key = snooze.EventKey;
            var item = new ToolStripMenuItem(SnoozeText.Menu(SnoozeText.Line(snooze, utcNow))) { Name = ThumbnailCommands.SnoozedEvent };
            item.Click += (_, _) => _endSnooze(key);
            _snoozed.DropDownItems.Add(item);
        }
        _snoozed.DropDownItems.Add(new ToolStripSeparator());
        var all = new ToolStripMenuItem(LocalizationService.Str("L.Ctx.SnoozeEndAll", "End all")) { Name = ThumbnailCommands.SnoozedEndAll };
        all.Click += (_, _) => _endSnooze(null);
        _snoozed.DropDownItems.Add(all);
    }

    /// <summary>The item with this command name, searched through every submenu (tests).</summary>
    public ToolStripItem Find(string name) => All(Strip.Items).First(i => i.Name == name);

    /// <summary>Every command name in the menu and its submenus, counting hidden items.</summary>
    public IReadOnlySet<string> CommandNames() =>
        All(Strip.Items).Where(i => !string.IsNullOrEmpty(i.Name)).Select(i => i.Name!).ToHashSet();

    public void Dispose() => Strip.Dispose();

    private void ShowVolume(int volume) =>
        _volume.Text = string.Format(LocalizationService.Str("L.Ctx.AudioFmt", "Game volume: {0}%"), volume);

    private ToolStripMenuItem Item(string name, string? key, string english, Action? click)
    {
        var item = new ToolStripMenuItem { Name = name };
        if (key != null)
        {
            item.Text = LocalizationService.Str(key, english);
            _labels.Add((item, key, english));
        }
        if (click != null) item.Click += (_, _) => click();
        return item;
    }

    private static IEnumerable<ToolStripItem> All(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            yield return item;
            if (item is ToolStripMenuItem { HasDropDownItems: true } menu)
                foreach (var child in All(menu.DropDownItems)) yield return child;
        }
    }
}
