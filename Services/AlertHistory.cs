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
/// matched, cut to <see cref="AlertHistory.MaxText"/> characters; it is always "" for a built-in alert read from a
/// log line, so the history holds no log text of those. For "Left behind", which MultiPreview works out itself, it
/// is where the others went ("Jita → Perimeter 3/4"). <paramref name="SoundHeld"/> is true when do-not-disturb held the
/// alert's sound (P81): the gate's answer at the moment the alert was dispatched, false for an alert that was not
/// dispatched to a sound at all and for every entry an older file holds.</summary>
public sealed record AlertHistoryEntry(DateTime TimeUtc, string Character, string EventKey, string Severity, string SolarSystem,
    AlertOutcome Outcome, string Source, string Text, bool SoundHeld = false)
{
    /// <summary>The order alerts were recorded in this session. Not saved.</summary>
    internal long Seq { get; init; }
}

/// <summary>What <see cref="AlertHistory.Read"/> found: the alerts, newest first; whether there were more than asked
/// for; how many lines of the day files could not be read (a line cut short by a crash, or damage); and how many day
/// files could not be opened at all (another program holds one, or access is refused).</summary>
public sealed record AlertHistoryPage(IReadOnlyList<AlertHistoryEntry> Entries, bool More, int BadLines, int UnreadableDays = 0);

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
/// failure is reported once per kind through the error log, without a path or any alert's content. What Record itself
/// has to report is handed to the thread pool: the error log is a file, and the reading thread writes none.</summary>
public sealed class AlertHistory : IDisposable
{
    public const int RingSize = 2000;
    public const int DefaultKeepDays = 30, MinKeepDays = 1, MaxKeepDays = 365;
    /// <summary>One day's file stops growing here (about 100,000 alerts). Later alerts of that day stay in memory only.</summary>
    public const int MaxDayBytes = 16 * 1024 * 1024;
    /// <summary>Alerts waiting to be written. More than this at once are kept in memory only.</summary>
    public const int QueueLimit = 10000;
    public const int MaxName = 100, MaxKey = 64, MaxText = 160;
    /// <summary>A longer line in a day file was not written by MultiPreview: it is skipped. The longest line it writes
    /// has every field at its limit in letters JSON spells out as six characters each: about 3,600.</summary>
    public const int MaxLineChars = 4096;
    /// <summary>A write that failed because the file could not be opened or written (a backup or a sync tool had it
    /// for a moment) is tried this many more times, <see cref="WriteRetryMs"/> apart, before it is given up.</summary>
    public const int WriteRetries = 3, WriteRetryMs = 100;
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
    private readonly ManualResetEventSlim _closing = new();     // set by Dispose; never disposed (the worker may still wait on it)
    private readonly Func<int, bool> _pause;
    private readonly Action<string>? _beforeReadingDay;
    // What Read has learned of the newest day files (IDEAS P70). Its own lock, not _lock: Record takes _lock on the log
    // monitor's thread and must never wait for a file read.
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, DayCache> _days = new(StringComparer.OrdinalIgnoreCase);
    private long _linesParsed;
    private Thread? _worker;
    private long _seq;
    private volatile bool _enabled = true;
    private volatile bool _disposed;
    private int _keepDays = DefaultKeepDays;

    /// <param name="folder">The History folder (AppPaths.ConfigDir\History). Created at the first write.</param>
    /// <param name="errors">Where a failure is reported, once per kind.</param>
    /// <param name="utcNow">The clock (tests).</param>
    /// <param name="pause">The wait between two tries of a write, in milliseconds, on the background thread: true
    /// when the time passed, false when the wait was cut short and the write is to be given up. Null: a real wait,
    /// which ends at once when the history is closed. Tests hand one in, so none of them sleeps.</param>
    /// <param name="beforeReadingDay">Called with a day file's path after the days were listed and before that file is
    /// opened (tests: delete it there, as a retention pass could). Null: nothing.</param>
    public AlertHistory(string folder, ErrorLog errors, Func<DateTime>? utcNow = null, Func<int, bool>? pause = null, Action<string>? beforeReadingDay = null)
    {
        _beforeReadingDay = beforeReadingDay;
        _folder = folder;
        _errors = errors;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _pause = pause ?? (ms => !_closing.Wait(ms));
    }

