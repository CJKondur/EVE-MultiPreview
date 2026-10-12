using System;
using System.Globalization;
using System.Threading.Tasks;
using EveMultiPreview.Views;

namespace EveMultiPreview.Services;

/// <summary>What App does for Windows notifications (IDEAS #33) besides making the way: what a click's two buttons
/// do, the hop from Windows' thread to the window's, following the switch, and the card's test notification. It was
/// lambdas in App, which no test can run, so nothing held it. Here App is handed in as functions, and a test hands
/// in recorders.</summary>
public sealed class NotificationActions
{
    private readonly Func<bool> _shuttingDown;
    private readonly Func<bool> _settingsLoaded;
    private readonly Func<bool> _on;
    private readonly Func<string, bool> _switchTo;
    private readonly Action<string, string, TimeSpan> _snooze;
    private readonly Action<string> _note;
    private readonly Action<Action> _post;
    private readonly Func<string, string, string> _str;

    /// <param name="shuttingDown">MultiPreview is closing: nothing here does anything any more.</param>
    /// <param name="settingsLoaded">The settings file was read (SettingsService.LoadedSuccessfully). A session whose
    /// settings could not be read runs on default values, where the switch is off: it must not remove the player's
    /// registry key because of that.</param>
    /// <param name="on">"Show alerts as Windows notifications", as the settings say now.</param>
    /// <param name="switchTo">Brings a character's client to the front; false when none is running (App.SwitchToCharacter).</param>
    /// <param name="snooze">(character, event, length): snoozes that event for that character (EventSnoozes.Snooze).</param>
    /// <param name="note">Says a short text near the pointer (ThumbnailManager.ShowTooltipFeedback).</param>
    /// <param name="post">Runs an action on the window's thread without waiting (Dispatcher.BeginInvoke).</param>
    /// <param name="str">LocalizationService.Str.</param>
    public NotificationActions(Func<bool> shuttingDown, Func<bool> settingsLoaded, Func<bool> on, Func<string, bool> switchTo,
        Action<string, string, TimeSpan> snooze, Action<string> note, Action<Action> post, Func<string, string, string> str)
    {
        _shuttingDown = shuttingDown;
        _settingsLoaded = settingsLoaded;
        _on = on;
        _switchTo = switchTo;
        _snooze = snooze;
        _note = note;
        _post = post;
        _str = str;
    }

    /// <summary>A click on a notification or on its first button: that character's client comes to the front, as
    /// with a click on a toast. A notification can outlive its client (it stays in the Notification Center): when
    /// no client of that character is running the player is told so near the pointer, as the alert history tells
    /// it, instead of a click that does nothing. The name is an argument of the text, never its format.</summary>
    public void SwitchTo(string character)
    {
        if (_switchTo(character)) return;
        _note(string.Format(CultureInfo.CurrentCulture, _str("L.Hist.NotRunning", "{0}'s client isn't running."), character));
    }

    /// <summary>A notification's second button: that event is snoozed for that character, and a note near the
    /// pointer says so, as a toast's menu does it.</summary>
    public void Snooze(string character, string eventKey, TimeSpan length)
    {
        _snooze(character, eventKey, length);
        _note(SnoozeText.Done(character, eventKey));
    }

    /// <summary>The hop: a click arrives on a thread of Windows', and what it means is done on the window's. Nothing
    /// is done once MultiPreview has begun to close, which it may have by the time the window's thread gets there.</summary>
    public void OnWindowThread(Action action) => _post(() =>
    {
        if (!_shuttingDown()) action();
    });

    /// <summary>The registry key and what is in the Notification Center follow the switch (NotificationsWay.Follow):
    /// at startup and after every settings change, on the window's thread. Not while MultiPreview is closing, and
    /// not in a session whose settings could not be read.</summary>
    public void Follow(NotificationsWay way, NotificationRegistration registration, bool starting = false)
    {
        if (_shuttingDown() || !_settingsLoaded()) return;
        way.Follow(_on(), registration, starting);
    }

    /// <summary>The card's "Show a test notification", on the window's thread. The switch is followed first: the
    /// card appears the moment the switch goes on, a second before the settings change is applied, and Windows
    /// shows nothing for a program whose key is not there yet. False: nothing was asked of Windows.</summary>
    public bool ShowTest(NotificationsWay way, NotificationRegistration registration, DateTime utcNow)
    {
        if (_shuttingDown() || !_on()) return false;
        Follow(way, registration);
        return way.ShowTest(NotificationsWay.TestTexts(_str), utcNow);
    }

    /// <summary>The card's question "is Windows itself not showing MultiPreview's notifications", asked from the
    /// window's thread and answered off it (NotificationsWay.BlockedAsync). Only while the switch is on, and the
    /// switch is followed first, for the reason above. False when nothing was asked.</summary>
    public Task<bool> Blocked(NotificationsWay way, NotificationRegistration registration)
    {
        if (_shuttingDown() || !_on()) return Task.FromResult(false);
        Follow(way, registration);
        return way.BlockedAsync();
    }
}
