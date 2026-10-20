using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>A toast's right-click menu's command names (ToolStripItem.Name).</summary>
public static class ToastCommands
{
    public const string Dismiss = "toast.dismiss";
    public const string Header = "toast.header";
    public const string Snooze15 = "toast.snooze15";
    public const string Snooze60 = "toast.snooze60";
    public const string SnoozeRestart = "toast.snoozeRestart";
}

/// <summary>What is said about a snooze, in one place for the toast's menu, the thumbnail's menu and the settings
/// page (IDEAS #30). The time is handed in.</summary>
public static class SnoozeText
{
    /// <summary>Whole minutes a timed snooze still runs; a started minute counts, and it is never less than 1.</summary>
    public static int MinutesLeft(EventSnooze snooze, DateTime utcNow) =>
        Math.Max(1, (int)Math.Ceiling((snooze.UntilUtc - utcNow).TotalMinutes));

    /// <summary>"Asteroid depleted · 12 min left", or "… · until MultiPreview restarts".</summary>
    public static string Line(EventSnooze snooze, DateTime utcNow)
    {
        var name = AlertHub.EventDisplayName(snooze.EventKey);
        return snooze.UntilRestart
            ? string.Format(LocalizationService.Str("L.Snooze.UntilRestartFmt", "{0} · until MultiPreview restarts"), name)
            : string.Format(LocalizationService.Str("L.Snooze.MinsLeftFmt", "{0} · {1} min left"), name, MinutesLeft(snooze, utcNow));
    }

    /// <summary>The confirmation after a snooze from a toast: which event, for whom.</summary>
    public static string Done(string character, string eventKey) =>
        string.Format(LocalizationService.Str("L.Snooze.Done", "Snoozed “{0}” for {1}"), AlertHub.EventDisplayName(eventKey), StreamerMask.Name(StreamerKind.Character, character));

    /// <summary>Text for a WinForms menu item, which reads an ampersand as "underline the next letter": a rule's
    /// name is the player's own text and is shown as it is.</summary>
    public static string Menu(string text) => text.Replace("&", "&&");
}

/// <summary>A toast's right-click menu (IDEAS #30): Dismiss, which is what a right-click did until now, and below it
/// three ways to snooze this toast's event for this toast's character. Built for one toast and shown once. While it
/// is open it watches the pointer, and closes when the pointer has left it (<see cref="ClosesNow"/>).</summary>
public sealed class ToastMenu : IDisposable
{
    /// <summary>How far outside the menu the pointer still counts as on it: this many device pixels on every side.</summary>
    public const int AwayMargin = 32;

    /// <summary>How long the pointer has to be away from the menu, without a break, before the menu closes; and how
    /// often it is looked at while the menu is open. Milliseconds.</summary>
    public const int AwayMs = 500, WatchMs = 100;

    private Action? _closed;
    private System.Windows.Forms.Timer? _watch;
    private long? _awaySinceMs;

    /// <summary>Whether the pointer is being watched: only while the menu is open (tests).</summary>
    public bool Watching => _watch != null;

    /// <summary>Whether the menu closes at this look: the pointer has been away from it for <see cref="AwayMs"/>
    /// without a break. "Away" is outside the menu's bounds grown by <see cref="AwayMargin"/> on every side, so a
    /// hand that slips off an edge closes nothing. The menu's place, the pointer's place (both in device pixels)
    /// and the time are handed in; <paramref name="awaySinceMs"/> is what is kept from one look to the next: when
    /// the pointer was first found away, or null while it is on the menu.</summary>
    public static bool ClosesNow(System.Drawing.Rectangle menu, System.Drawing.Point pointer, long nowMs, ref long? awaySinceMs)
    {
        if (System.Drawing.Rectangle.Inflate(menu, AwayMargin, AwayMargin).Contains(pointer))
        {
            awaySinceMs = null;
            return false;
        }
        awaySinceMs ??= nowMs;
        return nowMs - awaySinceMs.Value >= AwayMs;
    }

