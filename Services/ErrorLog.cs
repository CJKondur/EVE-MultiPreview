using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>Errors MultiPreview handles and keeps running after (IDEAS #55), in Logs\error_log.txt. Every one is
/// counted. The first of a kind (type and message) in a session is written with its stack, later ones are only
/// counted, so an error that repeats can't grow the file without end. Notes (a move, a hook put back) are written but
/// not counted. Thread-safe, and never throws: it runs inside the app's last-chance error handlers.</summary>
public sealed class ErrorLog
{
    /// <summary>The app's log, in the settings folder's Logs folder.</summary>
    public static ErrorLog Shared { get; } = new(() => AppPaths.ErrorLogPath);

    private static readonly string Version =
        typeof(ErrorLog).Assembly.GetName().Version?.ToString(3) ?? "?";

    private readonly Func<string> _path;
    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new();
    private bool _sessionStarted;
    private int _written;
    /// <summary>Entries written per session. Distinct messages (a path or an id in the text) would otherwise defeat the
    /// once-per-kind rule and grow the file without end.</summary>
    private const int MaxWritten = 100;
    private const int MaxMessage = 300, MaxStack = 2000;
    private int _count;

    public ErrorLog(Func<string> path) => _path = path;

    /// <summary>Errors handled since MultiPreview started.</summary>
    public int Count => Volatile.Read(ref _count);

    public string FilePath => _path();

    /// <summary>Raised after every Record, on the thread that recorded. A handler must not throw or record an error, or
    /// a failing handler would feed itself.</summary>
    public event Action? Changed;

    public void Record(Exception ex, string source, DateTime now)
    {
        lock (_lock)
        {
            _count++;
            if (_written < MaxWritten && _seen.Add(ex.GetType().FullName + "|" + ex.Message))
            {
                _written++;
                Append(now, $"{source}: {ex.GetType().Name}: {Cap(ex.Message, MaxMessage)}\n  Stack: {Cap(ex.StackTrace, MaxStack)}");
                if (_written == MaxWritten)
                    Append(now, $"{MaxWritten} errors written this session; further errors are counted but not written");
            }
        }
        try { Changed?.Invoke(); } catch { }
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
            var path = _path();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var header = _sessionStarted ? "" : $"[{now:yyyy-MM-dd HH:mm:ss}] ===== EVE MultiPreview v{Version} — session =====\n";
            File.AppendAllText(path, header + $"[{now:yyyy-MM-dd HH:mm:ss}] {text}\n");
            _sessionStarted = true;
        }
        catch { /* the error log must never throw */ }
    }
}
