using System.Diagnostics;
using System.Globalization;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>S8: the thumbnail text template (IDEAS P2, TextOverlayWindow.SetTextTemplate / Compose: keep in sync) and the
/// weekly playtime behind its {week} (IDEAS P11, ThumbnailManager.UpdatePlaytime / TickPlaytime / SavePlaytime /
/// StopPlaytime / FlushPlaytime / ForgetPlaytime / ClearPlaytime: keep in sync). The shared TextTemplate, PlaytimeLedger and
/// PlaytimeStore do the work; this file only feeds them as ThumbnailManager does.</summary>
public sealed partial class Previews
{
    // ── The text template ──

    /// <summary>What a template was filled from, and the text it gave: the text is rendered again only when one of these
    /// changed, so a tick with nothing new allocates nothing (the timer is kept as whole seconds, not as its text).</summary>
    private readonly record struct TemplateInputs(string Template, string Name, string Label, string System, long TimerSecond, int Jumps, string Week);

    private readonly Dictionary<uint, (TemplateInputs Inputs, string Text)> _templateTexts = new();

    /// <summary>With a template (Thumbnails › Text &amp; borders › Text template) the name line shows the filled template and the
    /// system and timer lines stay empty (TextOverlayWindow: the label, system and timer blocks fold into the name block); a
    /// toggle that is off leaves its token empty. Without one the look is unchanged.</summary>
    private ThumbnailLook TemplateLook(ThumbnailWindow thumb, ThumbnailLook look)
    {
        var template = S.ThumbnailTextTemplate;
        if (string.IsNullOrEmpty(template)) return look;
        var character = thumb.Client.Character;
        bool named = character.Length > 0;
        var inputs = new TemplateInputs(
            template,
            // {name}: empty while "Show character name" is off; a login screen keeps the word its thumbnail shows.
            !S.ShowThumbnailTextOverlay ? "" : named ? StreamerMask.Name(StreamerKind.Character, character) : look.Label,
            named && S.ThumbnailAnnotations.TryGetValue(character, out var note) ? StreamerMask.Name(StreamerKind.Label, note) ?? "" : "",
            named && S.ShowSystemName && _alerts?.SystemOf(character) is { Length: > 0 } sys ? StreamerMask.Name(StreamerKind.System, sys) ?? "" : "",
            S.ShowSessionTimer ? (long)(DateTime.UtcNow - thumb.SessionStartUtc).TotalSeconds : -1,
            // {jumps} and {week}: none for a login screen (TextOverlayWindow.UpdateCharacterName clears them).
            named && _alerts != null ? _alerts.Jumps.CountOf(character) : -1,
            named ? _weeks.GetValueOrDefault(character) ?? "" : "");
        if (!_templateTexts.TryGetValue(thumb.Client.Window, out var cached) || cached.Inputs != inputs)
        {
            var values = new Dictionary<string, string?>
            {
                [TextTemplate.NameToken] = inputs.Name,
                [TextTemplate.LabelToken] = inputs.Label,
                [TextTemplate.SystemToken] = inputs.System,
                [TextTemplate.TimerToken] = inputs.TimerSecond < 0 ? null : TimeSpan.FromSeconds(inputs.TimerSecond).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                [TextTemplate.JumpsToken] = inputs.Jumps < 0 ? null : inputs.Jumps.ToString(CultureInfo.InvariantCulture),
                [TextTemplate.WeekToken] = inputs.Week,
            };
            cached = (inputs, TextTemplate.Render(template, values));
            _templateTexts[thumb.Client.Window] = cached;
        }
        return look with { Label = cached.Text, ShowLabel = cached.Text.Length > 0 };
    }

    /// <summary>A template is set: the system and timer lines are part of the name line (Extras leaves them empty).</summary>
    private bool TemplateOn => !string.IsNullOrEmpty(S.ThumbnailTextTemplate);

    // ── Weekly playtime ──

    /// <summary>Seconds on screen per character per local day (ThumbnailManager.Playtime). Held, and playtime.json read, only
    /// while Count playtime is on.</summary>
    private readonly PlaytimeLedger _playtime = new(() => DateTime.Now);

    private PlaytimeStore? _playtimeFiles;
    private PlaytimeStore PlaytimeFiles => _playtimeFiles ??= new PlaytimeStore(AppPaths.ConfigDir);

    /// <summary>Each running character's week as the template shows it ("14 h"), set once a second; empty while not counted.</summary>
    private readonly Dictionary<string, string?> _weeks = new(StringComparer.OrdinalIgnoreCase);

    private const int PlaytimeSaveSeconds = 300;
    private bool _playtimeLoaded, _playtimeDirty;
    private int _playtimeLoadTries;
    private double _playtimeSinceTry, _playtimeCarry, _playtimeSinceSave;
    private long _playtimeStamp;

