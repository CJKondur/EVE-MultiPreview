using System;
using System.Collections.Generic;
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

    /// <summary>The longest a snooze read from a file may still run: the longest MultiPreview itself makes. A file
    /// can come from anywhere, and a snooze silences an alert.</summary>
    public static readonly TimeSpan LongestKept = TimeSpan.FromHours(1);

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
    /// runs out raises nothing.</summary>
    public event Action? Changed;

    /// <summary>Something is snoozed (it may have run out since nobody asked).</summary>
    public bool Any => Volatile.Read(ref _count) > 0;

    /// <summary>Snoozes one event for one character. <paramref name="length"/> null: until MultiPreview restarts.</summary>
    public void Snooze(string character, string eventKey, TimeSpan? length)
    {
        if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(eventKey)) return;
        var until = length is TimeSpan span ? _utcNow() + span : DateTime.MaxValue;
        lock (_lock) Put(new EventSnooze(character, eventKey, until));
        Changed?.Invoke();
    }

    /// <summary>Whether an alert of this event for this character is snoozed right now.</summary>
    public bool IsSnoozed(string character, string eventKey)
    {
        if (Volatile.Read(ref _count) == 0 || string.IsNullOrEmpty(character) || string.IsNullOrEmpty(eventKey)) return false;
        lock (_lock)
        {
            if (!_snoozes.TryGetValue((character, eventKey), out var snooze)) return false;
            if (snooze.UntilUtc > _utcNow()) return true;
            _snoozes.Remove((character, eventKey));   // it ran out
            Volatile.Write(ref _count, _snoozes.Count);
            return false;
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
        Changed?.Invoke();
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
        if (ended > 0) Changed?.Invoke();
        return ended;
    }

    /// <summary>The snoozes that are on, by character and then by event. Those that ran out are dropped.</summary>
    public IReadOnlyList<EventSnooze> Active()
    {
        if (Volatile.Read(ref _count) == 0) return Array.Empty<EventSnooze>();
        var now = _utcNow();
        lock (_lock)
        {
            foreach (var over in _snoozes.Where(kv => kv.Value.UntilUtc <= now).Select(kv => kv.Key).ToList()) _snoozes.Remove(over);
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

    /// <summary>Takes on the snoozes of a file's text, each held to its limits: a character and an event of a sane
    /// length, a time that is still ahead, and at most <see cref="LongestKept"/> from now however far ahead the file
    /// says. Returns how many were taken on. Never throws, and raises nothing.</summary>
    public int Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        Stored? stored;
        try { stored = JsonSerializer.Deserialize<Stored>(json, Json); }
        catch (Exception) { return 0; }
        if (stored?.Snoozes == null) return 0;
        var now = _utcNow();
        int taken = 0;
        lock (_lock)
        {
            foreach (var item in stored.Snoozes)
            {
                if (taken >= Max) break;
                if (item?.U is not DateTime given) continue;
                if (string.IsNullOrEmpty(item.C) || item.C.Length > MaxName || string.IsNullOrEmpty(item.E) || item.E.Length > MaxKey) continue;
                var until = given.Kind == DateTimeKind.Local ? given.ToUniversalTime() : DateTime.SpecifyKind(given, DateTimeKind.Utc);
                if (until <= now) continue;
                if (until - now > LongestKept) until = now + LongestKept;
                Put(new EventSnooze(item.C, item.E, until));
                taken++;
            }
        }
        return taken;
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

    /// <param name="folder">The settings folder (AppPaths.ConfigDir).</param>
    /// <param name="isLink">Whether a path is a link (SettingsHome.IsLink, unless a test says).</param>
    public EventSnoozeFile(string folder, ErrorLog? errors = null, Func<string, bool>? isLink = null)
    {
        _folder = folder;
        _errors = errors ?? ErrorLog.Shared;
        _isLink = isLink ?? SettingsHome.IsLink;
    }

    public string FilePath => Path.Combine(_folder, FileName);

    /// <summary>Reads the file into the snoozes. Returns how many were taken on.</summary>
    public int Load(EventSnoozes into)
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return 0;
            if (_isLink(path)) { Report("the snooze file is a link, and MultiPreview doesn't read snoozes through one", null); return 0; }
            if (new FileInfo(path).Length > MaxFileBytes) { Report("the snooze file is larger than a snooze file can be; it was not read", null); return 0; }
            return into.Restore(File.ReadAllText(path, Utf8));
        }
        catch (Exception ex)
        {
            Report("the snooze file could not be read", ex);
            return 0;
        }
    }

    /// <summary>Writes the timed snoozes that are on, or deletes the file when there are none. False when that failed;
    /// the old file is then as it was.</summary>
    public bool Save(EventSnoozes from)
    {
        var path = FilePath;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (_isLink(_folder) || _isLink(path)) throw new IOException("link");
            var timed = from.Active().Where(s => !s.UntilRestart).ToList();
            if (timed.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            Directory.CreateDirectory(_folder);
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8.GetBytes(EventSnoozes.ToJson(timed, DateTime.MinValue)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Report("the snoozes could not be saved", ex);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>What App does after every settings change and after every change to the snoozes: with keeping on
    /// the file holds what is snoozed now, with it off there is no file.</summary>
    public void Follow(bool keep, EventSnoozes snoozes)
    {
        if (keep) Save(snoozes);
        else Delete();
    }

    /// <summary>Removes the file: the player turned keeping off. A link is left alone.</summary>
    public void Delete()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path) && !_isLink(path)) File.Delete(path);
        }
        catch (Exception ex) { Report("the snooze file could not be removed", ex); }
    }

    /// <summary>A fixed sentence and the exception's type. Never its message (it can hold a path), never a name.</summary>
    private void Report(string what, Exception? ex)
    {
        try { _errors.Record(new IOException($"Alert snoozes: {what}" + (ex == null ? "" : $" ({ex.GetType().Name})")), "AlertSnoozes", DateTime.Now); }
        catch { /* reporting never stops the app */ }
    }
}