    /// <param name="eventName">The event's name as the toast shows it (AlertHub.EventDisplayName).</param>
    /// <param name="snooze">How long: 15 minutes, 1 hour, or null for "until MultiPreview restarts".</param>
    /// <param name="closed">What follows the menu closing (its toast's time runs again). Run once, whichever way the
    /// menu goes: an item, the pointer leaving it, or Dispose while it is still open or was never shown.</param>
    public ToastMenu(string characterName, string eventName, Action dismiss, Action<TimeSpan?> snooze, Action? closed = null)
    {
        _closed = closed;
        Strip = new ContextMenuStrip();
        Strip.Opened += (_, _) => StartWatching();
        Strip.Closed += (_, _) =>
        {
            StopWatching();
            RunClosed();
        };
        FluentMenu.Style(Strip, showIcons: false);
        Strip.Items.Add(Item(ToastCommands.Dismiss, "L.Toast.Dismiss", "Dismiss", dismiss));
        Strip.Items.Add(new ToolStripSeparator());
        Strip.Items.Add(new ToolStripLabel
        {
            Name = ToastCommands.Header,
            Text = SnoozeText.Menu(string.Format(LocalizationService.Str("L.Toast.SnoozeFor", "Snooze “{0}” for {1}:"), eventName, StreamerMask.Name(StreamerKind.Character, characterName))),
        });
        Strip.Items.Add(Item(ToastCommands.Snooze15, "L.Toast.Snooze15", "For 15 minutes", () => snooze(TimeSpan.FromMinutes(15))));
        Strip.Items.Add(Item(ToastCommands.Snooze60, "L.Toast.Snooze60", "For 1 hour", () => snooze(TimeSpan.FromHours(1))));
        Strip.Items.Add(Item(ToastCommands.SnoozeRestart, "L.Toast.SnoozeRestart", "Until MultiPreview restarts", () => snooze(null)));
    }

    public ContextMenuStrip Strip { get; }

    /// <summary>The item with this command name (tests).</summary>
    public ToolStripItem Find(string name) => Strip.Items.Cast<ToolStripItem>().First(i => i.Name == name);

    /// <summary>Every command name in the menu.</summary>
    public IReadOnlySet<string> CommandNames() =>
        Strip.Items.Cast<ToolStripItem>().Where(i => !string.IsNullOrEmpty(i.Name)).Select(i => i.Name!).ToHashSet();

    /// <summary>A menu that is still open is closed first. A strip that is disposed while open raises no Closed, so
    /// what follows the closing is run here if it has not run yet, and the watch is stopped here too.</summary>
    public void Dispose()
    {
        try
        {
            StopWatching();
            try { Strip.Close(); } catch { /* it is disposed all the same */ }
            RunClosed();
        }
        finally { Strip.Dispose(); }
    }

    // ── the menu goes away when the pointer does ──
    // A toast is a window that never takes the foreground, and so is this menu: with an EVE client in front, what
    // closes an ordinary menu (a click somewhere else, a key) reaches the game and never this program. So the menu
    // looks at the one thing it can always see, where the pointer is, and closes when the pointer has left it.
    // Nothing is asked about keys or buttons, nothing is hooked, and nothing is brought to the front.

    /// <summary>The menu has opened: the pointer is looked at from now on, and what was kept of an earlier opening
    /// is let go.</summary>
    private void StartWatching()
    {
        StopWatching();
        _awaySinceMs = null;
        _watch = new System.Windows.Forms.Timer { Interval = WatchMs };
        _watch.Tick += (_, _) => Watch();
        _watch.Start();
    }

    /// <summary>The menu has closed, whichever way: no timer is left. Asked twice, the second does nothing.</summary>
    private void StopWatching()
    {
        var watch = _watch;
        _watch = null;
        if (watch == null) return;
        watch.Stop();
        watch.Dispose();
    }

    /// <summary>One look, on the menu's own thread. A menu that is gone ends its own watch. A look that fails ends
    /// the watch as well: the menu is then what it was before, closed by one of its items.</summary>
    private void Watch()
    {
        try
        {
            if (Strip.IsDisposed || !Strip.Visible)
            {
                StopWatching();
                return;
            }
            if (ClosesNow(Strip.Bounds, Cursor.Position, Environment.TickCount64, ref _awaySinceMs)) Strip.Close();
        }
        catch (Exception ex)
        {
            StopWatching();
            App.LogError(ex);
        }
    }

    /// <summary>Runs what follows the menu closing, the first time it is asked and never again.</summary>
    private void RunClosed()
    {
        var run = _closed;
        _closed = null;
        run?.Invoke();
    }

    private static ToolStripMenuItem Item(string name, string key, string english, Action click)
    {
        var item = new ToolStripMenuItem { Name = name, Text = LocalizationService.Str(key, english) };
        item.Click += (_, _) => click();
        return item;
    }
}
