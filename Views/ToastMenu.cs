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
        string.Format(LocalizationService.Str("L.Snooze.Done", "Snoozed “{0}” for {1}"), AlertHub.EventDisplayName(eventKey), character);

    /// <summary>Text for a WinForms menu item, which reads an ampersand as "underline the next letter": a rule's
    /// name is the player's own text and is shown as it is.</summary>
    public static string Menu(string text) => text.Replace("&", "&&");
}

/// <summary>A toast's right-click menu (IDEAS #30): Dismiss, which is what a right-click did until now, and below it
/// three ways to snooze this toast's event for this toast's character. Built for one toast and shown once.</summary>
public sealed class ToastMenu : IDisposable
{
    private Action? _closed;

    /// <param name="eventName">The event's name as the toast shows it (AlertHub.EventDisplayName).</param>
    /// <param name="snooze">How long: 15 minutes, 1 hour, or null for "until MultiPreview restarts".</param>
    /// <param name="closed">What follows the menu closing (its toast's time runs again). Run once, whichever way the
    /// menu goes: an item, a click away, or Dispose while it is still open or was never shown.</param>
    public ToastMenu(string characterName, string eventName, Action dismiss, Action<TimeSpan?> snooze, Action? closed = null)
    {
        _closed = closed;
        Strip = new ContextMenuStrip();
        Strip.Closed += (_, _) => RunClosed();
        FluentMenu.Style(Strip, showIcons: false);
        Strip.Items.Add(Item(ToastCommands.Dismiss, "L.Toast.Dismiss", "Dismiss", dismiss));
        Strip.Items.Add(new ToolStripSeparator());
        Strip.Items.Add(new ToolStripLabel
        {
            Name = ToastCommands.Header,
            Text = SnoozeText.Menu(string.Format(LocalizationService.Str("L.Toast.SnoozeFor", "Snooze “{0}” for {1}:"), eventName, characterName)),
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
    /// what follows the closing is run here if it has not run yet.</summary>
    public void Dispose()
    {
        try
        {
            try { Strip.Close(); } catch { /* it is disposed all the same */ }
            RunClosed();
        }
        finally { Strip.Dispose(); }
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
