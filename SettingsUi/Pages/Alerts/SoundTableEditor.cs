using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>The sound cells' rules, as App.PlayAlertSound reads them. Reading never writes.</summary>
public static class AlertSoundFields
{
    public const int MaxWait = 3600;

    /// <summary>The event's own sound file, or "" (it plays the default sound).</summary>
    public static string OwnFile(AppSettings s, string key) => s.AlertSounds.TryGetValue(key, out var f) ? f?.Trim() ?? "" : "";

    /// <summary>An empty file is the same as none (the old Clear stored ""), so it is removed.</summary>
    public static void SetFile(AppSettings s, string key, string? path)
    {
        path = path?.Trim() ?? "";
        if (path.Length == 0) s.AlertSounds.Remove(key);
        else s.AlertSounds[key] = path;
    }

    /// <summary>A stored whitespace-only path: App.PlayAlertSound takes it as the event's file (it only falls back
    /// to the default for an empty one), finds no such file and plays nothing.</summary>
    public static bool IsBlankOwn(AppSettings s, string key) =>
        s.AlertSounds.TryGetValue(key, out var f) && !string.IsNullOrEmpty(f) && string.IsNullOrWhiteSpace(f);

    /// <summary>What plays for the event: its own file, else the default sound; "" when a blank own path means silence.</summary>
    public static string PlayedFile(AppSettings s, string key) =>
        IsBlankOwn(s, key) ? ""
        : OwnFile(s, key) is { Length: > 0 } own ? own : s.AlertSoundFile?.Trim() ?? "";

    /// <summary>Seconds before the event's sound plays again for one character. Missing, 0 or negative = no wait,
    /// as App.PlayAlertSound reads it (the old box showed 5 for a missing value).</summary>
    public static int Wait(AppSettings s, string key) => s.SoundCooldowns.TryGetValue(key, out var w) && w > 0 ? w : 0;

    public static void SetWait(AppSettings s, string key, int seconds)
    {
        if (seconds <= 0) s.SoundCooldowns.Remove(key);
        else s.SoundCooldowns[key] = Math.Min(seconds, MaxWait);
    }

    /// <summary>Empty is 0. Returns the L.* key of an error, or null.</summary>
    public static string? CheckWait(string? text, out int seconds)
    {
        seconds = 0;
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return null;
        if (ThumbnailFields.TryWhole(t, out var v) && v >= 0 && v <= MaxWait) { seconds = v; return null; }
        return "L.S.Sounds.WaitError";
    }
}

/// <summary>Plays a sound once, as an alert would: the old ▶ ignored the volume (ruling 15).</summary>
public static class AlertSoundPreview
{
    private static readonly List<MediaPlayer> Playing = new();

