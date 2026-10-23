using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Windows notifications as a way an alert reaches the player (IDEAS #33): every alert that got past the
/// gates is handed to <see cref="AlertNotifications"/>, which decides what Windows is asked, and a
/// <see cref="WorkPump"/> asks it through an <see cref="IToastNotifier"/>. A click comes back on a thread of Windows'
/// and is done on the window's. Every call into Windows runs on another thread and is waited for at most
/// <see cref="CallLimit"/>: a Windows that does not answer costs that one call, never the pump and never the window.
/// It is handed everything it uses (the notifier, the texts, the click's work, the waiting), so a test hands it
/// recorders.</summary>
public sealed class NotificationsWay : IAlertWay
{
    /// <summary>The longest one call into Windows may take (show, take one away, take all away, "are notifications
    /// on"). A call that has not come back by then is given up, and the way goes on with the next thing. That
    /// Windows does not answer is reported once in a session, with a fixed text. (Speech allows a sentence twenty
    /// seconds: SpokenWay.Longest.)</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long a clean-up (the feature switched off, MultiPreview closing) waits for the pump to finish
    /// what it is asking Windows this moment, before it takes everything away. Without the wait a notification that
    /// Windows is being handed just then would land behind the clean-up, and stay.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(2);

    /// <summary>The tag of the card's test notification. Not a tag the ledger makes (those begin with "n"), so a
    /// click on the test says words that mean nothing (NotificationXml.Read). It has no buttons.</summary>
    public const string TestTag = "test";

    /// <summary>How long the test notification stays before Windows removes it by itself.</summary>
    public static readonly TimeSpan TestKeep = TimeSpan.FromMinutes(1);

    /// <summary>After so many calls in a row that Windows did not answer, Windows is asked nothing more until the
    /// switch is followed again with the feature on (<see cref="Follow"/>: a settings change, and the card's
    /// button and question follow it first). Behind every call that is given up may be a thread that Windows
    /// holds, and a Windows that has no answer three times running will have none for the fourth alert.</summary>
    public const int MostGivenUp = 3;

    /// <summary>So many different error codes of Windows are reported in a session, each once; further ones are not.</summary>
    private const int MaxFailedCodes = 16;

    private readonly IToastNotifier _notifier;
    private readonly Func<AlertNotifications.Entry, NotificationTexts> _texts;
    private readonly Action<string> _switchTo;
    private readonly Action<string, string, TimeSpan> _snooze;
    private readonly Action<Action> _onWindowThread;
    private readonly Action<Exception> _failed;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly HashSet<int> _failedCodes = new();     // locked on itself
    private NotificationContent? _test;     // the test notification that waits for the pump
    private int _used;          // something this session showed may still be in the Notification Center
    private int _clearFirst;    // take away what an earlier session left, before anything else
    private int _questionFailed;            // "are notifications on" could not be asked, and that was reported
    private int _givenUp;                   // calls in a row that Windows did not answer in time
    private int _toldGivenUp;               // "Windows did not answer" was reported (once a session)
    private bool _takenAwayAtOff;           // this "off" has had its clean-up (the window's thread only)
    private volatile bool _keyRefused;      // the registry key could not be written: Windows does not know MultiPreview
    private volatile bool _stopped;

    /// <param name="texts">The texts of an entry, in the player's language. It runs under the ledger's lock: it must
    /// not call back into this class and must not wait for another thread.</param>
    /// <param name="switchTo">Brings a character's client to the front (NotificationActions.SwitchTo).</param>
    /// <param name="snooze">(character, event, length): snoozes that event for that character (NotificationActions.Snooze).</param>
    /// <param name="onWindowThread">Runs an action on the window's thread without waiting (NotificationActions.OnWindowThread).</param>
    /// <param name="failed">Is told that Windows refused something, or that a click's work threw: what failed and
    /// the TYPE of what was thrown with its error code (AlertWays.Failure), never its message. App writes what it
    /// is told to error_log.txt, players post that file, and this way's work is made of characters' names. Also
    /// told, with a fixed text: a call Windows did not answer in time, and an error code Windows gave for a
    /// notification it could not show.</param>
    /// <param name="wait">Waits a time, or until told to stop (Task.Delay; a test's ends when the test says).</param>
    public NotificationsWay(IToastNotifier notifier, Func<AlertNotifications.Entry, NotificationTexts> texts, Action<string> switchTo,
        Action<string, string, TimeSpan> snooze, Action<Action> onWindowThread, Action<Exception>? failed = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _notifier = notifier;
        _texts = texts;
        _switchTo = switchTo;
        _snooze = snooze;
        _onWindowThread = onWindowThread;
        _failed = failed ?? (_ => { });
        _wait = wait ?? Task.Delay;
        Pump = new WorkPump(Drain, _failed);
        _notifier.Clicked += OnClicked;
        // Closed by hand: the notification is gone, the pair's banner time is kept (the banner that was just closed
        // does not come back at the next alert). The test notification is no entry.
        _notifier.Dismissed += tag =>
        {
            if (tag != TestTag) Ledger.Dismissed(tag);
        };
        _notifier.Failed += OnFailed;
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
        // While the registry key could not be written Windows does not know MultiPreview: asking it for a
        // notification would fail for every alert, and every failure is a counted error.
        if (_stopped || _keyRefused) return;
        if (Ledger.Raised(notice, s.WindowsNotifications, s.WindowsNotificationsFrom, _texts)) Pump.Wake();
    }

    /// <summary>A character's client came to the front, or is gone (AlertAttention.Looked): its notifications are
    /// taken away.</summary>
    public void Looked(string character)
    {
        if (!_stopped && Ledger.Looked(character)) Pump.Wake();
    }

    /// <summary>One event was snoozed for a character (App.SnoozeEvent, IDEAS P86): that client's notification of that
    /// event is taken away.</summary>
    public void Snoozed(string character, string eventKey)
    {
        if (!_stopped && Ledger.Forget(character, eventKey)) Pump.Wake();
    }

    /// <summary>A character's alerts were muted (ThumbnailManager.AlertMuted, IDEAS P86): every notification of that
    /// client is taken away.</summary>
    public void Muted(string character)
    {
        if (!_stopped && Ledger.Forget(character)) Pump.Wake();
    }

    /// <summary>Keeps the registry key and what is in the Notification Center in step with the switch. Called on
    /// the window's thread: at startup, and after every settings change.
    /// On: the key is written where it differs; at the start, and when the key was not known to be there, what an
    /// earlier session left is taken away once, on the pump. What the registration answers is remembered: while
    /// the key could not be written, Windows is asked nothing.
    /// Off: MultiPreview's notifications are taken away first, while Windows still knows whose they are, and what
    /// the pump is asking Windows this moment is let finish before that (at most <see cref="IdleLimit"/>); then the
    /// key is removed. That clean-up is asked once for each switching off, whatever Windows answers: a Windows that
    /// refuses it is not asked again at every settings change. In a session that never had the feature on,
    /// Windows' notifications are not asked anything.
    /// On also starts the count of calls Windows did not answer afresh (<see cref="MostGivenUp"/>): a Windows that
    /// was asked nothing more is asked again.</summary>
    public void Follow(bool on, NotificationRegistration registration, bool starting = false)
    {
        if (_stopped) return;
        if (on)
        {
            Interlocked.Exchange(ref _givenUp, 0);
            _takenAwayAtOff = false;
            bool known = registration.Registered;
            bool registered = registration.Follow(true);
            _keyRefused = !registered;
            if (registered && (starting || !known))
            {
                Interlocked.Exchange(ref _clearFirst, 1);
                Pump.Wake();
            }
            return;
        }
        _keyRefused = false;
        if (registration.Registered || (Used && !_takenAwayAtOff)) TakeAllAway();
        _takenAwayAtOff = true;
        registration.Follow(false);
    }

    /// <summary>The card's "Show a test notification": one notification, shown by the pump under the same limit as
    /// an alert's. It is no entry of the ledger, so a button on it could do nothing: it has none, a click on it
    /// means nothing, and a look at a client does not take it away. It goes by itself after <see cref="TestKeep"/>,
    /// and with everything else when the feature is switched off, MultiPreview closes or Windows ends the session.
    /// Every press does something (FX-m3): one that comes while a test notification is out REPLACES it (the same
    /// tag, which Windows replaces), so there is one at most, and its minute counts anew. Presses that come faster
    /// than the pump asks Windows are one call: the pump asks for the last. False: Windows is not asked for one
    /// (MultiPreview is closing, or the registry key could not be written).</summary>
    /// <param name="utcNow">Now: the test ends by itself <see cref="TestKeep"/> from now.</param>
    public bool ShowTest(NotificationTexts texts, DateTime utcNow)
    {
        if (_stopped || _keyRefused) return false;
        var content = new NotificationContent(TestTag, NotificationXml.Build(texts, TestTag, buttons: false), true, utcNow + TestKeep);
        Interlocked.Exchange(ref _test, content);
        Pump.Wake();
        return true;
    }

    /// <summary>Asks Windows whether it is NOT showing MultiPreview's notifications (they are switched off in
    /// Windows' own settings, or by a policy): on another thread, under the limit of every call. False as well
    /// when Windows could not be asked, and when this way asks Windows nothing. A Windows that can't answer is
    /// reported once in a session: the question is asked at every opening of the settings page, and a Windows
    /// that has no answer once will have none the next time.</summary>
    public async Task<bool> BlockedAsync()
    {
        const string question = "asking Windows whether its notifications are on";
        if (_stopped || _keyRefused) return false;
        bool blocked = false;
        Exception? refused = null;
        bool answered = await Ask(question, () =>
        {
            try { blocked = _notifier.Blocked(); }
            catch (Exception ex) { refused = ex; }
        }).ConfigureAwait(false);
        if (refused != null && Interlocked.Exchange(ref _questionFailed, 1) == 0) Report(question, refused);
        return answered && refused == null && blocked;
    }

    /// <summary>The pump's work: asks Windows what the ledger wants asked, in order.</summary>
    private async Task Drain()
    {
        if (Interlocked.Exchange(ref _clearFirst, 0) == 1) await Do(new NotifierWork(NotifierWork.Kind.Clear)).ConfigureAwait(false);
        while (!_stopped)
        {
            var work = Interlocked.Exchange(ref _test, null) is { } test ? new NotifierWork(NotifierWork.Kind.Show, test) : Ledger.NextWork();
            if (work == null) return;
            await Do(work).ConfigureAwait(false);
        }
    }

    private async Task Do(NotifierWork work)
    {
        switch (work.What)
        {
            case NotifierWork.Kind.Show:
                var content = work.Content!;
                Interlocked.Exchange(ref _used, 1);   // before the call: one that throws or is given up may have shown it all the same
                if (!await Ask("showing a notification", () => _notifier.Show(content)).ConfigureAwait(false)) break;
                // A click, a look or a closing that came while Windows was being asked has taken the entry away.
                // What has just arrived would be a notification no click and no look could reach: it goes again.
                if (content.Tag != TestTag && !Ledger.Holds(content.Tag))
                    await Ask("taking a notification away", () => _notifier.Remove(content.Tag)).ConfigureAwait(false);
                break;
            case NotifierWork.Kind.Remove:
                string tag = work.Tag!;
                await Ask("taking a notification away", () => _notifier.Remove(tag)).ConfigureAwait(false);
                break;
            default:
                if (await Ask("taking the notifications away", () => _notifier.Clear()).ConfigureAwait(false)) Interlocked.Exchange(ref _used, 0);
                break;
        }
    }

    /// <summary>One call into Windows: on another thread, and waited for at most <see cref="CallLimit"/>. True:
    /// Windows did it. False: it threw (reported by type and error code); or it did not come back in time (whatever
    /// the call does later is seen by nobody; said once in a session, with a fixed text); or Windows was not asked
    /// at all, because <see cref="MostGivenUp"/> calls in a row were given up. Nothing leaves here as an exception,
    /// so whoever asked goes on with the next thing.</summary>
    private async Task<bool> Ask(string what, Action call)
    {
        if (Volatile.Read(ref _givenUp) >= MostGivenUp) return false;
        try
        {
            using var limit = new CancellationTokenSource();
            var asked = Task.Run(call);
            var first = await Task.WhenAny(asked, _wait(CallLimit, limit.Token)).ConfigureAwait(false);
            limit.Cancel();     // ends the wait when Windows answered
            if (first != asked)
            {
                _ = asked.ContinueWith(late => _ = late.Exception, TaskScheduler.Default);
                Interlocked.Increment(ref _givenUp);
                if (Interlocked.Exchange(ref _toldGivenUp, 1) == 0)
                    Tell(new TimeoutException("Windows notifications: Windows did not answer within " + (int)CallLimit.TotalSeconds + " seconds, and the call was given up. This is said once. After "
                        + MostGivenUp + " such calls in a row Windows is asked nothing more until the next settings change or test notification."));
                return false;
            }
            Interlocked.Exchange(ref _givenUp, 0);      // Windows answered, whatever it answered
            await asked.ConfigureAwait(false);   // hands on what Windows threw
            return true;
        }
        catch (Exception ex)
        {
            Report(what, ex);
            return false;
        }
    }

    /// <summary>Everything of MultiPreview's is taken out of the Notification Center now, from this thread (the
    /// window's), and nothing is left for the pump. First what waits is dropped and the pump is let finish what it
    /// is asking Windows this moment, at most <see cref="IdleLimit"/>: a notification Windows is being handed just
    /// now lands before the clean-up, not behind it. Then Windows is asked, under the limit of every call.</summary>
    private void TakeAllAway()
    {
        DropWhatWaits();
        WaitForThePump();
        Do(new NotifierWork(NotifierWork.Kind.Clear)).GetAwaiter().GetResult();
    }

    private void DropWhatWaits()
    {
        Ledger.Clear();
        while (Ledger.NextWork() != null) { }
        Interlocked.Exchange(ref _clearFirst, 0);
        Interlocked.Exchange(ref _test, null);
    }

    /// <summary>Waits until the pump is not running, at most <see cref="IdleLimit"/>. Nothing the pump does needs
    /// this thread, so waiting here can't keep it from finishing.</summary>
    private void WaitForThePump()
    {
        var idle = Pump.Idle;
        if (idle.IsCompleted) return;
        try
        {
            using var limit = new CancellationTokenSource();
            Task.WhenAny(idle, _wait(IdleLimit, limit.Token)).GetAwaiter().GetResult();
            limit.Cancel();     // ends the wait when the pump finished
        }
        catch (Exception ex) { Report("waiting for the pump", ex); }
    }

    /// <summary>A click, on a thread of Windows'. Only words and a tag of this session mean anything; what they
    /// mean is done on the window's thread. That work is handed on and runs later, with nothing of this method
    /// around it: so it catches for itself. What it throws (App's own tables are keyed by characters, and say which
    /// key they missed) must not reach the app's line for errors nobody handled, which writes the message.</summary>
    private void OnClicked(string arguments)
    {
        try
        {
            if (_stopped) return;
            if (arguments == NotificationXml.Go + TestTag) return;   // a click on the test notification means nothing
            if (NotificationXml.Read(arguments) is not { } click) return;
            var entry = Ledger.Clicked(click.Tag);
            if (entry == null || entry.Gone) return;   // a gone client (it crashed) has nothing to switch to or to snooze
            _onWindowThread(() =>
            {
                try
                {
                    if (click.Verb == "nap") _snooze(entry.Character, entry.EventKey, AlertNotifications.SnoozeFor);
                    else _switchTo(entry.Character);
                }
                catch (Exception ex) { Report("what a click on a notification does", ex); }
            });
        }
        catch (Exception ex) { Report("a click on a notification", ex); }
    }

    /// <summary>Windows could not show a notification, and says why with an error code (on a thread of Windows').
    /// Each code is reported once in a session, and nothing but the code: with notifications blocked by a policy
    /// every alert would fail the same way.</summary>
    private void OnFailed(int code)
    {
        lock (_failedCodes)
        {
            if (_failedCodes.Count >= MaxFailedCodes || !_failedCodes.Add(code)) return;
        }
        Tell(new InvalidOperationException("Windows notifications: Windows could not show a notification (0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")"));
    }

    /// <summary>Tells the reporter what failed and the type of what was thrown, never the exception itself.</summary>
    private void Report(string what, Exception ex) => Tell(AlertWays.Failure("Windows notifications: " + what, ex));

    /// <summary>Tells the reporter something this class wrote itself: a fixed text, with no name in it.</summary>
    private void Tell(Exception fixedText)
    {
        try { _failed(fixedText); }
        catch (Exception) { /* reporting must never cost the alert */ }
    }

    /// <summary>MultiPreview is closing: nothing more is shown, a click means nothing any more, and what this
    /// session showed is taken away (a button of a closed MultiPreview can do nothing). What the pump is asking
    /// Windows this moment is let finish first, at most <see cref="IdleLimit"/>, so that it does not land behind
    /// the clean-up. A session that showed nothing, or took everything away since, does not ask Windows again.
    /// Never throws: App calls it at exit, and everything below it there must still run.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try
        {
            Pump.Stop();
            TakeAwayWhatWasShown();
        }
        catch (Exception ex) { Report("stopping", ex); }
    }

    /// <summary>Windows is ending the session (signing out, shutting down). What this session showed is taken away
    /// before the app closes, because the exit itself may never get to run: what waits is dropped, the pump is let
    /// finish (at most <see cref="IdleLimit"/>), and Windows is asked once if anything may be there. (WPF then closes
    /// MultiPreview: nothing in it cancels the session's end, and so a shutdown that is cancelled afterwards finds it
    /// closed, as it always did.) Never throws.</summary>
    public void SessionEnding()
    {
        if (_stopped) return;
        try { TakeAwayWhatWasShown(); }
        catch (Exception ex) { Report("clearing up at the end of the session", ex); }
    }

    /// <summary>The clean-up of an exit and of a session's end, from this thread (the window's).</summary>
    private void TakeAwayWhatWasShown()
    {
        DropWhatWaits();
        WaitForThePump();
        if (Used) Do(new NotifierWork(NotifierWork.Kind.Clear)).GetAwaiter().GetResult();
    }

    /// <summary>The texts of an entry: the character, the event (and how often, from the second alert on), where
    /// it is, and the two buttons. A name is a part or an argument of a text, never its format.</summary>
    /// <param name="eventName">An event's name as the player reads it (AlertHub.EventDisplayName).</param>
    /// <param name="place">(character, event): where it is, for an alert whose own content that is ("Left behind":
    /// where the others went); null for every other. A notification is seen over any program, in the Notification
    /// Center and on the lock screen: where a character is does not go there unless it is the alert itself.</param>
    /// <param name="str">LocalizationService.Str.</param>
    public static NotificationTexts Texts(AlertNotifications.Entry entry, Func<string, string> eventName, Func<string, string, string?> place,
        Func<string, string, string> str)
    {
        string name = eventName(entry.EventKey);
        string line = entry.Count > 1 ? name + " " + (char)0xD7 + entry.Count.ToString(CultureInfo.CurrentCulture) : name;
        // Streamer mode (IDEAS #19): a notification is drawn over any program, so the name and the place are masked here; the
        // entry (and so the de-dupe and the click) keeps the real name.
        string who = StreamerMask.Name(StreamerKind.Character, entry.Character);
        string? where = place(entry.Character, entry.EventKey);
        if (where != null) where = StreamerMask.Name(StreamerKind.System, where);
        return new NotificationTexts(who, line, where,
            string.Format(CultureInfo.CurrentCulture, str("L.Notify.Switch", "Switch to {0}"), who),
            str("L.Notify.Snooze", "Snooze for 15 minutes"));
    }

    /// <summary>The texts of the test notification: MultiPreview's name, and a line that says it is a test and goes
    /// by itself. It has no buttons (ShowTest), so the two labels are empty.</summary>
    /// <param name="str">LocalizationService.Str.</param>
    public static NotificationTexts TestTexts(Func<string, string, string> str) =>
        new(NotificationRegistration.DisplayName, str("L.Notify.TestLine", "This is a test. It goes away by itself after a minute."), null, "", "");
}
