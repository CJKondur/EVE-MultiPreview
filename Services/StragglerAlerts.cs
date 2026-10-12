using System;
using System.Collections.Generic;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>"Left behind" (IDEAS #5) as App runs it: the look App's one-second timer asks for, and the alert raised
/// for each character the watch answers with. It is handed what App has (the watch, the settings, the characters
/// that have a client, where the log monitor has a character, what an alert is shown with, the history), so a test
/// hands it recorders. The alert is shown and kept the way the log monitor's are: the same dispatcher (mute, snooze,
/// border, badge, toast, sound) and the same history. The window's thread.</summary>
public sealed class StragglerAlerts
{
    private readonly StragglerWatch _watch;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IEnumerable<string>> _running;
    private readonly Func<string, string?> _systemOf;
    private readonly AlertTargets _targets;
    private readonly AlertHistory? _history;
    private readonly Action<Exception> _failed;
    private readonly Func<AppSettings, bool> _isOn;

    /// <param name="watch">The rule. App tells it every system change itself (StragglerWatch.Moved).</param>
    /// <param name="settings">The settings as they are now.</param>
    /// <param name="running">The characters that have a client (ThumbnailManager.GetActiveCharacterNames).</param>
    /// <param name="systemOf">Where the log monitor has a character, or null (LogMonitorService.GetCharacterSystem).</param>
    /// <param name="targets">What an alert is shown with: the one App's alert handler uses.</param>
    /// <param name="history">The alert history, or null where there is none.</param>
    /// <param name="failed">Is told what a look or one alert threw (App logs it). Nothing leaves <see cref="Look"/> as
    /// an exception: a timer whose tick threw would tick no more.</param>
    /// <param name="isOn">Whether to look at all: the event is on for everyone, or (IDEAS #31) for some group. Each
    /// alert a look raises still passes the dispatcher's gate for its own character. Without it: the switch for
    /// everyone (StragglerWatch.IsOn).</param>
    public StragglerAlerts(StragglerWatch watch, Func<AppSettings> settings, Func<IEnumerable<string>> running,
        Func<string, string?> systemOf, AlertTargets targets, AlertHistory? history, Action<Exception> failed,
        Func<AppSettings, bool>? isOn = null)
    {
        _isOn = isOn ?? StragglerWatch.IsOn;
        _watch = watch;
        _settings = settings;
        _running = running;
        _systemOf = systemOf;
        _targets = targets;
        _history = history;
        _failed = failed;
    }

    /// <summary>The groups as the Groups page has them (MergedGroups.Build), each under a name that is its alone: its
    /// place in the list and its name. Two groups can have one name (two colour groups), and the watch remembers
    /// what it judged by the name it is handed: under one name the wave of the one would hide the wave of the other.
    /// That name is shown nowhere.</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Members)> Groups(AppSettings s)
    {
        var groups = new List<(string Name, IReadOnlyList<string> Members)>();
        foreach (var group in SettingsUi.MergedGroups.Build(s))
            groups.Add(($"{groups.Count + 1}: {group.Name}", group.Members));
        return groups;
    }

    /// <summary>One look, once a second: has a jump wave left a member of a group behind? While the event is off it
    /// asks nothing of anybody. A failure costs this look; a failure while one alert is raised costs that alert, and
    /// the others of the look are raised all the same.</summary>
    public void Look(DateTime utcNow)
    {
        IReadOnlyList<Straggler> left;
        try
        {
            var s = _settings();
            if (!_isOn(s)) return;
            left = _watch.Check(utcNow, Groups(s), _running(), _systemOf, StragglerWatch.Wait(s.StragglerWaitSeconds));
        }
        catch (Exception ex)
        {
            _failed(ex);
            return;
        }
        foreach (var one in left)
        {
            try
            {
                Raise(utcNow, one.Character, StragglerWatch.EventKey, StragglerWatch.Severity, one.Text);
            }
            catch (Exception ex)
            {
                _failed(ex);
            }
        }
    }

    /// <summary>Raises an alert MultiPreview worked out itself instead of reading it from a log line, and keeps it in
    /// the history when that is on. It has no rule. Its text is what MultiPreview worked out, never a line of a log:
    /// for "Left behind", where the others went ("Jita → Perimeter 3/4"), so the history and its CSV say that also
    /// when no toast was shown.</summary>
    public AlertOutcome Raise(DateTime utcNow, string character, string alertType, string severity, string? text = null)
    {
        var outcome = AlertDispatcher.Dispatch(_settings(), _targets, character, alertType, severity);
        if (outcome != AlertOutcome.Off && _history is { Enabled: true })
            _history.Record(AlertHistory.Entry(utcNow, character, alertType, severity, _systemOf(character), outcome, text: text));
        return outcome;
    }

    /// <summary>What the toast of an alert says where it shows the system otherwise (AlertHub.PlaceLookup): for "Left
    /// behind", where the others went; null for every other alert, and the toast shows the system.</summary>
    public string? PlaceOf(string character, string alertType) =>
        alertType == StragglerWatch.EventKey ? _watch.WaveOf(character) : null;
}
