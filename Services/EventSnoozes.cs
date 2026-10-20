using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>One alert event snoozed for one character (IDEAS #30).</summary>
public sealed record EventSnooze(string Character, string EventKey, DateTime UntilUtc)
{
    /// <summary>It lasts until MultiPreview restarts, or until the player ends it.</summary>
    public bool UntilRestart => UntilUtc == DateTime.MaxValue;
}

/// <summary>Which alert events are snoozed for which character, and until when (IDEAS #30). Finer than the
/// whole-character mute, which stays what it is: "Asteroid depleted" can be quiet on one miner while that miner's
/// "Warp scrambled" still alerts. A character is found whatever its case; an event's key is matched exactly.
/// No window in it; any thread; the clock is handed in.</summary>
public sealed class EventSnoozes
{
    /// <summary>The list holds this many. One more makes the snooze that ends soonest give way: the player's newest
    /// choice is never the one refused.</summary>
    public const int Max = 200;

    /// <summary>The longest a character's name and an event's key can be (AlertHistory's limits).</summary>
    public const int MaxName = 100, MaxKey = 64;

    /// <summary>The longest a timed snooze runs: the longest MultiPreview's own menu makes. Snooze holds what it is
    /// asked for to it. A file can come from anywhere, and a snooze silences an alert: an entry of a file that ends
    /// later than this from now (and <see cref="ClockSlack"/>) is not MultiPreview's, and neither is its file:
    /// nothing of that file is taken. (Cut to this length instead, the entry would run a fresh hour after every
    /// start, for ever; left out by itself, a ladder of entries an hour apart would do the same.) And a timed snooze
    /// that has more than this left was made before the clock was set back: it is over.</summary>
    public static readonly TimeSpan LongestKept = TimeSpan.FromHours(1);

    /// <summary>What a PC's clock may be corrected by between the making of a snooze and a look at it, or between
    /// the write of the file and its read, without the snooze counting as longer than MultiPreview makes one. A
    /// one-hour snooze has exactly <see cref="LongestKept"/> left when it is made: without this, a clock that reads
    /// a moment earlier at the next look would end it for good.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(1);

    private sealed class Key : IEqualityComparer<(string Character, string EventKey)>
    {
        public bool Equals((string Character, string EventKey) a, (string Character, string EventKey) b) =>
            string.Equals(a.Character, b.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.EventKey, b.EventKey, StringComparison.Ordinal);

        public int GetHashCode((string Character, string EventKey) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Character), StringComparer.Ordinal.GetHashCode(key.EventKey));
    }

    private readonly object _lock = new();
    private readonly Dictionary<(string Character, string EventKey), EventSnooze> _snoozes = new(new Key());
    private readonly Func<DateTime> _utcNow;
    private int _count;   // read without the lock: with nothing snoozed, asking costs one read

    public EventSnoozes(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Raised after the player's own change (Snooze, End, EndAll), on the thread that made it. A snooze that
    /// runs out raises nothing. Each follower is told on its own: one that throws stops neither the others nor the
    /// one who made the change, and its failure goes to the error log.</summary>
    public event Action? Changed;

    /// <summary>Something is snoozed (it may have run out since nobody asked).</summary>
    public bool Any => Volatile.Read(ref _count) > 0;

    /// <summary>Snoozes one event for one character. <paramref name="length"/> null: until MultiPreview restarts.
    /// A length is held to <see cref="LongestKept"/>; one that is no time at all snoozes nothing.</summary>
    public void Snooze(string character, string eventKey, TimeSpan? length)
    {
        if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(eventKey)) return;
        var until = DateTime.MaxValue;
        if (length is TimeSpan span)
        {
            if (span <= TimeSpan.Zero) return;
            until = _utcNow() + (span > LongestKept ? LongestKept : span);
        }
        lock (_lock) Put(new EventSnooze(character, eventKey, until));
        Tell();
    }

    /// <summary>Whether an alert of this event for this character is snoozed right now.</summary>
    public bool IsSnoozed(string character, string eventKey)
    {
        if (Volatile.Read(ref _count) == 0 || string.IsNullOrEmpty(character) || string.IsNullOrEmpty(eventKey)) return false;
        lock (_lock)
        {
            if (!_snoozes.TryGetValue((character, eventKey), out var snooze)) return false;
            if (On(snooze, _utcNow())) return true;
            _snoozes.Remove((character, eventKey));   // it ran out
            Volatile.Write(ref _count, _snoozes.Count);
            return false;
        }
    }

    /// <summary>Whether a snooze is on at this time. A timed one is over when its time has come, and also while more
    /// than <see cref="LongestKept"/> (and <see cref="ClockSlack"/>) is left of it: nothing here runs that long, so
    /// the clock was set back, and a snooze must not grow by what the clock lost.</summary>
    private static bool On(EventSnooze snooze, DateTime utcNow) =>
        snooze.UntilRestart || (snooze.UntilUtc > utcNow && snooze.UntilUtc - utcNow <= LongestKept + ClockSlack);

    /// <summary>Tells the followers of <see cref="Changed"/>, each on its own.</summary>
    private void Tell()
    {
        var followers = Changed;
        if (followers == null) return;
        foreach (var follower in followers.GetInvocationList())
        {
            try { ((Action)follower)(); }
            catch (Exception ex)
            {
                // A fixed sentence and the exception's type. Never its message, never a name.
                try { ErrorLog.Shared.Record(new InvalidOperationException($"Alert snoozes: what follows a change failed ({ex.GetType().Name})"), "AlertSnoozes", DateTime.Now); }
                catch { /* reporting never stops the app */ }
            }
        }
    }

    /// <summary>Ends one snooze. True when there was one.</summary>
    public bool End(string character, string eventKey)
    {
        if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(eventKey)) return false;
        lock (_lock)
        {
            if (!_snoozes.Remove((character, eventKey))) return false;
            Volatile.Write(ref _count, _snoozes.Count);
        }
        Tell();
        return true;
    }

    /// <summary>Ends every snooze, or every snooze of one character. Returns how many.</summary>
    public int EndAll(string? character = null)
    {
        int ended;
        lock (_lock)
        {
            var keys = _snoozes.Keys.Where(k => character == null || string.Equals(k.Character, character, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keys) _snoozes.Remove(key);
            ended = keys.Count;
            Volatile.Write(ref _count, _snoozes.Count);
        }
        if (ended > 0) Tell();
        return ended;
    }

    /// <summary>The snoozes that are on, by character and then by event. Those that ran out are dropped.</summary>
    public IReadOnlyList<EventSnooze> Active()
    {
        if (Volatile.Read(ref _count) == 0) return Array.Empty<EventSnooze>();
        var now = _utcNow();
        lock (_lock)
        {
            foreach (var over in _snoozes.Where(kv => !On(kv.Value, now)).Select(kv => kv.Key).ToList()) _snoozes.Remove(over);
            Volatile.Write(ref _count, _snoozes.Count);
            return _snoozes.Values.OrderBy(s => s.Character, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.EventKey, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>One character's snoozes that are on.</summary>
    public IReadOnlyList<EventSnooze> Of(string character) =>
        string.IsNullOrEmpty(character) ? Array.Empty<EventSnooze>()
        : Active().Where(s => string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase)).ToList();

    private void Put(EventSnooze snooze)
    {
        var key = (snooze.Character, snooze.EventKey);
        if (!_snoozes.ContainsKey(key) && _snoozes.Count >= Max)
            _snoozes.Remove(_snoozes.MinBy(kv => kv.Value.UntilUtc).Key);
        _snoozes[key] = snooze;
        Volatile.Write(ref _count, _snoozes.Count);
    }

    // ── the file's form ──

    private sealed class Stored
    {
        [JsonPropertyName("snoozes")] public List<Item?>? Snoozes { get; set; }
    }

    private sealed class Item
    {
        [JsonPropertyName("c")] public string? C { get; set; }
        [JsonPropertyName("e")] public string? E { get; set; }
        [JsonPropertyName("u")] public DateTime? U { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The timed snoozes that are still on, as the file holds them. "Until MultiPreview restarts" is never
    /// written: by its meaning it ends there.</summary>
    public static string ToJson(IEnumerable<EventSnooze> snoozes, DateTime utcNow) =>
        JsonSerializer.Serialize(new Stored
        {
            Snoozes = snoozes.Where(s => !s.UntilRestart && s.UntilUtc > utcNow)
                .Select(s => (Item?)new Item { C = s.Character, E = s.EventKey, U = DateTime.SpecifyKind(s.UntilUtc, DateTimeKind.Utc) }).ToList(),
        }, Json);

    /// <summary>Takes on the snoozes of a file's text. The file is taken only when NO entry of it ends later than
    /// <see cref="LongestKept"/> and <see cref="ClockSlack"/> from now: a file MultiPreview wrote never holds such an
    /// entry, so one that does came from somewhere else, and nothing of it is taken. (Left out one by one, a ladder
    /// of entries an hour apart would silence an alert at every start. So what a file can silence is an hour in
    /// all.) Of a file that is taken, an entry is taken only within its limits: a character and an event of a sane
    /// length and written as MultiPreview writes them (<see cref="Plain"/>), and a time that is still ahead. An
    /// entry outside them is left out, never cut to fit. Returns how many snoozes there are that were not there
    /// before: an entry that is in the file twice is one. Never throws, and raises nothing.</summary>
    /// <param name="refused">Called, once, when the file is refused because an entry of it ends too far ahead (IDEAS
    /// P80 (3)): after the clock was set back that can be the player's own file, and nothing else says so.</param>
    public int Restore(string? json, Action? refused = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(json, Json); }
        catch (Exception) { return 0; }
        if (stored?.Snoozes == null) return 0;
        var now = _utcNow();
        // Before any entry is taken, and whatever else is wrong with the entry that says so.
        foreach (var item in stored.Snoozes)
            if (item?.U is DateTime far && Utc(far) - now > LongestKept + ClockSlack)
            {
                refused?.Invoke();
                return 0;
            }
        int taken = 0;
        lock (_lock)
        {
            foreach (var item in stored.Snoozes)
            {
                if (taken >= Max) break;
                if (item?.U is not DateTime given) continue;
                if (string.IsNullOrEmpty(item.C) || item.C.Length > MaxName || string.IsNullOrEmpty(item.E) || item.E.Length > MaxKey) continue;
                if (!Plain(item.C) || !Plain(item.E)) continue;
                var until = Utc(given);
                if (until <= now) continue;
                bool isNew = !_snoozes.ContainsKey((item.C, item.E));
                Put(new EventSnooze(item.C, item.E, until));
                if (isNew) taken++;
            }
        }
        return taken;
    }

    /// <summary>A file's time as UTC: the same moment when it carries a zone, and UTC when it carries none, as
    /// MultiPreview writes it.</summary>
    private static DateTime Utc(DateTime given) =>
        given.Kind == DateTimeKind.Local ? given.ToUniversalTime() : DateTime.SpecifyKind(given, DateTimeKind.Utc);

    /// <summary>A name as MultiPreview itself writes one: no space at either end, no control character, and no
    /// character that only steers the text (a mark that turns it around, a space of no width, a line or paragraph
    /// separator). These names are listed in the settings window and in a thumbnail's menu.</summary>
    private static bool Plain(string text)
    {
        if (text.Length != text.Trim().Length) return false;
        foreach (char c in text)
        {
            if (char.IsControl(c)) return false;
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
        }
        return true;
    }
}

/// <summary>The timed snoozes as a file of their own in the settings folder (IDEAS #30: "keep snoozes across
/// restarts", off unless the player turns it on). Not the settings file, whose format is shared with the AutoHotkey
/// builds. Nothing here throws: a snooze file is never worth a start that fails.</summary>
public sealed class EventSnoozeFile
{
    public const string FileName = "alert_snoozes.json";

    /// <summary>200 snoozes are about 20 KB. A larger file is not MultiPreview's and is not read.</summary>
    public const int MaxFileBytes = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _folder;
    private readonly ErrorLog _errors;
    private readonly Func<string, bool> _isLink;
    private readonly object _gate = new();
    /// <summary>The text Save last put into the file, under _gate. Null when that is not known: nothing written yet,
    /// the file removed, or a write or a removal that failed.</summary>
    private string? _written;
    /// <summary>What <see cref="Report"/> has said since a save last succeeded (IDEAS P80 (4)): each sentence once, so a
    /// settings folder that is a link is one entry in the error log and not one at every change.</summary>
    private readonly HashSet<string> _reported = new();

    /// <param name="folder">The settings folder (AppPaths.ConfigDir).</param>
    /// <param name="isLink">Whether a path is a link (SettingsHome.IsLink, unless a test says).</param>
    public EventSnoozeFile(string folder, ErrorLog? errors = null, Func<string, bool>? isLink = null)
    {
        _folder = folder;
        _errors = errors ?? ErrorLog.Shared;
        _isLink = isLink ?? SettingsHome.IsLink;
    }

    public string FilePath => Path.Combine(_folder, FileName);

    /// <summary>Reads the file into the snoozes. Returns how many were taken on. Nothing is read through a link, be
    /// it the file or the settings folder itself.</summary>
    public int Load(EventSnoozes into)
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return 0;
            if (_isLink(_folder) || _isLink(path)) { Report("the snooze file or its folder is a link, and MultiPreview doesn't read snoozes through one", null); return 0; }
            if (new FileInfo(path).Length > MaxFileBytes) { Report("the snooze file is larger than a snooze file can be; it was not read", null); return 0; }
            return into.Restore(File.ReadAllText(path, Utf8),
                () => Report("the snooze file holds an entry that ends more than an hour and a minute from now (the clock was set back, or the file is not MultiPreview's); none of it was read", null));
        }
        catch (Exception ex)
        {
            Report("the snooze file could not be read", ex);
            return 0;
        }
    }

    /// <summary>Writes the timed snoozes that are on, or deletes the file when there are none. False when that failed;
    /// the old file is then as it was. The text written last is remembered: while the file is there, the same text is
    /// not written a second time (App asks after every settings change), so nothing is replaced and nothing flushed.</summary>
    public bool Save(EventSnoozes from)
    {
        var path = FilePath;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        lock (_gate)
        {
            try
            {
                if (_isLink(_folder) || _isLink(path)) throw new IOException("link");
                var timed = from.Active().Where(s => !s.UntilRestart).ToList();
                if (timed.Count == 0)
                {
                    _written = null;
                    if (File.Exists(path)) File.Delete(path);
                    Saved();
                    return true;
                }
                var text = EventSnoozes.ToJson(timed, DateTime.MinValue);
                if (text == _written && File.Exists(path)) return true;
                Directory.CreateDirectory(_folder);
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Utf8.GetBytes(text));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
                _written = text;
                Saved();
                return true;
            }
            catch (Exception ex)
            {
                _written = null;
                Report("the snoozes could not be saved", ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>What App does after every settings change and after every change to the snoozes: with keeping on
    /// the file holds what is snoozed now, with it off there is no file.</summary>
    public void Follow(bool keep, EventSnoozes snoozes)
    {
        if (keep) Save(snoozes);
        else Delete();
    }

    /// <summary>Removes the file: the player turned keeping off. A link is left alone, and so is a file in a settings
    /// folder that is one.</summary>
    public void Delete()
    {
        lock (_gate)
        {
            _written = null;
            try
            {
                var path = FilePath;
                if (File.Exists(path) && !(_isLink(_folder) || _isLink(path)))
                {
                    File.Delete(path);
                    Saved();
                }
            }
            catch (Exception ex) { Report("the snooze file could not be removed", ex); }
        }
    }

    /// <summary>A save, or the removal of the file, went through: what failed before is told again if it fails again.</summary>
    private void Saved()
    {
        lock (_reported) _reported.Clear();
    }

    /// <summary>A fixed sentence and the exception's type. Never its message (it can hold a path), never a name.</summary>
    private void Report(string what, Exception? ex)
    {
        lock (_reported)
            if (!_reported.Add(what)) return;
        try { _errors.Record(new IOException($"Alert snoozes: {what}" + (ex == null ? "" : $" ({ex.GetType().Name})")), "AlertSnoozes", DateTime.Now); }
        catch { /* reporting never stops the app */ }
    }
}
