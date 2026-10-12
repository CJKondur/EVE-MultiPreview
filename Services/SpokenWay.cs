using System;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>Spoken alerts as a way an alert reaches the player (IDEAS #26): every alert that was shown is handed to
/// <see cref="SpokenAlerts"/>, which decides what is said, and a <see cref="WorkPump"/> says it through an
/// <see cref="ISpeaker"/>, one utterance at a time. Raised and HandedOff return at once on any thread; the voice
/// runs on the pump's. It is handed everything it asks (the settings, do-not-disturb, the names, the clock), so a
/// test hands it recorders.</summary>
public sealed class SpokenWay : IAlertWay
{
    /// <summary>The longest one utterance may take. A voice that has not finished by then is given up, so a voice
    /// that hangs costs one sentence and not the rest of the session.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(20);

    private readonly ISpeaker _speaker;
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppSettings, string, bool> _soundsHeld;
    private readonly Func<string, string> _eventName;
    private readonly Func<string, string, string> _str;
    private readonly Func<string> _language;
    private readonly Action<Exception> _failed;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly CancellationTokenSource _stop = new();

    /// <param name="settings">The settings as they are now.</param>
    /// <param name="soundsHeld">(settings, severity): do-not-disturb holds that alert's sound right now (DoNotDisturb.Silences).</param>
    /// <param name="eventName">An event's name as the player reads it (AlertHub.EventDisplayName).</param>
    /// <param name="str">LocalizationService.Str.</param>
    /// <param name="language">The app's language, as "en" or "de" (LocalizationService.CurrentLanguage).</param>
    /// <param name="failed">Is told what the voice threw, and when it was given up (App logs it). Never a name.</param>
    /// <param name="wait">Waits a time, or until told to stop (Task.Delay; a test's passes at once).</param>
    public SpokenWay(ISpeaker speaker, Func<AppSettings> settings, Func<AppSettings, string, bool> soundsHeld, Func<string, string> eventName,
        Func<string, string, string> str, Func<string> language, Action<Exception> failed, Func<DateTime>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
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
        Pump = new WorkPump(Drain, failed);
    }

    /// <summary>What is said and when (tests look at it).</summary>
    public SpokenAlerts Spoken { get; } = new();

    /// <summary>The pump that drives the voice (tests wait for it to be idle).</summary>
    public WorkPump Pump { get; }

    public bool IsOn(AppSettings s) => s.SpeakAlerts;

    public void Raised(AlertNotice notice, AppSettings s) => Take(notice, s, insist: false);

    /// <summary>The last word of a chain of repeats (AlertEscalation.HandOff): a critical alert was repeated as often
    /// as set and is still unseen. It is said once more, also within the half minute, while spoken alerts are on and
    /// do-not-disturb lets it.</summary>
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
            try { await Say(next, s).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* stopping is no failure */ }
            catch (Exception ex) { Report(ex); }
            finally { Spoken.Finished(); }
        }
    }

    private async Task Say(Utterance utterance, AppSettings s)
    {
        string text = SpokenAlerts.Text(utterance, _eventName, _str);
        string voice = SpeechVoiceChoice.For(utterance.Severity, s.SpeechVoices, _speaker.Voices(), _language());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var said = _speaker.SayAsync(text, voice, SpeechOptions.Of(s).Volume, limit.Token);
        var first = await Task.WhenAny(said, GiveUp(limit.Token)).ConfigureAwait(false);
        limit.Cancel();   // ends the wait when the voice finished, and tells the voice to stop when the wait did
        if (first != said)
        {
            _ = said.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);   // whatever it does later is seen by nobody
            _stop.Token.ThrowIfCancellationRequested();
            throw new TimeoutException("A spoken alert was not finished after " + (int)Longest.TotalSeconds + " seconds and was given up.");
        }
        await said.ConfigureAwait(false);   // hands on what the voice threw
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

    /// <summary>MultiPreview is closing: nothing more is said, and what is being said is cut off.</summary>
    public void Stop()
    {
        _stop.Cancel();
        Pump.Stop();
        Spoken.Clear();
    }
}
