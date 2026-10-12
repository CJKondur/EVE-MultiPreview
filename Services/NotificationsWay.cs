using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Windows notifications as a way an alert reaches the player (IDEAS #33): every alert that was shown is
/// handed to <see cref="AlertNotifications"/>, which decides what Windows is asked, and a <see cref="WorkPump"/>
/// asks it through an <see cref="IToastNotifier"/>. A click comes back on a thread of Windows' and is done on the
/// window's. It is handed everything it uses, so a test hands it recorders.</summary>
public sealed class NotificationsWay : IAlertWay
{
    private readonly IToastNotifier _notifier;
    private readonly Func<AlertNotifications.Entry, NotificationTexts> _texts;
    private readonly Action<string> _switchTo;
    private readonly Action<string, string, TimeSpan> _snooze;
    private readonly Action<Action> _onWindowThread;
    private readonly Action<Exception> _failed;
    private int _used;          // something this session showed may still be in the Notification Center
    private int _clearFirst;    // take away what an earlier session left, before anything else
    private volatile bool _stopped;

    /// <param name="texts">The texts of an entry, in the player's language. It runs under the ledger's lock and
    /// must not call back into this class.</param>
    /// <param name="switchTo">Brings a character's client to the front (App.SwitchToCharacter).</param>
    /// <param name="snooze">(character, event, length): snoozes that event for that character (EventSnoozes.Snooze).</param>
    /// <param name="onWindowThread">Runs an action on the window's thread without waiting (Dispatcher.BeginInvoke).</param>
    /// <param name="failed">Is told what Windows threw (App logs it).</param>
    public NotificationsWay(IToastNotifier notifier, Func<AlertNotifications.Entry, NotificationTexts> texts, Action<string> switchTo,
        Action<string, string, TimeSpan> snooze, Action<Action> onWindowThread, Action<Exception>? failed = null)
    {
        _notifier = notifier;
        _texts = texts;
        _switchTo = switchTo;
        _snooze = snooze;
        _onWindowThread = onWindowThread;
        _failed = failed ?? (_ => { });
        Pump = new WorkPump(Drain, _failed);
        _notifier.Clicked += OnClicked;
        _notifier.Dismissed += tag => Ledger.Clicked(tag);   // closed by hand: forgotten; a later alert makes a new entry
    }

    /// <summary>What was shown and what Windows is still to be asked (tests look at it).</summary>
    public AlertNotifications Ledger { get; } = new();

    /// <summary>The pump that talks to Windows (tests wait for it to be idle).</summary>
    public WorkPump Pump { get; }

    /// <summary>Something this session showed may still be in the Notification Center: a notification was shown and
    /// they were not all taken away since.</summary>
    public bool Used => Volatile.Read(ref _used) != 0;

    public bool IsOn(AppSettings s) => s.WindowsNotifications;

    public void Raised(AlertNotice notice, AppSettings s)
    {
        if (_stopped) return;
        if (Ledger.Raised(notice, s.WindowsNotifications, s.WindowsNotificationsFrom, _texts)) Pump.Wake();
    }

    /// <summary>A character's client came to the front, or is gone (AlertAttention.Looked): its notifications are
    /// taken away.</summary>
    public void Looked(string character)
    {
        if (!_stopped && Ledger.Looked(character)) Pump.Wake();
    }

    /// <summary>Keeps the registry key and what is in the Notification Center in step with the switch. Called on
    /// the window's thread: at startup, and after every settings change.
    /// On: the key is written where it differs; at the start, and when the key was not known to be there, what an
    /// earlier session left is taken away once, on the pump.
    /// Off: MultiPreview's notifications are taken away first, at once, while Windows still knows whose they are;
    /// then the key is removed. In a session that never had the feature on, Windows' notifications are not asked
    /// anything; and once everything is taken away and the key is gone, a further "off" asks nothing more.</summary>
    public void Follow(bool on, NotificationRegistration registration, bool starting = false)
    {
        if (_stopped) return;
        if (on)
        {
            bool known = registration.Registered;
            if (registration.Follow(true) && (starting || !known))
            {
                Interlocked.Exchange(ref _clearFirst, 1);
                Pump.Wake();
            }
            return;
        }
        if (registration.Registered || Used) TakeAllAway();
        registration.Follow(false);
    }

    /// <summary>The pump's work: asks Windows what the ledger wants asked, in order.</summary>
    private Task Drain()
    {
        if (Interlocked.Exchange(ref _clearFirst, 0) == 1) Do(new NotifierWork(NotifierWork.Kind.Clear));
        while (!_stopped && Ledger.NextWork() is { } work) Do(work);
        return Task.CompletedTask;
    }

    private void Do(NotifierWork work)
    {
        try
        {
            switch (work.What)
            {
                case NotifierWork.Kind.Show:
                    Interlocked.Exchange(ref _used, 1);   // before the call: one that throws may have shown it all the same
                    _notifier.Show(work.Content!);
                    break;
                case NotifierWork.Kind.Remove:
                    _notifier.Remove(work.Tag!);
                    break;
                default:
                    _notifier.Clear();
                    Interlocked.Exchange(ref _used, 0);
                    break;
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>Everything of MultiPreview's is taken out of the Notification Center now, on this thread, and
    /// nothing is left for the pump.</summary>
    private void TakeAllAway()
    {
        Ledger.Clear();
        while (Ledger.NextWork() != null) { }
        Interlocked.Exchange(ref _clearFirst, 0);
        Do(new NotifierWork(NotifierWork.Kind.Clear));
    }

    /// <summary>A click, on a thread of Windows'. Only words and a tag of this session mean anything; what they
    /// mean is done on the window's thread.</summary>
    private void OnClicked(string arguments)
    {
        try
        {
            if (_stopped || NotificationXml.Read(arguments) is not { } click) return;
            var entry = Ledger.Clicked(click.Tag);
            if (entry == null) return;
            _onWindowThread(() =>
            {
                if (click.Verb == "nap") _snooze(entry.Character, entry.EventKey, AlertNotifications.SnoozeFor);
                else _switchTo(entry.Character);
            });
        }
        catch (Exception ex) { Report(ex); }
    }

    private void Report(Exception ex)
    {
        try { _failed(ex); } catch (Exception) { /* reporting must never cost the alert */ }
    }

    /// <summary>MultiPreview is closing: nothing more is shown, a click means nothing any more, and what this
    /// session showed is taken away (a button of a closed MultiPreview can do nothing). A session that showed
    /// nothing, or took everything away since, does not ask Windows again.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        Pump.Stop();
        if (Used) TakeAllAway();
    }

    /// <summary>The texts of an entry: the character, the event (and how often, from the second alert on), where
    /// it is, and the two buttons. A name is a part or an argument of a text, never its format.</summary>
    /// <param name="eventName">An event's name as the player reads it (AlertHub.EventDisplayName).</param>
    /// <param name="place">(character, event): the system, or for "Left behind" where the others went; null for none.</param>
    /// <param name="str">LocalizationService.Str.</param>
    public static NotificationTexts Texts(AlertNotifications.Entry entry, Func<string, string> eventName, Func<string, string, string?> place,
        Func<string, string, string> str)
    {
        string name = eventName(entry.EventKey);
        string line = entry.Count > 1 ? name + " " + (char)0xD7 + entry.Count.ToString(CultureInfo.CurrentCulture) : name;
        return new NotificationTexts(entry.Character, line, place(entry.Character, entry.EventKey),
            string.Format(CultureInfo.CurrentCulture, str("L.Notify.Switch", "Switch to {0}"), entry.Character),
            str("L.Notify.Snooze", "Snooze for 15 minutes"));
    }
}