    /// <summary>Whether <paramref name="file"/> may be played: a plain file on a drive of this PC
    /// (CustomRuleCheck.LocalSound; a path to another computer would make Windows log in to it, IDEAS P63) that
    /// <paramref name="exists"/> says is there. The disk is not asked for a path the rule refuses.</summary>
    public static bool Playable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? file, Func<string, bool> exists) =>
        !string.IsNullOrWhiteSpace(file) && EveMultiPreview.Services.CustomRuleCheck.LocalSound(file).Length > 0 && exists(file);

    /// <summary>Plays <paramref name="file"/> at <paramref name="volumePercent"/>, whether or not alert sounds are on.
    /// False when there is no such file. Never call it in a test with a real file.</summary>
    public static bool Play(string? file, int volumePercent)
    {
        if (!Playable(file, System.IO.File.Exists)) return false;
        try
        {
            var player = new MediaPlayer { Volume = Math.Clamp(volumePercent, 0, 100) / 100.0 };
            void Done() { player.Close(); Playing.Remove(player); }
            player.MediaEnded += (_, _) => Done();
            player.MediaFailed += (_, _) => Done();
            player.Open(new Uri(file));
            player.Play();
            Playing.Add(player);   // held until it ends, so it isn't collected mid-sound
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Alerts › Sounds › Sound for each event (spec §5): the default sound, then each event's own file
/// with Change…, Play, Clear and Wait. App.PlayAlertSound reads all of it at the next alert: a change only saves.</summary>
public sealed class SoundTableEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private static readonly string[] Columns = { "L.S.Sounds.ColEvent", "L.S.Sounds.ColSound", "", "L.S.Sounds.ColWait" };

    private readonly SettingsContext _ctx;
    private readonly EditorTable _table = new();
    private readonly List<string> _keys = new();
    private readonly TextBlock _error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

    public SoundTableEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _error.SetResourceReference(StyleProperty, "Fx.Error");
        var note = EditorCard.Note("L.S.Sounds.Note");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 8, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(_table);
        stack.Children.Add(_error);
        stack.Children.Add(note);
        Content = EditorCard.Build("alerts.sounds.table", null, stack);
        Refresh();
    }

    /// <summary>The event keys in the order shown: one for each row but the default sound's.</summary>
    public IReadOnlyList<string> Keys => _keys;

    private static string Str(string key) => SettingsContext.Str(key);

    public void Refresh()
    {
        _keys.Clear();
        // The Sound and the buttons columns share what the Event and Wait columns leave (the buttons take three parts to
        // the Sound's one, and wrap within theirs); the Sound column keeps a width, and its text wraps, so a narrow page
        // never squeezes it to nothing (nor pushes Wait past the edge).
        _table.Reset(Columns.Select((_, c) => c switch
        {
            1 => new GridLength(1, GridUnitType.Star),
            2 => new GridLength(3, GridUnitType.Star),
            _ => GridLength.Auto,
        }).ToArray());
        _table.Body.ColumnDefinitions[1].MinWidth = 96;
        int header = AddRow(header: true);
        for (int c = 0; c < Columns.Length; c++)
            if (Columns[c].Length > 0) Place(EditorTable.Head(Columns[c]), header, c);
        AddRow(null, Str("L.S.Sounds.Default"), _ctx.S.AlertSoundFile?.Trim() ?? "");
        foreach (var e in AlertEvents.Listed) AddRow(e.Key, AlertEvents.Name(e.Key), AlertSoundFields.OwnFile(_ctx.S, e.Key), AlertSoundFields.IsBlankOwn(_ctx.S, e.Key));
        ShowError(null);
    }

    /// <summary>One row; a null key is the default sound (no Wait cell).</summary>
    private void AddRow(string? key, string name, string own, bool blank = false)
    {
        int r = AddRow();
        if (key == null) Place(EditorCard.Note("L.S.Sounds.Default", strong: true), r, 0);
        else
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(StyleProperty, "Fx.Label");
            Loc.SetKey(label, "L.AlertEvt." + key);
            Place(label, r, 0);
            _keys.Add(key);
        }
        // The file's name; an event without one plays the default sound, and a blank path plays nothing.
        var sound = own.Length > 0 ? EditorCard.Value(System.IO.Path.GetFileName(own), secondary: true)
            : EditorCard.Note(key == null || blank ? "L.S.Sounds.None" : "L.S.Sounds.UsesDefault");
        sound.TextWrapping = TextWrapping.Wrap;
        Place(sound, r, 1);
        // Change…, Built-in…, Play and Clear wrap onto a second line when the column is narrow.
        var buttons = new List<UIElement>
        {
            RowButton("L.S.Sounds.Change", "L.S.Sounds.ChangeFor", name, () => Change(key)),
            ToneMenu.Button(name, tone => UseTone(key, tone)),
            RowButton("L.S.Sounds.Play", "L.S.Sounds.PlayFor", name, () => Play(key)),
        };
        if (own.Length > 0 || blank) buttons.Add(RowButton("L.S.Sounds.Clear", "L.S.Sounds.ClearFor", name, () => Clear(key)));
        Place(EditorCard.WrapRow(buttons.ToArray()), r, 2);
        if (key != null) Place(WaitBox(key, name), r, 3);
    }

    private static Button RowButton(string key, string nameKey, string name, Action click)
    {
        var button = EditorCard.Button(key, click);
        AutomationProperties.SetName(button, string.Format(Str(nameKey), name));
        return button;
    }

    /// <summary>Stores on Enter or when the box loses focus; a bad entry shows an error and the old value comes back.</summary>
    private TextBox WaitBox(string key, string name)
    {
        string Shown() => AlertSoundFields.Wait(_ctx.S, key).ToString(CultureInfo.CurrentCulture);
        var given = Shown();
        var box = new TextBox { Text = given, Width = 56, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, string.Format(Str("L.S.Sounds.WaitFor"), name));
        void Commit()
        {
            if (box.Text == given) return;
            var error = CommitWait(key, box.Text);
            if (error != null)
            {
                box.Text = given;
                ShowError(string.Format(CultureInfo.CurrentCulture, Str(error), 0, AlertSoundFields.MaxWait));
                return;
            }
            ShowError(null);
            box.Text = given = Shown();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Commit(); k.Handled = true; } };
        return box;
    }

    /// <summary>Change…: the open-file dialog, starting at the current file. A null key is the default sound. A file
    /// the sound rule refuses (CustomRuleCheck.LocalSound) is not stored, as in the group and rule pickers: the toast
    /// says why, and the setting stays as it was.</summary>
    public void Change(string? key)
    {
        var current = key == null ? _ctx.S.AlertSoundFile : AlertSoundFields.OwnFile(_ctx.S, key);
        var picked = _ctx.Host.PickFile(current, AlertsPages.SoundFilter);
        if (picked == null) return;
        Take(key, picked);
    }

    /// <summary>Built-in…: one of the generated tones (IDEAS P15). Its file is written into the settings folder, and
    /// stored as a picked file is.</summary>
    public void UseTone(string? key, Tone tone)
    {
        if (ToneMenu.Write(_ctx.Host, tone, _ctx.Service.ConfigDirectory) is { } path) Take(key, path);
    }

    /// <summary>Stores a file the player chose, unless the sound rule refuses it: then the toast says why.</summary>
    private void Take(string? key, string picked)
    {
        if (CustomRuleCheck.LocalSound(picked).Length == 0) _ctx.Host.Toast(Str(AlertsPages.SoundRefusedKey(picked)));
        else Store(key, picked);
    }

    /// <summary>Clear: the event plays the default sound again (or, for the default, no sound).</summary>
    public void Clear(string? key) => Store(key, "");

    private void Store(string? key, string path)
    {
        if (key == null) _ctx.S.AlertSoundFile = path.Trim();
        else AlertSoundFields.SetFile(_ctx.S, key, path);
        _ctx.Applier.Request(ApplyScope.Save);
        Refresh();
    }

    /// <summary>Play: what an alert would play, at the alert volume. Says so when there is nothing to play.</summary>
    public bool Play(string? key)
    {
        var file = key == null ? _ctx.S.AlertSoundFile : AlertSoundFields.PlayedFile(_ctx.S, key);
        if (AlertSoundPreview.Play(file, _ctx.S.AlertSoundVolume)) return true;
        _ctx.Host.Toast(Str("L.S.Sounds.Missing"));
        return false;
    }

    /// <summary>A typed Wait: empty or 0 = no wait, else 1–3600 s. Returns the L.* key of an error, or null.</summary>
    public string? CommitWait(string key, string text)
    {
        var error = AlertSoundFields.CheckWait(text, out var seconds);
        if (error != null) return error;
        if (AlertSoundFields.Wait(_ctx.S, key) == seconds) return null;
        AlertSoundFields.SetWait(_ctx.S, key, seconds);
        _ctx.Applier.Request(ApplyScope.Save);
        return null;
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private int AddRow(bool header = false) => _table.AddRow(header);

    private void Place(FrameworkElement element, int row, int column)
    {
        element.Margin = new Thickness(0, 4, 12, 4);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        _table.Place(element, row, column);
    }
}
