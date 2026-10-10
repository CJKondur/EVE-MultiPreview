using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>One alert as the history keeps it (IDEAS #1). <paramref name="TimeUtc"/> is when MultiPreview raised it.
/// <paramref name="EventKey"/> is the alert type ("attack", or "custom:‹id›" for a rule of the player's own).
/// <paramref name="SolarSystem"/> is where the character was at that moment, "" when not known. <paramref name="Source"/>
/// is "" for a built-in alert and the rule's name for a custom one. <paramref name="Text"/> is what a custom rule
/// matched, cut to <see cref="AlertHistory.MaxText"/> characters; it is always "" for a built-in alert, so the history
/// holds no log text of those.</summary>
public sealed record AlertHistoryEntry(DateTime TimeUtc, string Character, string EventKey, string Severity, string SolarSystem,
    AlertOutcome Outcome, string Source, string Text)
{
    /// <summary>The order alerts were recorded in this session. Not saved.</summary>
    internal long Seq { get; init; }
}

/// <summary>What <see cref="AlertHistory.Read"/> found: the alerts, newest first; whether there were more than asked
/// for; and how many lines of the day files could not be read (a line cut short by a crash, or damage).</summary>
public sealed record AlertHistoryPage(IReadOnlyList<AlertHistoryEntry> Entries, bool More, int BadLines);

/// <summary>The alert history (IDEAS #1): every alert that got past its switch and its quiet time, with what became of
/// it. The newest <see cref="RingSize"/> are held in memory, and each is appended to one file per UTC day in the
/// settings folder's History folder, alerts-yyyy-MM-dd.jsonl: one JSON object per line.
///
/// Why that format: a line is only ever added at the end, so nothing already written is rewritten; a crash in the
/// middle of a write can damage the last line only; the next write first ends such a half line, so it can't swallow
/// the line after it; and reading skips every line it can't read. JSON escapes line breaks, so text another player
/// typed (a custom rule's match) can never start a line of its own.
///
/// <see cref="Record"/> is called on the log monitor's reading thread: it adds to the memory list and to a queue and
/// returns. One background thread does every file write and delete. Nothing here throws into the caller, and a
/// failure is reported once per kind through the error log, without a path or any alert's content.</summary>
public sealed class AlertHistory : IDisposable
{
    public const int RingSize = 2000;
    public const int DefaultKeepDays = 30, MinKeepDays = 1, MaxKeepDays = 365;
    /// <summary>One day's file stops growing here (about 100,000 alerts). Later alerts of that day stay in memory only.</summary>
    public const int MaxDayBytes = 16 * 1024 * 1024;
    /// <summary>Alerts waiting to be written. More than this at once are kept in memory only.</summary>
    public const int QueueLimit = 10000;
    public const int MaxName = 100, MaxKey = 64, MaxText = 160;
    /// <summary>A longer line in a day file was not written by MultiPreview: it is skipped.</summary>
    public const int MaxLineChars = 2048;
    public const string FilePrefix = "alerts-", FileExtension = ".jsonl";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly Regex DayFileName = new(@"^alerts-(\d{4}-\d{2}-\d{2})\.jsonl$", RegexOptions.CultureInvariant);

    private readonly string _folder;
    private readonly ErrorLog _errors;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Queue<AlertHistoryEntry> _ring = new();
    private readonly HashSet<long> _unwritten = new();          // in the ring, not (yet) in a day file
    private readonly HashSet<string> _reported = new();
    private readonly BlockingCollection<object> _queue = new(QueueLimit);
    private Thread? _worker;
    private long _seq;
    private volatile bool _enabled = true;
    private volatile bool _disposed;
    private int _keepDays = DefaultKeepDays;

    /// <param name="folder">The History folder (AppPaths.ConfigDir\History). Created at the first write.</param>
    /// <param name="errors">Where a failure is reported, once per kind.</param>
    /// <param name="utcNow">The clock (tests).</param>
    public AlertHistory(string folder, ErrorLog errors, Func<DateTime>? utcNow = null)
    {
        _folder = folder;
        _errors = errors;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Folder => _folder;

    /// <summary>Off: nothing is recorded, in memory or in a file. What the files already hold stays, and can still be
    /// read and cleared.</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>How many days back the day files are kept (1 to 365). <see cref="DeleteOldDays"/> uses it.</summary>
    public int KeepDays
    {
        get => Volatile.Read(ref _keepDays);
        set => Volatile.Write(ref _keepDays, Math.Clamp(value, MinKeepDays, MaxKeepDays));
    }

    /// <summary>Raised for every alert recorded, on the thread that recorded it (the log monitor's). A window marshals.</summary>
    public event Action<AlertHistoryEntry>? Added;

    /// <summary>Raised after <see cref="Clear"/> emptied the history, on the background thread.</summary>
    public event Action? Cleared;

    /// <summary>An entry with every field held to its limit: names and keys cut, the matched text made one line and
    /// cut to <see cref="MaxText"/>, the time taken as UTC.</summary>
    public static AlertHistoryEntry Entry(DateTime time, string? character, string? eventKey, string? severity, string? system,
        AlertOutcome outcome, string? source = null, string? text = null) =>
        new(time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc),
            Cut(character, MaxName), Cut(eventKey, MaxKey), Cut(severity, MaxKey), Cut(system, MaxName), outcome,
            Cut(source, MaxName), Cut(OneLine(text), MaxText));

    private static string Cut(string? text, int max)
    {
        text = text?.Trim() ?? "";
        if (text.Length <= max) return text;
        // Not through the middle of a surrogate pair: half of one is not valid text.
        return text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
    }

    /// <summary>Line breaks, tabs and other control characters become spaces, and so do Unicode's line and paragraph
    /// separators (U+2028, U+2029) and the characters that turn the reading direction round (U+202A to U+202E and
    /// U+2066 to U+2069): text another player typed could use those to show a row as something it isn't.</summary>
    private static string? OneLine(string? text) =>
        text == null ? null : string.Create(text.Length, text, (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = source[i];
                bool layout = c == 0x2028 || c == 0x2029 || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069);
                span[i] = char.IsControl(c) || layout ? ' ' : c;
            }
        });

    /// <summary>Records an alert. Any thread; returns at once; never throws.</summary>
    public void Record(AlertHistoryEntry entry)
    {
        try
        {
            if (!_enabled || _disposed) return;
            entry = entry with { Seq = Interlocked.Increment(ref _seq) };
            lock (_lock)
            {
                _ring.Enqueue(entry);
                _unwritten.Add(entry.Seq);
                while (_ring.Count > RingSize) _unwritten.Remove(_ring.Dequeue().Seq);
            }
            if (!Enqueue(entry)) Report("QueueFull", "more alerts at once than could be written; some are kept only until MultiPreview closes", null);
        }
        catch (Exception ex) { Report("Record", "an alert could not be recorded", ex); }
        try { Added?.Invoke(entry); }
        catch (Exception ex) { Report("Listener", "a listener failed", ex); }
    }

    /// <summary>The alerts held in memory, newest first.</summary>
    public IReadOnlyList<AlertHistoryEntry> Recent()
    {
        lock (_lock) return _ring.Reverse().ToList();
    }

    /// <summary>The alerts kept since <paramref name="sinceUtc"/>, newest first, at most <paramref name="max"/>: what
    /// the day files hold, and what is in memory but in no file (a write that failed). It waits for waiting writes
    /// first and reads files, so call it off the UI thread. Never throws.</summary>
    public AlertHistoryPage Read(DateTime sinceUtc, int max)
    {
        var entries = new List<AlertHistoryEntry>();
        int bad = 0;
        bool more = false;
        try
        {
            Flush(TimeSpan.FromSeconds(2));
            lock (_lock) entries.AddRange(_ring.Where(e => _unwritten.Contains(e.Seq) && e.TimeUtc >= sinceUtc));
            int fromFiles = 0;
            foreach (var (path, day) in DayFiles().OrderByDescending(f => f.Day))
            {
                if (day < sinceUtc.Date) break;
                var lines = ReadDay(path, ref bad);                      // oldest first
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (lines[i].TimeUtc < sinceUtc) continue;
                    if (fromFiles >= max) { more = true; break; }
                    entries.Add(lines[i]);
                    fromFiles++;
                }
                if (more) break;
            }
        }
        catch (Exception ex) { Report("Read", "the history could not be read", ex); }
        var newestFirst = entries.OrderByDescending(e => e.TimeUtc).ToList();
        if (newestFirst.Count > max)
        {
            newestFirst.RemoveRange(max, newestFirst.Count - max);
            more = true;
        }
        return new AlertHistoryPage(newestFirst, more, bad);
    }

    /// <summary>Waits until everything recorded so far is written. False when that took longer than
    /// <paramref name="timeout"/> or the history is closed.</summary>
    public bool Flush(TimeSpan timeout) => RunOnWorker(() => { }, timeout);

    /// <summary>Deletes the day files older than <see cref="KeepDays"/>, on the background thread. App calls it once
    /// at startup; a changed keep time never deletes anything while MultiPreview runs, so a half-typed number can't.</summary>
    public void DeleteOldDays() => Enqueue(new Job(() =>
    {
        var cutoff = _utcNow().Date.AddDays(-KeepDays);
        foreach (var (path, day) in DayFiles())
            if (day < cutoff) File.Delete(path);
    }, null));

    /// <summary>Deletes every day file and empties the memory list. False when it didn't finish within
    /// <paramref name="timeout"/>. Call it off the UI thread.</summary>
    public bool Clear(TimeSpan timeout) => RunOnWorker(() =>
    {
        lock (_lock)
        {
            _ring.Clear();
            _unwritten.Clear();
        }
        foreach (var (path, _) in DayFiles()) File.Delete(path);
        try { Cleared?.Invoke(); }
        catch (Exception ex) { Report("Listener", "a listener failed", ex); }
    }, timeout);

    /// <summary>Writes what is waiting (for at most two seconds) and stops the background thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        _worker?.Join(TimeSpan.FromSeconds(2));
    }

    // ── The background thread ──

    private sealed record Job(Action Run, ManualResetEventSlim? Done);

    private bool RunOnWorker(Action action, TimeSpan timeout)
    {
        // The worker sets the event and this side waits on it. It is never disposed: after a wait that timed out the
        // worker would set a disposed event, which throws there. An event nobody waited on with a handle holds none.
        var done = new ManualResetEventSlim();
        if (!Enqueue(new Job(action, done))) return false;
        return done.Wait(timeout);
    }

    private bool Enqueue(object item)
    {
        try
        {
            if (_disposed) return false;
            if (!_queue.TryAdd(item)) return false;
            if (_worker == null)
            {
                lock (_lock)
                {
                    if (_worker == null)
                    {
                        var thread = new Thread(Work) { IsBackground = true, Name = "AlertHistory", Priority = ThreadPriority.BelowNormal };
                        _worker = thread;
                        thread.Start();
                    }
                }
            }
            return true;
        }
        catch (InvalidOperationException) { return false; }   // closed between the check and the add
    }

    private void Work()
    {
        var batch = new List<AlertHistoryEntry>();
        try
        {
            foreach (var first in _queue.GetConsumingEnumerable())
            {
                object? item = first;
                while (item != null)
                {
                    if (item is AlertHistoryEntry entry) batch.Add(entry);
                    else if (item is Job job)
                    {
                        Write(batch);
                        try { job.Run(); }
                        catch (Exception ex) { Report("Job", "a history file could not be deleted", ex); }
                        job.Done?.Set();
                    }
                    if (batch.Count >= 500 || !_queue.TryTake(out item)) item = null;
                }
                Write(batch);
            }
        }
        catch (Exception ex) { Report("Worker", "the writing thread stopped", ex); }
    }

    /// <summary>Appends a batch to its day files, then empties it. An alert that couldn't be written stays in memory.</summary>
    private void Write(List<AlertHistoryEntry> batch)
    {
        if (batch.Count == 0) return;
        foreach (var day in batch.GroupBy(e => e.TimeUtc.Date))
        {
            try
            {
                Append(DayFile(day.Key), string.Concat(day.Select(e => Line(e) + "\n")));
                lock (_lock) foreach (var e in day) _unwritten.Remove(e.Seq);
            }
            catch (DayFullException) { Report("DayFull", "today's history file is full; further alerts of today are kept only until MultiPreview closes", null); }
            catch (Exception ex) { Report("Write", "an alert could not be written to the history file", ex); }
        }
        batch.Clear();
    }

    private sealed class DayFullException : Exception { }

    private string DayFile(DateTime day) =>
        Path.Combine(_folder, FilePrefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileExtension);

    private void Append(string path, string lines)
    {
        // MultiPreview never writes through a link (IDEAS #57).
        if (SettingsHome.IsLink(_folder) || SettingsHome.IsLink(path)) throw new IOException("link");
        Directory.CreateDirectory(_folder);
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (file.Length >= MaxDayBytes) throw new DayFullException();
        if (file.Length > 0)
        {
            // A write that was cut short (a crash, a power cut) left half a line: end it, so it can't join this one.
            file.Seek(-1, SeekOrigin.End);
            if (file.ReadByte() != '\n') file.WriteByte((byte)'\n');
        }
        file.Seek(0, SeekOrigin.End);
        file.Write(Utf8.GetBytes(lines));
    }

    // ── The day files ──

    /// <summary>Only files named exactly alerts-yyyy-MM-dd.jsonl count: nothing else in the folder is read or deleted.</summary>
    private IEnumerable<(string Path, DateTime Day)> DayFiles()
    {
        if (!Directory.Exists(_folder) || SettingsHome.IsLink(_folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(_folder, FilePrefix + "*" + FileExtension, SearchOption.TopDirectoryOnly))
        {
            var name = DayFileName.Match(Path.GetFileName(path));
            if (name.Success && !SettingsHome.IsLink(path)
                && DateTime.TryParseExact(name.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                yield return (path, day);
        }
    }

    /// <summary>A day file's alerts, oldest first. Lines that can't be read are counted and skipped. Of a file larger
    /// than MultiPreview writes one, only the end is read.</summary>
    private static List<AlertHistoryEntry> ReadDay(string path, ref int bad)
    {
        var entries = new List<AlertHistoryEntry>();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long limit = MaxDayBytes + (1024 * 1024);
        bool tail = file.Length > limit;
        if (tail) file.Seek(-limit, SeekOrigin.End);
        using var reader = new StreamReader(file, Utf8);
        if (tail) reader.ReadLine();                                    // the rest of a line the seek landed in
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            if (Parse(line) is { } entry) entries.Add(entry);
            else bad++;
        }
        return entries;
    }

    private sealed class Stored
    {
        [JsonPropertyName("t")] public DateTime T { get; set; }
        [JsonPropertyName("c")] public string? C { get; set; }
        [JsonPropertyName("e")] public string? E { get; set; }
        [JsonPropertyName("s")] public string? S { get; set; }
        [JsonPropertyName("y")] public string? Y { get; set; }
        [JsonPropertyName("o")] public string? O { get; set; }
        [JsonPropertyName("r")] public string? R { get; set; }
        [JsonPropertyName("x")] public string? X { get; set; }
    }

    /// <summary>One alert as a line of a day file: t time (UTC), c character, e event, s severity, y system, o outcome,
    /// r rule name, x matched text. Empty fields are left out.</summary>
    public static string Line(AlertHistoryEntry e) => JsonSerializer.Serialize(new Stored
    {
        T = e.TimeUtc, C = e.Character, E = e.EventKey, S = e.Severity,
        Y = e.SolarSystem.Length > 0 ? e.SolarSystem : null,
        O = OutcomeText(e.Outcome),
        R = e.Source.Length > 0 ? e.Source : null,
        X = e.Text.Length > 0 ? e.Text : null,
    }, Json);

    /// <summary>The alert a line holds, or null when the line is not one MultiPreview wrote: not JSON, no time,
    /// character or event, or a field longer than MultiPreview writes it.</summary>
    public static AlertHistoryEntry? Parse(string line)
    {
        if (line.Length > MaxLineChars) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(line, Json);
            if (s == null || s.T == default || string.IsNullOrWhiteSpace(s.C) || string.IsNullOrWhiteSpace(s.E)) return null;
            if (s.C.Length > MaxName || s.E.Length > MaxKey || (s.S?.Length ?? 0) > MaxKey || (s.Y?.Length ?? 0) > MaxName
                || (s.R?.Length ?? 0) > MaxName || (s.X?.Length ?? 0) > MaxText) return null;
            return Entry(s.T, s.C, s.E, s.S, s.Y, OutcomeOf(s.O), s.R, s.X);
        }
        catch (Exception) { return null; }
    }

    public static string OutcomeText(AlertOutcome outcome) => outcome switch
    {
        AlertOutcome.NoToast => "notoast",
        AlertOutcome.Muted => "muted",
        AlertOutcome.NoWindow => "nowindow",
        _ => "shown",
    };

    private static AlertOutcome OutcomeOf(string? text) => text switch
    {
        "notoast" => AlertOutcome.NoToast,
        "muted" => AlertOutcome.Muted,
        "nowindow" => AlertOutcome.NoWindow,
        _ => AlertOutcome.Shown,
    };

    /// <summary>Once per kind and session: a fixed sentence and the exception's type. Never its message (it can hold a
    /// path) and never an alert's content.</summary>
    private void Report(string kind, string what, Exception? ex)
    {
        try
        {
            lock (_reported) if (!_reported.Add(kind)) return;
            _errors.Record(new IOException($"Alert history: {what}" + (ex == null ? "" : $" ({ex.GetType().Name})")), "AlertHistory", DateTime.Now);
        }
        catch { /* reporting must never throw into the alert path */ }
    }
}
