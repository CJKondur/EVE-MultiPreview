using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>Errors MultiPreview handles and keeps running after (IDEAS #55), in Logs\error_log.txt. Every one is
/// counted. The first of a kind (type and message) in a session is written with its stack, later ones are only
/// counted, so an error that repeats can't grow the file without end. Notes (a move, a hook put back) are written but
/// not counted. Thread-safe, and never throws: it runs inside the app's last-chance error handlers. Players attach the
/// file to bug reports, so an exception is written through Describe, which keeps character names out of it.</summary>
public sealed class ErrorLog
{
    /// <summary>The app's log, in the settings folder's Logs folder. Nothing is written while that folder is a link
    /// (AppPaths.LogsWritable); errors are still counted.</summary>
    public static ErrorLog Shared { get; } = new(() => AppPaths.ErrorLogPath, () => AppPaths.LogsWritable);

    private static readonly string Version =
        typeof(ErrorLog).Assembly.GetName().Version?.ToString(3) ?? "?";

    private readonly Func<string> _path;
    private readonly Func<bool>? _writable;
    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new();
    private bool _sessionStarted;
    private int _written;
    /// <summary>Entries written per session. Distinct messages (a path or an id in the text) would otherwise defeat the
    /// once-per-kind rule and grow the file without end.</summary>
    private const int MaxWritten = 100;
    private const int MaxMessage = 300, MaxStack = 2000;
    private int _count;

    /// <param name="path">The log file.</param>
    /// <param name="writable">Asked before every write; false writes nothing. Null: always write.</param>
    public ErrorLog(Func<string> path, Func<bool>? writable = null)
    {
        _path = path;
        _writable = writable;
    }

    /// <summary>Errors handled since MultiPreview started.</summary>
    public int Count => Volatile.Read(ref _count);

    public string FilePath => _path();

    /// <summary>Raised after every Record, on the thread that recorded. A handler must not throw or record an error, or
    /// a failing handler would feed itself.</summary>
    public event Action? Changed;

    public void Record(Exception ex, string source, DateTime now) => Record(ex, source, now, always: false);

    /// <summary>The error that ends the process (App's last-chance handler): counted like any other, and always
    /// written, past the once-per-kind rule and the session's limit. It is the entry a bug report needs.</summary>
    public void RecordCrash(Exception ex, DateTime now) => Record(ex, "Crash", now, always: true);

    private void Record(Exception? ex, string source, DateTime now, bool always)
    {
        try
        {
            lock (_lock)
            {
                _count++;
                var what = Describe(ex);
                if (always || (_written < MaxWritten && _seen.Add(what)))
                {
                    string? stack = null;
                    try { stack = ex?.StackTrace; } catch { }
                    Append(now, $"{source}: {Cap(what, MaxMessage)}\n  Stack: {Cap(stack, MaxStack)}");
                    if (!always && ++_written == MaxWritten)
                        Append(now, $"{MaxWritten} errors written this session; further errors are counted but not written");
                }
            }
        }
        catch { /* the error log must never throw */ }
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>An exception as the log (and a note or a reason that ends up in it) may show it: its type and message.
    /// A JsonException gives its type and the line only (counted from 1, SettingsService.FailedLine), never its message:
    /// that names the JSON path of the value that failed, and in a settings file a path is made of character names.
    /// Not the byte position either: a column in a file this size points at a value. The same path is cut from any other
    /// message the serializer appended it to. Never throws.</summary>
    public static string Describe(Exception? ex)
    {
        if (ex == null) return "(no exception)";
        string type = "Exception";
        try
        {
            type = ex.GetType().Name;
            if (ex is JsonException)
                return $"{type}: line {SettingsService.FailedLine(ex)?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
            var message = ex.Message;
            int path = message.IndexOf(" Path: $", StringComparison.Ordinal);
            return $"{type}: {(path < 0 ? message : message[..path])}";
        }
        catch { return type; }
    }

    /// <summary>A line for whoever reads the log, not counted as an error.</summary>
    public void Note(string line, DateTime now)
    {
        lock (_lock) Append(now, line);
    }

    private static string Cap(string? s, int max) =>
        s == null ? "" : s.Length <= max ? s : s[..max] + "…";

    /// <summary>Under the lock. The first write of a session starts with a header naming the version.</summary>
    private void Append(DateTime now, string text)
    {
        try
        {
            if (_writable?.Invoke() == false) return;
            var path = _path();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var header = _sessionStarted ? "" : $"[{now:yyyy-MM-dd HH:mm:ss}] ===== EVE MultiPreview v{Version} — session =====\n";
            File.AppendAllText(path, header + $"[{now:yyyy-MM-dd HH:mm:ss}] {text}\n");
            _sessionStarted = true;
        }
        catch { /* the error log must never throw */ }
    }
}
