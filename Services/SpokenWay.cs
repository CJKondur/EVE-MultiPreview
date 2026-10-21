using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Spoken alerts as a way an alert reaches the player (IDEAS #26): every alert that was shown is handed to
/// <see cref="SpokenAlerts"/>, which decides what is said, and a <see cref="WorkPump"/> says it through an
/// <see cref="ISpeaker"/>, one utterance at a time. Raised and HandedOff return at once on any thread; the voice
/// runs on the pump's. A sentence may wait ten seconds for its turn, so what was asked when its alert was raised is
/// asked again when its turn comes: do-not-disturb, the severities the player chose, and whether the player has
/// dealt with the alert meanwhile. It is handed everything it asks (the settings, do-not-disturb, the names, the
/// clock), so a test hands it recorders.</summary>
public sealed class SpokenWay : IAlertWay
{
    /// <summary>The longest one utterance may take, from the moment its turn has come: asking Windows which voices
    /// it has is part of it. A voice that has not finished by then is given up, so a voice that hangs costs one
    /// sentence and not the rest of the session.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(20);

    /// <summary>So many asks of the voice may be out at once: given up after <see cref="Longest"/>, and not come
    /// back since. Each of them may be a thread that Windows holds. While that many are out the voice is not asked
    /// again: a sentence whose turn comes is given up at once.</summary>
    public const int MostAsksOut = 3;

    /// <summary>So many sentences in a row that the voice threw on (a sound device that is not there): then it is not
    /// asked again until "Speak alerts" is switched off and on, or until the voice answers a "Try" on the Voices card
    /// (<see cref="VoiceAnswered"/>). A voice that hangs is counted by <see cref="MostAsksOut"/> instead.</summary>
    public const int MostFailures = 3;

    private readonly ISpeaker _speaker;
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppSettings, string, bool> _soundsHeld;
    private readonly Func<string, string> _eventName;
    private readonly Func<string, string, string> _str;
    private readonly Func<string> _language;
    private readonly Action<Exception> _failed;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<AlertNotice, bool>? _stillWanted;
    private readonly CancellationTokenSource _stop = new();
    private int _asksOut;           // asks of the voice that were given up and have not come back
    private int _toldAsksOut;       // "the voice is not asked while three asks are out" was reported
    private readonly object _asksLock = new();
    private int _asksEra;           // counted anew from here: asks of an earlier era no longer count when they come back
    private bool _switchedOff;      // "Speak alerts" was off at the last Follow
    private int _failures;          // sentences in a row that the voice threw on; a sentence that is said ends the run
    private string? _countedEvent;  // the event of the sentence said last, when it counted its clients (pump's thread only)
    private DateTime _countedAt;    // when that sentence was said

    /// <param name="settings">The settings as they are now.</param>
    /// <param name="soundsHeld">(settings, severity): do-not-disturb holds that alert's sound right now (DoNotDisturb.Silences).</param>
    /// <param name="eventName">An event's name as the player reads it (AlertHub.EventDisplayName).</param>
    /// <param name="str">LocalizationService.Str.</param>
    /// <param name="language">The app's language, as "en" or "de" (LocalizationService.CurrentLanguage).</param>
    /// <param name="failed">Is told that the voice threw (what failed and the TYPE of what was thrown, never its
    /// message), and when the voice was given up (a fixed text). App writes what it is told to error_log.txt, and
    /// players post that file: a message can repeat the sentence the voice was handed, and with it a character's
    /// name. It may throw itself: that is swallowed.</param>
    /// <param name="wait">Waits a time, or until told to stop (Task.Delay; a test's passes at once).</param>
    /// <param name="stillWanted">Asked for each alert of a sentence when the sentence's turn comes: false when the
    /// player has dealt with the alert since it was raised (looked at its client, muted the character, snoozed the
    /// event). Such an alert leaves the sentence. Asked on the pump's thread. Null: every alert is still wanted.</param>
    public SpokenWay(ISpeaker speaker, Func<AppSettings> settings, Func<AppSettings, string, bool> soundsHeld, Func<string, string> eventName,
        Func<string, string, string> str, Func<string> language, Action<Exception> failed, Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null, Func<AlertNotice, bool>? stillWanted = null)
    {
        _speaker = speaker;
        _settings = settings;
        _soundsHeld = soundsHeld;
        _eventName = eventName;
        _str = str;
        _language = language;
        _failed = failed;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _wait = wait ?? Task.Delay;
        _stillWanted = stillWanted;
        Pump = new WorkPump(Drain, failed);
    }

    /// <summary>What is said and when (tests look at it).</summary>
    public SpokenAlerts Spoken { get; } = new();

    /// <summary>The pump that drives the voice (tests wait for it to be idle).</summary>
    public WorkPump Pump { get; }

    public bool IsOn(AppSettings s) => s.SpeakAlerts;

    /// <summary>An alert was shown. Not for the client in front when it was told (FY-n1): as the notification and the
    /// growing thumbnail, the voice is for the clients the player is NOT looking at. Its sound plays all the same.</summary>
    public void Raised(AlertNotice notice, AppSettings s)
    {
        if (notice.ClientInFront) return;
        Take(notice, s, insist: false);
    }

    /// <summary>The player looked at a client (AlertAttention.Looked): what the voice holds against saying its
    /// alerts again is forgotten (FY-n2).</summary>
    public void Looked(string character) => Spoken.Looked(character);

    /// <summary>Follows "Speak alerts" (on the window's thread, after every settings change). Switched off and on
    /// again, the asks that never came back are counted anew (FX-m4): a voice that hung three times is asked again,
    /// and is not silent for the rest of the session. So is a voice that threw <see cref="MostFailures"/> times in a
    /// row: the run is counted anew (P85).</summary>
    public void Follow(bool on)
    {
        if (!on)
        {
            _switchedOff = true;
            return;
        }
        if (!_switchedOff) return;
        _switchedOff = false;
        lock (_asksLock)
        {
            _asksEra++;
            Volatile.Write(ref _asksOut, 0);
        }
        Interlocked.Exchange(ref _toldAsksOut, 0);
        Volatile.Write(ref _failures, 0);
    }

    /// <summary>The voice spoke a sentence that did not come through this way: the "Try" on Alerts › Sounds › Voices,
    /// heard without throwing (a device that was missing is back). The run of failures ends, so the alerts are spoken
    /// again without "Speak alerts" being switched off and on. It does not count anew the asks that were given up
    /// (a voice that hangs): only a run of throws ends here. Any thread.</summary>
    public void VoiceAnswered() => Volatile.Write(ref _failures, 0);

    /// <summary>The last word of a chain of repeats (AlertEscalation.HandOff): a critical alert was repeated as often
    /// as set and is still unseen. It is said once more, also within the half minute, while spoken alerts are on and
    /// do-not-disturb lets it. It waits for its turn like any sentence, and is asked about again then: once the
    /// player has looked at that client it is not said.</summary>
    public void HandedOff(UnseenAlert alert)
    {
        var s = _settings();
        if (!s.SpeakAlerts) return;
        Take(new AlertNotice(_utcNow(), alert.Character, alert.EventKey, alert.Severity, false), s, insist: true);
    }

    private void Take(AlertNotice notice, AppSettings s, bool insist)
    {
        var taken = Spoken.Raised(notice, SpeechOptions.Of(s), SoundQuietTime(s, notice.EventKey), _soundsHeld(s, notice.Severity), insist);
        if (taken is SpokenAlerts.Taken.Queued or SpokenAlerts.Taken.Merged) Pump.Wake();
    }

    /// <summary>The event's own sound quiet time (Alerts › Sounds), or 0. The table is the settings' own and the
    /// window may be saving it this moment: a read that fails counts as no quiet time.</summary>
    private static int SoundQuietTime(AppSettings s, string eventKey)
    {
        try { return s.SoundCooldowns?.GetValueOrDefault(eventKey, 0) ?? 0; }
        catch (InvalidOperationException) { return 0; }
    }

    /// <summary>The pump's work: says what waits, one after the other, until nothing waits.</summary>
    private async Task Drain()
    {
        while (!_stop.IsCancellationRequested)
        {
            var s = _settings();
            if (!s.SpeakAlerts)
            {
                Spoken.Clear();
                return;
            }
            var next = Spoken.Next(_utcNow(), out var wait);
            if (next == null)
            {
                if (wait <= TimeSpan.Zero) return;
                try { await _wait(wait, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            // Until it has been said, none of it was. What is not said (let go below, or the voice threw or was given
            // up) was never heard: its "said at" marks go, so the same alert is said the next time it comes.
            var unsaid = next.Alerts;
            try
            {
                var left = StillToSay(next, s);
                if (left != null && await Say(left, s).ConfigureAwait(false))
                    unsaid = next.Alerts.Where(alert => !left.Alerts.Contains(alert)).ToArray();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* stopping is no failure */ }
            catch (Exception ex) { Report(Failure("saying a sentence", ex)); }
            finally
            {
                Spoken.NotSaid(unsaid);
                Spoken.Finished();
            }
        }
    }

    /// <summary>What is still said of a sentence whose turn has come, or null: nothing of it. Its alerts were raised
    /// up to ten seconds ago, and the player may have switched do-not-disturb on since, chosen fewer severities, or
    /// dealt with an alert. Do-not-disturb is asked with the sentence's own severity, as the alert sound asks it, so
    /// "Critical alerts still play" keeps a critical sentence.</summary>
    private Utterance? StillToSay(Utterance next, AppSettings s)
    {
        if (_soundsHeld(s, next.Severity) || !AlertWays.Reaches(s.SpeakAlertsFrom, next.Severity)) return null;
        if (_stillWanted == null) return next;
        var wanted = next.Alerts.Where(Wanted).ToArray();
        if (wanted.Length == next.Alerts.Count) return next;
        return wanted.Length == 0 ? null : next with { Characters = wanted.Select(alert => alert.Character).ToArray(), Alerts = wanted };
    }

    /// <summary>Asks whether an alert is still wanted. A question that throws must not cost the alert its voice: it
    /// counts as wanted, and the failure is reported (a table keyed by a character names the key it missed, which
    /// is why a report never carries a message).</summary>
    private bool Wanted(AlertNotice alert)
    {
        try { return _stillWanted!(alert); }
        catch (Exception ex)
        {
            Report(Failure("asking whether an alert is still wanted", ex));
            return true;
        }
    }

    /// <summary>Says a sentence. False: the voice had not finished after the longest time and was given up, which
    /// is reported here; or it was not asked at all, because <see cref="MostAsksOut"/> asks that were given up have
    /// not come back (a voice that hangs for good: every further ask could be one more thread that Windows holds).
    /// That is reported once in a session, with a fixed text. What the voice threw leaves as it is; the loop
    /// reports it by its type.</summary>
    private async Task<bool> Say(Utterance utterance, AppSettings s)
    {
        // A voice that threw MostFailures times in a row is not asked again until Follow counts anew; that was
        // reported when it came to that.
        if (Volatile.Read(ref _failures) >= MostFailures) return false;
        if (Volatile.Read(ref _asksOut) >= MostAsksOut)
        {
            if (Interlocked.Exchange(ref _toldAsksOut, 1) == 0)
                Report(new TimeoutException(MostAsksOut + " spoken alerts were given up and the voice has not come back from any of them. It is not asked again until it comes back from one."));
            return false;
        }
        // "More clients" for a sentence that counts right after one that counted, for the same event, a moment ago.
        bool counts = SpokenAlerts.Counts(utterance, _eventName);
        bool more = counts && !utterance.Still && _countedEvent == utterance.EventKey && _utcNow() - _countedAt <= SpokenAlerts.MaxAge;
        string text = SpokenAlerts.Text(utterance, _eventName, _str, more);
        string language = _language();
        int volume = SpeechOptions.Of(s).Volume;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var told = limit.Token;     // read now: the voice may still hold it when the limit is over and disposed
        // The limit runs before the voice is asked anything, and the voice is asked on another thread: which voices
        // Windows has is asked for every sentence, and a Windows that does not answer that must cost this sentence
        // and not the pump.
        var giveUp = GiveUp(told);
        var said = Task.Run(() =>
        {
            string voice = SpeechVoiceChoice.For(utterance.Severity, s.SpeechVoices, _speaker.Voices(), language, _speaker.DefaultVoice());
            told.ThrowIfCancellationRequested();    // given up while Windows was asked: nothing is said late
            return _speaker.SayAsync(text, voice, volume, told);
        }, told);
        var first = await Task.WhenAny(said, giveUp).ConfigureAwait(false);
        limit.Cancel();   // ends the wait when the voice finished, and tells the voice to stop when the wait did
        if (first != said)
        {
            // Whatever it does later is seen by nobody, but that it came back at all is counted: the count is taken
            // down on the thread that brings the ask back, in the same breath. Not when the count was begun anew
            // since (Follow): this ask is no longer in it.
            int era;
            lock (_asksLock)
            {
                era = _asksEra;
                _asksOut++;
            }
            _ = said.ContinueWith(late =>
            {
                _ = late.Exception;
                lock (_asksLock)
                    if (era == _asksEra) _asksOut--;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _stop.Token.ThrowIfCancellationRequested();
            Report(new TimeoutException("A spoken alert was not finished after " + (int)Longest.TotalSeconds + " seconds and was given up."));
            return false;
        }
        try { await said.ConfigureAwait(false); }   // hands on what the voice threw
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Counted here and not in the loop: only what the VOICE threw counts. The run ends the way it is reported
            // (a fixed text, no name), once; the failure itself is reported by the loop, by its type.
            if (Interlocked.Increment(ref _failures) == MostFailures)
                Report(new InvalidOperationException("Spoken alerts: the voice failed " + MostFailures + " times in a row and is not asked again until spoken alerts are switched off and on, or a voice's Try on the Voices card is heard."));
            throw;
        }
        Volatile.Write(ref _failures, 0);
        _countedEvent = counts ? utterance.EventKey : null;
        _countedAt = _utcNow();
        return true;
    }

    /// <summary>Finishes when the longest time for one utterance is over, or when told to stop.</summary>
    private async Task GiveUp(CancellationToken stop)
    {
        try { await _wait(Longest, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private void Report(Exception ex)
    {
        try { _failed(ex); } catch (Exception) { /* reporting must never stop the voice */ }
    }

    /// <summary>What is reported of something that was thrown: what failed, and the TYPE of what was thrown (for
    /// several at once the type of each). Never the exception itself: neither its message nor what it carries may
    /// reach the error log (see the constructor).</summary>
    private static Exception Failure(string what, Exception ex) =>
        new InvalidOperationException("Spoken alerts: " + what + " failed (" + (ex is AggregateException several
            ? string.Join(", ", several.Flatten().InnerExceptions.Select(inner => inner.GetType().Name))
            : ex.GetType().Name) + ")");

    /// <summary>MultiPreview is closing: nothing more is said, and what is being said is cut off. Never throws: App
    /// calls it first of all at exit, and what the voice does when it is told to stop runs in here, on this thread.
    /// If that left as an exception, the alert history would not be written and the settings and the tray icon
    /// would not be closed. It is reported by its type, and the rest is done.</summary>
    public void Stop()
    {
        try { _stop.Cancel(); }
        catch (Exception ex) { Report(Failure("telling the voice to stop", ex)); }
        Pump.Stop();
        Spoken.Clear();
    }
}