    public string Folder => _folder;

    /// <summary>How many day-file lines <see cref="Read"/> has handed to the parser since this history was made: a
    /// second read of a file that did not change adds none, a read after an append adds the new lines (tests).</summary>
    public long LinesParsed => Interlocked.Read(ref _linesParsed);

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

    /// <summary>An entry with every field held to its limit, the time taken as UTC. Every text field goes through
    /// <see cref="PlainText.OneLine"/>: one line, no control or format characters (so none that turn the reading
    /// direction round or hide in a name), no half characters, cut to its limit. A character's name, a system and a
    /// rule's name come from logs and files as much as the matched text does.</summary>
    public static AlertHistoryEntry Entry(DateTime time, string? character, string? eventKey, string? severity, string? system,
        AlertOutcome outcome, string? source = null, string? text = null, bool soundHeld = false) =>
        new(time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc),
            PlainText.OneLine(character, MaxName), PlainText.OneLine(eventKey, MaxKey), PlainText.OneLine(severity, MaxKey),
            PlainText.OneLine(system, MaxName), outcome, PlainText.OneLine(source, MaxName), PlainText.OneLine(text, MaxText), soundHeld);

    /// <summary>Records an alert. Any thread; returns at once; never throws. On the calling thread (the log monitor's)
    /// it takes one lock, adds to the memory list and adds to the queue: no file is touched there, the error log
    /// included, and a busy or stuck background thread costs it nothing.</summary>
    public void Record(AlertHistoryEntry entry)
    {
        try
        {
            // An event that is off for its character was no alert (IDEAS #31): never kept, whoever hands it in.
            if (!_enabled || _disposed || entry.Outcome == AlertOutcome.Off) return;
            entry = entry with { Seq = Interlocked.Increment(ref _seq) };
            lock (_lock)
            {
                _ring.Enqueue(entry);
                _unwritten.Add(entry.Seq);
                while (_ring.Count > RingSize) _unwritten.Remove(_ring.Dequeue().Seq);
            }
            // Closed between the look above and here (MultiPreview is exiting) is not a full queue.
            if (!Enqueue(entry) && !_disposed)
                Report("QueueFull", "more alerts at once than could be written; some are kept only until MultiPreview closes", null, later: true);
        }
        catch (Exception ex) { Report("Record", "an alert could not be recorded", ex, later: true); }
        try { Added?.Invoke(entry); }
        catch (Exception ex) { Report("Listener", "a listener failed", ex, later: true); }
    }

    /// <summary>The alerts held in memory, newest first.</summary>
    public IReadOnlyList<AlertHistoryEntry> Recent()
    {
        lock (_lock) return _ring.Reverse().ToList();
    }

    /// <summary>The alerts kept since <paramref name="sinceUtc"/>, newest first, at most <paramref name="max"/> (none
    /// for a <paramref name="max"/> of 0 or less): what the day files hold, and what is in memory but in no file (a
    /// write that failed). It waits for waiting writes first and reads files, so call it off the UI thread. Never
    /// throws. A day file that can't be read is left out and the other days are still listed; the page counts such
    /// days (<see cref="AlertHistoryPage.UnreadableDays"/>, at every read) and the error log says so once per session. A day
    /// file deleted between the listing and the opening is not such a day: it is gone, and nothing is counted or logged.</summary>
    public AlertHistoryPage Read(DateTime sinceUtc, int max)
    {
        max = Math.Max(0, max);
        var entries = new List<AlertHistoryEntry>();
        var waiting = new List<AlertHistoryEntry>();
        int bad = 0, unreadableDays = 0;
        bool more = false;
        try
        {
            Flush(TimeSpan.FromSeconds(2));
            lock (_lock) waiting.AddRange(_ring.Where(e => _unwritten.Contains(e.Seq) && e.TimeUtc >= sinceUtc));
            int fromFiles = 0;
            foreach (var (path, day) in DayFiles().OrderByDescending(f => f.Day))
            {
                if (day < sinceUtc.Date) break;
                List<AlertHistoryEntry> lines;
                try { _beforeReadingDay?.Invoke(path); lines = ReadDay(path, day, ref bad); }   // oldest first
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;                                            // deleted since it was listed (a retention pass): nothing to read, nothing lost, not "unreadable"
                }
                catch (Exception ex)
                {
                    // A day file that can't be read (another program has it and shares nothing, or access is
                    // refused) costs its own day only: the older days are read all the same.
                    Report("Read", "the history could not be read", ex);
                    unreadableDays++;
                    continue;
                }
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
        if (waiting.Count > 0)
        {
            // The memory list was looked at before the files were read. An alert the background thread got into its
            // file between the two is in both: it is listed once. (A file's alert has no place in this session's order.)
            var inFiles = new HashSet<AlertHistoryEntry>(entries);
            entries.AddRange(waiting.Where(e => !inFiles.Contains(e with { Seq = 0 })));
        }
        var newestFirst = entries.OrderByDescending(e => e.TimeUtc).ToList();
        if (newestFirst.Count > max)
        {
            newestFirst.RemoveRange(max, newestFirst.Count - max);
            more = true;
        }
        return new AlertHistoryPage(newestFirst, more, bad, unreadableDays);
    }

    /// <summary>Waits until everything recorded so far is written. False when that took longer than
    /// <paramref name="timeout"/> or the history is closed.</summary>
    public bool Flush(TimeSpan timeout) => RunOnWorker(() => { }, timeout);

    /// <summary>Deletes the day files older than <see cref="KeepDays"/>, on the background thread. App calls it once
    /// at startup; a changed keep time never deletes anything while MultiPreview runs, so a half-typed number can't.</summary>
    public void DeleteOldDays() => Enqueue(new Job(() =>
    {
        var cutoff = _utcNow().Date.AddDays(-KeepDays);
        DeleteDays(day => day < cutoff);
    }, null));

    /// <summary>Deletes every day file and empties the memory list. False when a day file could not be deleted (it is
    /// open in another program, or read-only): the others are gone, that one stays and is still listed. False too
    /// when it didn't finish within <paramref name="timeout"/>. Call it off the UI thread.</summary>
    public bool Clear(TimeSpan timeout)
    {
        bool allGone = false;
        bool finished = RunOnWorker(() =>
        {
            lock (_lock)
            {
                _ring.Clear();
                _unwritten.Clear();
            }
            allGone = DeleteDays(_ => true);
            lock (_cacheLock) _days.Clear();
            // Whatever could be deleted: the list changed.
            try { Cleared?.Invoke(); }
            catch (Exception ex) { Report("Listener", "a listener failed", ex); }
        }, timeout);
        return finished && allGone;
    }

    /// <summary>Deletes the day files <paramref name="which"/> picks, on the background thread. One that can't be
    /// deleted doesn't keep the others: it is reported (once per session) and left. False when any stayed.</summary>
    private bool DeleteDays(Func<DateTime, bool> which)
    {
        bool allGone = true;
        try
        {
            foreach (var (path, day) in DayFiles().ToList())
            {
                if (!which(day)) continue;
                try { File.Delete(path); }
                catch (Exception ex)
                {
                    allGone = false;
                    Report("Job", "a history file could not be deleted", ex);
                }
                lock (_cacheLock) _days.Remove(path);            // deleted or not: what was read of it is not trusted again
            }
        }
        catch (Exception ex)                                    // the folder itself could not be listed
        {
            allGone = false;
            Report("Job", "a history file could not be deleted", ex);
        }
        return allGone;
    }

    /// <summary>Lets go of everything the reads of day files left behind (the parsed days of IDEAS P70). The history window
    /// calls it when it closes, so a closed window leaves no parsed days in memory for the rest of the session; the next
    /// read parses its days again. Any thread.</summary>
    public void ForgetReads()
    {
        lock (_cacheLock) _days.Clear();
    }

    /// <summary>Writes what is waiting (for at most two seconds) and stops the background thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing.Set();                                         // a wait between two tries of a write ends now
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        _worker?.Join(TimeSpan.FromSeconds(2));
        lock (_cacheLock) _days.Clear();
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
                        // Normal priority: it takes the lock Record takes, and a thread held back while it has
                        // that lock would hold the log monitor back with it.
                        var thread = new Thread(Work) { IsBackground = true, Name = "AlertHistory" };
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
                AppendPatiently(DayFile(day.Key), string.Concat(day.Select(e => Line(e) + "\n")));
                lock (_lock) foreach (var e in day) _unwritten.Remove(e.Seq);
            }
            catch (DayFullException) { Report("DayFull", "today's history file is full; further alerts of today are kept only until MultiPreview closes", null); }
            catch (Exception ex) { Report("Write", "an alert could not be written to the history file", ex); }
        }
        batch.Clear();
    }

    /// <summary>Appends, and on an IOException (the file is open in another program for a moment: a backup, a sync
    /// tool, a virus scanner) waits and tries again, <see cref="WriteRetries"/> more times at most. Only that kind of
    /// failure passes by itself: a full day file, a link or a refused access is not tried again. On the background
    /// thread only, so a wait here costs the log monitor nothing. The last failure goes to the caller, which reports it.</summary>
    private void AppendPatiently(string path, string lines)
    {
        for (int triesLeft = WriteRetries; ; triesLeft--)
        {
            try
            {
                Append(path, lines);
                return;
            }
            catch (IOException) when (triesLeft > 0) { }
            // Waited here, behind the catch, so the file is closed again while it waits. A wait that was cut short
            // (MultiPreview is closing) leaves one last try, at once.
            if (!_pause(WriteRetryMs)) triesLeft = 1;
        }
    }

    private sealed class DayFullException : Exception { }

    /// <summary>The History folder or the day file is a link. Not an IOException: waiting does not make it a file.</summary>
    private sealed class LinkException : Exception { }

    private string DayFile(DateTime day) =>
        Path.Combine(_folder, FilePrefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileExtension);

    private void Append(string path, string lines)
    {
        // MultiPreview never writes through a link (IDEAS #57).
        if (SettingsHome.IsLink(_folder) || SettingsHome.IsLink(path)) throw new LinkException();
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

    private const int MaxCachedDays = 3, FingerprintBytes = 64, ReadChunk = 64 * 1024;

    /// <summary>What a read of one day file left behind, so the next read takes only what was added (IDEAS P70).
    /// <see cref="GoodOffset"/> is just past the last complete line: an unfinished line at the end of the file is not
    /// parsed until its newline is there, and is read again from <see cref="GoodOffset"/> every time. The file is
    /// trusted to be the same file only while its creation time, the first bytes of it and the bytes just before
    /// <see cref="GoodOffset"/> are what they were (a day file is only ever appended to), and it did not shrink.</summary>
    private sealed class DayCache
    {
        public DateTime Day;
        public long Length, GoodOffset;
        public DateTime WriteUtc, CreatedUtc;
        public bool Tailed;                                 // the file was larger than MultiPreview writes one: only its end was read
        public byte[] Head = Array.Empty<byte>(), Back = Array.Empty<byte>();
        public List<AlertHistoryEntry> Entries = new();
        public int Bad;
    }

    /// <summary>A day file's alerts, oldest first, as a copy the caller may keep. Lines that can't be read are counted
    /// (<paramref name="bad"/> gets every bad line of the file at every read, cached ones too) and skipped. A file
    /// already read is not read again: if it only grew, the new bytes are read from where the last complete line ended;
    /// if it shrank, or is not the file that was read (see <see cref="DayCache"/>), it is read from its start. Of a file
    /// larger than MultiPreview writes one, only the end is read, and again in full whenever it changes. Any thread:
    /// one read of a file at a time.</summary>
    private List<AlertHistoryEntry> ReadDay(string path, DateTime day, ref int bad)
    {
        lock (_cacheLock)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = file.Length;
                long limit = MaxDayBytes + (1024 * 1024);
                bool tail = length > limit;
                _days.TryGetValue(path, out var cache);
                if (cache != null && !SameFile(cache, file, length)) cache = null;
                if (cache == null || length != cache.Length)
                {
                    bool append = cache != null && !tail;
                    long from = append ? cache!.GoodOffset : tail ? length - limit : 0;
                    var fresh = new List<AlertHistoryEntry>();
                    int freshBad = 0;
                    var (good, end) = ScanLines(file, from, tail, fresh, ref freshBad);   // nothing is kept until it all read
                    if (!append) cache = new DayCache { Day = day, Tailed = tail };
                    cache!.Entries.AddRange(fresh);
                    cache.Bad += freshBad;
                    cache.GoodOffset = good;
                    cache.Length = end;
                    cache.WriteUtc = File.GetLastWriteTimeUtc(file.SafeFileHandle);
                    cache.CreatedUtc = File.GetCreationTimeUtc(file.SafeFileHandle);
                    int fingerprint = tail ? 0 : (int)Math.Min(FingerprintBytes, good);
                    cache.Head = ReadAt(file, 0, fingerprint);
                    cache.Back = ReadAt(file, good - fingerprint, fingerprint);
                    _days[path] = cache;
                    ForgetOldDays();
                }
                bad += cache.Bad;
                return cache.Entries.ToList();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                _days.Remove(path);
                throw;
            }
        }
    }

    /// <summary>True while what was read of the file still describes it: it did not shrink, it was created when the
    /// cache says, and the bytes that decide whether it was only appended to are the ones that were read (its first
    /// bytes and the bytes just before where the last read stopped). A file as long as it was must also have the write
    /// time it had. A file that was read in its tail only is trusted just while it is exactly as it was.</summary>
    private static bool SameFile(DayCache cache, FileStream file, long length)
    {
        if (length < cache.Length) return false;
        if (File.GetCreationTimeUtc(file.SafeFileHandle) != cache.CreatedUtc) return false;
        bool sameLength = length == cache.Length;
        if (sameLength && File.GetLastWriteTimeUtc(file.SafeFileHandle) != cache.WriteUtc) return false;
        if (cache.Tailed) return sameLength;
        return ReadAt(file, 0, cache.Head.Length).AsSpan().SequenceEqual(cache.Head)
            && ReadAt(file, cache.GoodOffset - cache.Back.Length, cache.Back.Length).AsSpan().SequenceEqual(cache.Back);
    }

    private static byte[] ReadAt(FileStream file, long offset, int count)
    {
        var bytes = new byte[count];
        if (count == 0) return bytes;
        file.Seek(offset, SeekOrigin.Begin);
        int got = file.ReadAtLeast(bytes, count, throwOnEndOfStream: false);
        return got == count ? bytes : bytes[..got];
    }

    /// <summary>Reads from <paramref name="from"/> to the end of the file, line by line (split at 0x0A in the bytes,
    /// then each line decoded on its own, so a character cut by the end of a chunk or of the file is never decoded in
    /// halves). Returns the offset just past the last newline and the offset reached. What follows the last newline
    /// is not parsed: the writer may still be writing it. <paramref name="skipFirst"/>: the first line is the rest of
    /// one the start landed in.</summary>
    private (long Good, long End) ScanLines(FileStream file, long from, bool skipFirst, List<AlertHistoryEntry> into, ref int bad)
    {
        file.Seek(from, SeekOrigin.Begin);
        var chunk = new byte[ReadChunk];
        var pending = new MemoryStream();                   // the start of a line a chunk ended in
        long chunkAt = from, lineAt = from, good = from;
        int got;
        while ((got = file.Read(chunk, 0, chunk.Length)) > 0)
        {
            int at = 0;
            while (at < got)
            {
                int newline = chunk.AsSpan(at, got - at).IndexOf((byte)'\n');
                if (newline < 0)
                {
                    pending.Write(chunk, at, got - at);
                    break;
                }
                ReadOnlySpan<byte> line;
                if (pending.Length == 0) line = chunk.AsSpan(at, newline);
                else
                {
                    pending.Write(chunk, at, newline);
                    line = pending.GetBuffer().AsSpan(0, (int)pending.Length);
                }
                if (skipFirst) skipFirst = false;
                else TakeLine(line, lineAt == 0, into, ref bad);
                pending.SetLength(0);
                at += newline + 1;
                lineAt = good = chunkAt + at;
            }
            chunkAt += got;
        }
        return (good, chunkAt);
    }

    private void TakeLine(ReadOnlySpan<byte> line, bool atFileStart, List<AlertHistoryEntry> into, ref int bad)
    {
        if (atFileStart && line.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) line = line[3..];    // a byte order mark another program wrote
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (line.Length == 0) return;
        Interlocked.Increment(ref _linesParsed);
        if (Parse(Utf8.GetString(line)) is { } entry) into.Add(entry);
        else bad++;
    }

    /// <summary>At most <see cref="MaxCachedDays"/> days stay cached: the newest. Under <see cref="_cacheLock"/>.</summary>
    private void ForgetOldDays()
    {
        if (_days.Count <= MaxCachedDays) return;
        foreach (var path in _days.OrderByDescending(d => d.Value.Day).Skip(MaxCachedDays).Select(d => d.Key).ToList())
            _days.Remove(path);
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
        /// <summary>Sound held by do-not-disturb. Written only when true; a line without it (every line an older
        /// MultiPreview wrote) reads as false.</summary>
        [JsonPropertyName("h")] public bool? H { get; set; }
    }

    /// <summary>One alert as a line of a day file: t time (UTC), c character, e event, s severity, y system, o outcome,
    /// r rule name, x matched text, h sound held (true only). Empty fields are left out.</summary>
    public static string Line(AlertHistoryEntry e) => JsonSerializer.Serialize(new Stored
    {
        T = e.TimeUtc, C = e.Character, E = e.EventKey, S = e.Severity,
        Y = e.SolarSystem.Length > 0 ? e.SolarSystem : null,
        O = OutcomeText(e.Outcome),
        R = e.Source.Length > 0 ? e.Source : null,
        X = e.Text.Length > 0 ? e.Text : null,
        H = e.SoundHeld ? true : null,
    }, Json);

    /// <summary>The alert a line holds, or null when the line is not one MultiPreview wrote: not JSON, no time,
    /// character or event (a name of nothing but characters that don't show is none), or a field longer than
    /// MultiPreview writes it.</summary>
    public static AlertHistoryEntry? Parse(string line)
    {
        if (line.Length > MaxLineChars) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(line, Json);
            if (s == null || s.T == default || string.IsNullOrWhiteSpace(s.C) || string.IsNullOrWhiteSpace(s.E)) return null;
            if (s.C.Length > MaxName || s.E.Length > MaxKey || (s.S?.Length ?? 0) > MaxKey || (s.Y?.Length ?? 0) > MaxName
                || (s.R?.Length ?? 0) > MaxName || (s.X?.Length ?? 0) > MaxText) return null;
            var entry = Entry(s.T, s.C, s.E, s.S, s.Y, OutcomeOf(s.O), s.R, s.X, s.H == true);
            return entry.Character.Length == 0 || entry.EventKey.Length == 0 ? null : entry;
        }
        catch (Exception) { return null; }
    }

    public static string OutcomeText(AlertOutcome outcome) => outcome switch
    {
        AlertOutcome.NoToast => "notoast",
        AlertOutcome.Muted => "muted",
        AlertOutcome.NoWindow => "nowindow",
        AlertOutcome.Snoozed => "snoozed",
        _ => "shown",
    };

    private static AlertOutcome OutcomeOf(string? text) => text switch
    {
        "notoast" => AlertOutcome.NoToast,
        "muted" => AlertOutcome.Muted,
        "nowindow" => AlertOutcome.NoWindow,
        "snoozed" => AlertOutcome.Snoozed,
        _ => AlertOutcome.Shown,
    };

    /// <summary>Once per kind and session: a fixed sentence and the exception's type. Never its message (it can hold a
    /// path) and never an alert's content. <paramref name="later"/> is for <see cref="Record"/>, which runs on the
    /// log monitor's thread: the error log is a file, so its entry is written from the thread pool instead.</summary>
    private void Report(string kind, string what, Exception? ex, bool later = false)
    {
        try
        {
            lock (_reported) if (!_reported.Add(kind)) return;
            var error = new IOException($"Alert history: {what}" + (ex == null ? "" : $" ({ex.GetType().Name})"));
            if (later) ThreadPool.QueueUserWorkItem(_ => WriteReport(error));
            else WriteReport(error);
        }
        catch { /* reporting must never throw into the alert path */ }
    }

    private void WriteReport(Exception error)
    {
        try { _errors.Record(error, "AlertHistory", DateTime.Now); }
        catch { /* reporting must never throw */ }
    }
}
