using System;
using System.Globalization;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>Tells an unexpected end from a clean exit (IDEAS #55). The instance that holds the single-instance lock
/// writes a marker into the settings folder when it starts and deletes it when it exits cleanly (or Windows signs out),
/// so a marker found at the next start means that session ended some other way: a crash, Task Manager, a power cut.</summary>
public sealed class RunMarker
{
    public const string FileName = "EVE MultiPreview.running";

    /// <summary>When the last session started, if it ended unexpectedly (App sets it at startup; the Diagnostics page
    /// shows it). Null after a clean exit.</summary>
    public static DateTime? PreviousUncleanStart { get; set; }

    private readonly string _path;
    private bool _written;

    public RunMarker(string dir) => _path = Path.Combine(dir, FileName);

    /// <summary>Called once at startup. Returns when the previous session started if it never ended cleanly, else null,
    /// then marks this session as running. Never throws.</summary>
    public DateTime? Begin(DateTime now)
    {
        DateTime? previous = null;
        try
        {
            if (File.Exists(_path))
                previous = DateTime.TryParse(File.ReadAllText(_path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
                    ? started
                    : File.GetLastWriteTime(_path);
        }
        catch { }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, now.ToString("o", CultureInfo.InvariantCulture));
            _written = true;
        }
        catch { }
        return previous;
    }

    /// <summary>A clean end: deletes the marker this instance wrote. An instance that never began (the second
    /// instance, which only says "already running") deletes nothing. Never throws.</summary>
    public void End()
    {
        if (!_written) return;
        try { File.Delete(_path); } catch { }
        _written = false;
    }
}