    /// <summary>Once a second (ThumbnailManager's session-timer tick): the time since the last one, from a clock no wall-clock
    /// step moves. Also drops the template texts of thumbnails that are gone.</summary>
    private void FollowPlaytime()
    {
        if (_templateTexts.Count > _thumbs.Count)
            foreach (var gone in _templateTexts.Keys.Where(k => !_thumbs.ContainsKey(k)).ToList()) _templateTexts.Remove(gone);
        if (!S.TrackPlaytime) { StopPlaytime(); return; }
        long now = Stopwatch.GetTimestamp();
        double elapsed = _playtimeStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_playtimeStamp, now).TotalSeconds;
        _playtimeStamp = now;
        TickPlaytime(elapsed);
    }

    /// <summary>ThumbnailManager.TickPlaytime: counts the seconds for every character with a thumbnail (not a login screen),
    /// loads the file on the first call after the switch went on (tried again every 30 s of play, 8 times in all), saves every
    /// five minutes and hands each character its week. A fraction is carried; more than a minute at once counts for nothing.</summary>
    private void TickPlaytime(double elapsedSeconds)
    {
        const int retrySeconds = 30, loadAttempts = 8;
        if (!(elapsedSeconds > 0) || double.IsInfinity(elapsedSeconds)) elapsedSeconds = 0;
        if (!_playtimeLoaded)
        {
            _playtimeSinceTry += Math.Min(elapsedSeconds, retrySeconds);
            if (_playtimeLoadTries == 0 || (_playtimeLoadTries < loadAttempts && _playtimeSinceTry >= retrySeconds))
            {
                _playtimeLoadTries++;
                _playtimeSinceTry = 0;
                if (PlaytimeFiles.TryLoad(out var held)) { _playtime.Import(held); _playtimeLoaded = true; }
            }
            if (!_playtimeLoaded) return;   // the file could not be read: count nothing, write nothing, try again later
        }
        _playtimeSinceSave += Math.Min(elapsedSeconds, PlaytimeSaveSeconds);
        double total = _playtimeCarry + elapsedSeconds;
        int whole = (int)Math.Floor(Math.Min(total, 1_000_000));
        _playtimeCarry = whole > PlaytimeLedger.MaxAddSeconds ? 0 : total - whole;
        if (whole is > 0 and <= PlaytimeLedger.MaxAddSeconds)
        {
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _playtime.BeginRound();   // one round per tick, however many characters it counts
            foreach (var thumb in _thumbs.Values)
                if (thumb.Client.Character is { Length: > 0 } name && counted.Add(name) && _playtime.Add(name, whole))
                    _playtimeDirty = true;
        }
        if (_playtimeDirty && _playtimeSinceSave >= PlaytimeSaveSeconds) SavePlaytime();
        PushPlaytime();
    }

    private bool SavePlaytime()
    {
        _playtimeSinceSave = 0;
        if (!_playtimeLoaded) return false;
        if (!PlaytimeFiles.Save(_playtime.Export())) return false;
        _playtimeDirty = false;
        return true;
    }

    /// <summary>Each running character's week, for the template ({week}).</summary>
    private void PushPlaytime()
    {
        _weeks.Clear();
        foreach (var thumb in _thumbs.Values)
            if (thumb.Client.Character is { Length: > 0 } name)
                _weeks[name] = PlaytimeLedger.Format(_playtime.WeekSeconds(name));
    }

    /// <summary>The switch is off: what is held is dropped without a write (the file keeps what was last saved) and {week} goes.</summary>
    private void StopPlaytime()
    {
        if (!_playtimeLoaded && _playtimeLoadTries == 0) return;
        _playtimeLoaded = _playtimeDirty = false;
        _playtimeLoadTries = 0;
        _playtimeSinceTry = 0;
        _playtimeStamp = 0;
        _playtimeCarry = _playtimeSinceSave = 0;
        _playtime.Clear();
        _weeks.Clear();
    }

    /// <summary>At exit: what was counted since the last save is written, if the switch is on.</summary>
    private void FlushPlaytime()
    {
        if (_playtimeLoaded && _playtimeDirty && S.TrackPlaytime) SavePlaytime();
    }

    /// <summary>Forget this character (its page's Forget): the name leaves the ledger and the file at once, and its jump count
    /// goes. False when the name may still be in the file (it could not be read or written).</summary>
    public bool ForgetPlaytime(string name)
    {
        _alerts?.Jumps.Forget(name);
        if (_playtimeLoaded)
        {
            bool saved = true;
            if (_playtime.Forget(name)) { _playtimeDirty = true; saved = SavePlaytime(); }
            PushPlaytime();
            return saved;
        }
        return PlaytimeFiles.Forget(name);
    }

    /// <summary>Clear playtime history: the file (and the ones set aside) is deleted and the ledger emptied, whatever the switch says.</summary>
    public void ClearPlaytime()
    {
        _playtime.Clear();
        _playtimeDirty = false;
        PlaytimeFiles.Delete();
        if (_playtimeLoaded) PushPlaytime();
    }
}
