using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading;

namespace EveMultiPreview.Services;

/// <summary>The stall recorder (players' "lag and lock-ups in fights"): a watchdog thread that writes Logs/debug_stalls.log
/// whenever something stops keeping up, so a bug report says what froze, for how long and what was running. It writes
/// nothing while all is well. Shared by both apps; each hands it its own UI-thread post and, on Windows, the client probe.
/// What it records:
/// - the UI thread not answering a ping for <see cref="UiStallMs"/> or more, with the work that was running then (the
///   <see cref="Op"/> marker), MultiPreview's CPU time and the garbage collections during the block (busy, or waiting
///   on something?), and once more while a block passes <see cref="LongStallMs"/> (a hang the player ends by killing us);
/// - the watchdog itself waking <see cref="LateWakeMs"/> late: the whole process (a GC pause) or the machine was held up;
/// - the keyboard and mouse hooks getting an event <see cref="HookLagMs"/> or more after it happened (Windows), which is
///   input lag for every program;
/// - an EVE client that Windows calls hung (Windows: IsHungAppWindow, no messages handled for 5 s; nothing is sent to it).
/// A session header lists the version, the machine and the settings that cost the most.</summary>
public sealed class StallWatch : IDisposable
{
    public const int PollMs = 100, UiStallMs = 250, LongStallMs = 5000, LateWakeMs = 250, HookLagMs = 200;
    public const int ClientCheckMs = 1000;
    /// <summary>Lines written in one minute at most; the rest are counted and the count written once the minute is over.</summary>
    public const int MaxLinesPerMinute = 120;
    public const string LogName = "debug_stalls.log", OldLogName = "debug_stalls_old.log";
    public const long MaxLogBytes = 2_000_000;

    // ── what the UI thread is doing (Op) ──
    private static int _uiThreadId = -1;
    private static string? _op;
    private static long _opSince;

    /// <summary>Marks <paramref name="name"/> as the UI thread's work until the scope ends (nested scopes put the outer
    /// one back). Free off the UI thread and before a watch starts: it then does nothing.</summary>
    public static OpScope Op(string name)
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _uiThreadId)) return default;
        var scope = new OpScope(Volatile.Read(ref _op), Volatile.Read(ref _opSince));
        Volatile.Write(ref _opSince, Environment.TickCount64);
        Volatile.Write(ref _op, name);
        return scope;
    }

    public readonly struct OpScope : IDisposable
    {
        private readonly string? _prev;
        private readonly long _prevSince;
        private readonly bool _on;
        internal OpScope(string? prev, long prevSince) { _prev = prev; _prevSince = prevSince; _on = true; }
        public void Dispose()
        {
            if (!_on) return;
            Volatile.Write(ref _opSince, _prevSince);
            Volatile.Write(ref _op, _prev);
        }
    }

    // ── hook lag (Windows hooks call HookLag) ──
    private static int _keyLagMax, _mouseLagMax;

    /// <summary>A low-level hook got an input event: <paramref name="eventTime"/> is the event's own time stamp (the hook
    /// struct's time, GetTickCount's clock). Only a running maximum is kept, so this costs a subtraction.</summary>
    public static void HookLag(uint eventTime, bool mouse)
    {
        int lag = unchecked((int)((uint)Environment.TickCount - eventTime));
        if (lag < HookLagMs || lag > 60_000) return;   // on time, or a time stamp that is no clock reading
        ref int max = ref mouse ? ref _mouseLagMax : ref _keyLagMax;
        int seen;
        while (lag > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, lag, seen) != seen) { }
    }

    /// <summary>The settings that cost the most while fighting, for the session header: what to look at first. Only the
    /// ones both apps have; the Windows app adds its own (<see cref="WindowsSettingsSummary"/>), which the Linux app must
    /// not read (LinuxSupportTests: a setting hidden on Linux has no reader there).</summary>
    public static string SettingsSummary(EveMultiPreview.Models.AppSettings s)
    {
        var debug = new List<string>();
        if (s.EnableDebugLogging_Alerts) debug.Add("alerts");
        if (s.EnableDebugLogging_Cycling) debug.Add("cycling");
        if (s.EnableDebugLogging_WindowHooks) debug.Add("window hooks");
        return $"settings: keep thumbnails above clients {On(s.KeepThumbnailsAboveClients)}, static thumbnails {On(s.StaticThumbnails)}, "
            + $"suspend when background {On(s.SuspendThumbnailsWhenBackground)}, process stats {On(s.ShowProcessStats)}, "
            + $"under-fire indicator {On(s.EnableUnderFireIndicator)}, debug logs {(debug.Count == 0 ? "off" : string.Join("+", debug))}";
    }

    /// <summary>The Windows app's own costly settings, after <see cref="SettingsSummary"/>. Never called by the Linux app.</summary>
    public static string WindowsSettingsSummary(EveMultiPreview.Models.AppSettings s) =>
        $"manage CPU affinity {On(s.ManageAffinity)} (auto-balance {On(s.AutoBalanceCores)}), RTSS FPS {On(s.ShowRtssFps)}, "
        + $"debug logs dwm {On(s.EnableDebugLogging_DWM)}, injection {On(s.EnableDebugLogging_Injection)}";

    private static string On(bool b) => b ? "on" : "off";

    /// <summary>Names the UI thread's work when no <see cref="Op"/> marks it (Windows: the dispatcher operation running);
    /// asked on the watchdog thread only once a block is seen.</summary>
    public static Func<string?>? Unmarked { get; set; }

    /// <summary>A client the probe asks (Windows): its character and window.</summary>
    public readonly record struct ClientRef(string Name, IntPtr Window);

    private readonly Action<Action> _postToUi;
    private readonly Func<long> _now;
    private readonly Action<string> _write;
    private readonly Func<string>? _context;
    private readonly Func<IReadOnlyList<ClientRef>>? _uiClients;
    private readonly Func<IntPtr, bool>? _hung;
    private Thread? _thread, _probeThread;
    private volatile bool _stopped;

    /// <param name="postToUi">Queues work on the UI thread (BeginInvoke / Dispatcher.UIThread.Post).</param>
    /// <param name="write">Writes one line; null writes Logs/debug_stalls.log.</param>
    /// <param name="context">A short line of the state at a stall (clients, alerts); read on the watchdog thread.</param>
    /// <param name="uiClients">The clients to probe, read ON the UI thread (where the client list lives).</param>
    /// <param name="hung">Whether Windows calls a client's window hung (ClientProbe.IsHung): asks Windows, sends the client nothing.</param>
    public StallWatch(Action<Action> postToUi, Action<string>? write = null, Func<string>? context = null,
        Func<IReadOnlyList<ClientRef>>? uiClients = null, Func<IntPtr, bool>? hung = null, Func<long>? now = null)
    {
        _postToUi = postToUi;
        _now = now ?? (() => Environment.TickCount64);
        _write = write ?? WriteToLog;
        _context = context;
        _uiClients = uiClients;
        _hung = hung;
    }

    /// <summary>Starts watching; call it ON the UI thread (it is the thread the pings and the Op markers are for).</summary>
    /// <param name="threads">False for the tests: they call <see cref="Tick"/> and <see cref="ProbeClients"/> themselves.</param>
    public void Start(string header, bool threads = true)
    {
        Volatile.Write(ref _uiThreadId, Environment.CurrentManagedThreadId);
        Line("─── session " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ─── " + header
            + $" | GC: {(GCSettings.IsServerGC ? "server" : "workstation")}, {GCSettings.LatencyMode}");
        if (!threads) return;
        _thread = new Thread(() => Loop(PollMs, Tick)) { IsBackground = true, Name = "StallWatch", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (_hung != null && _uiClients != null)
        {
            _probeThread = new Thread(() => Loop(ClientCheckMs, ProbeClients)) { IsBackground = true, Name = "StallWatch probe" };
            _probeThread.Start();
        }
    }

    private void Loop(int every, Action<long> step)
    {
        while (!_stopped)
        {
            Thread.Sleep(every);
            if (_stopped) return;
            try { step(_now()); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Line("watchdog error: " + ex.GetType().Name + ": " + ex.Message); }
        }
    }

    // ── the watchdog tick: late wake, UI pings, hook lag ──
    private long _lastTickEnd = -1;
    private GcMark _lastTickGc = GcMark.Take();
    private long _pingSent = -1, _answeredAt;   // _answeredAt and _answeredSeq are written on the UI thread
    private int _pingSeq, _answeredSeq;
    private GcMark _pingGc;
    private TimeSpan _pingCpu;
    private bool _stallSeen, _longStallSeen;
    private string? _stallOp;
    private long _stallOpSince;

    /// <summary>One watchdog step at <paramref name="now"/> (milliseconds, the watch's clock). Public for the tests.</summary>
    public void Tick(long now)
    {
        var gc = GcMark.Take();
        if (_lastTickEnd >= 0 && now - _lastTickEnd - PollMs >= LateWakeMs)
            Line($"MultiPreview held up: the watchdog woke {now - _lastTickEnd - PollMs} ms late (the whole process or the machine stalled)"
                + gc.Since(_lastTickGc) + Context());
        _lastTickGc = gc;

        if (_pingSent >= 0 && Volatile.Read(ref _answeredSeq) == _pingSeq)
        {
            long answered = Interlocked.Read(ref _answeredAt);
            if (_stallSeen)
                Line($"UI thread blocked {answered - _pingSent} ms while: {Doing(_stallOp, _stallOpSince, _pingSent)}"
                    + Cpu(_pingCpu, answered - _pingSent) + GcMark.Take().Since(_pingGc) + Context());
            _pingSent = -1;
            _stallSeen = _longStallSeen = false;
        }
        if (_pingSent < 0)
        {
            _pingSent = now;
            _pingGc = GcMark.Take();
            _pingCpu = OwnCpu();
            _stallOp = null;
            int seq = ++_pingSeq;
            _postToUi(() => Answer(seq));
        }
        else if (!_stallSeen && now - _pingSent >= UiStallMs)
        {
            _stallSeen = true;
            _stallOp = Volatile.Read(ref _op);
            _stallOpSince = Volatile.Read(ref _opSince);
            if (_stallOp == null && Unmarked != null)
            {
                try { if (Unmarked() is { Length: > 0 } running) { _stallOp = "dispatcher operation " + running; _stallOpSince = _pingSent; } }
                catch (Exception) { }
            }
        }
        else if (_stallSeen && !_longStallSeen && now - _pingSent >= LongStallMs)
        {
            _longStallSeen = true;
            Line($"UI thread STILL blocked after {now - _pingSent} ms while: {Doing(Volatile.Read(ref _op), Volatile.Read(ref _opSince), _pingSent)}"
                + Cpu(_pingCpu, now - _pingSent) + GcMark.Take().Since(_pingGc) + Context());
        }

        int key = Interlocked.Exchange(ref _keyLagMax, 0), mouse = Interlocked.Exchange(ref _mouseLagMax, 0);
        if (key > 0) Line($"keyboard hook got a keystroke {key} ms late (input lags for every program)" + Context());
        if (mouse > 0) Line($"mouse hook got a mouse event {mouse} ms late (input lags for every program)" + Context());
        _lastTickEnd = _now();
    }

    private int _pings;
    private volatile IReadOnlyList<ClientRef>? _clientsSeen;

    /// <summary>The ping's answer, on the UI thread; every tenth one also takes the clients for the probe.</summary>
    private void Answer(int seq)
    {
        Interlocked.Exchange(ref _answeredAt, _now());
        Volatile.Write(ref _answeredSeq, seq);
        if (_uiClients != null && _pings++ % 10 == 0)
            try { _clientsSeen = _uiClients(); } catch (Exception) { }
    }

    /// <summary>The UI thread's pings without the thread: for the tests, which have no message loop.</summary>
    public void AnswerPing() => Answer(_pingSeq);

    private static string Doing(string? op, long since, long pingSent) =>
        op == null ? "nothing marked (window messages, layout or rendering, or unmarked work)"
            : $"{op} (running since {Math.Max(0, pingSent - since)} ms before the block was seen)";

    private string Context()
    {
        if (_context == null) return "";
        try { return " | " + _context(); } catch (Exception) { return ""; }
    }

    private static TimeSpan OwnCpu()
    {
        try { using var p = Process.GetCurrentProcess(); return p.TotalProcessorTime; } catch (Exception) { return TimeSpan.Zero; }
    }

    /// <summary>MultiPreview's CPU over the block: near the block's length = busy (on one core), near 0 = waiting on
    /// something (another window, a file, a lock).</summary>
    private static string Cpu(TimeSpan since, long blockMs)
    {
        var used = OwnCpu() - since;
        return since == TimeSpan.Zero || used < TimeSpan.Zero ? "" : $" | MultiPreview CPU {used.TotalMilliseconds:0} ms of {blockMs} ms";
    }

    // ── the client probe (Windows): a client Windows calls hung froze ──
    private readonly Dictionary<IntPtr, long> _slowSince = new();

    /// <summary>One probe round at <paramref name="now"/>. Public for the tests.</summary>
    public void ProbeClients(long now)
    {
        var clients = _clientsSeen;
        if (clients == null || _hung == null) return;
        var seen = new HashSet<IntPtr>();
        foreach (var c in clients)
        {
            seen.Add(c.Window);
            bool slow = _hung(c.Window);
            if (slow && !_slowSince.ContainsKey(c.Window))
            {
                _slowSince[c.Window] = now;
                Line($"client '{c.Name}' is not responding (Windows calls it hung: no messages handled for 5 s; the EVE client froze)" + Context());
            }
            else if (!slow && _slowSince.Remove(c.Window, out var since))
                Line($"client '{c.Name}' responding again (hung for about {now - since} ms more after it was seen)");
        }
        foreach (var gone in new List<IntPtr>(_slowSince.Keys))
            if (!seen.Contains(gone)) _slowSince.Remove(gone);
    }

    // ── writing ──
    private readonly object _writeLock = new();
    private long _minute = -1;
    private int _linesThisMinute, _dropped;

    private void Line(string text)
    {
        lock (_writeLock)
        {
            long minute = _now() / 60_000;
            if (minute != _minute)
            {
                if (_dropped > 0) Emit($"({_dropped} more lines in the last minute were left out)");
                _minute = minute;
                _linesThisMinute = _dropped = 0;
            }
            if (++_linesThisMinute > MaxLinesPerMinute) { _dropped++; return; }
            Emit(text);
        }
    }

    private void Emit(string text)
    {
        try { _write("[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + text); }
        catch (Exception) { }
    }

    /// <summary>Appends to Logs/debug_stalls.log (never through a linked Logs folder, AppPaths.LogsWritable); past
    /// <see cref="MaxLogBytes"/> the file becomes debug_stalls_old.log and a new one starts. Both go in bug reports.</summary>
    private static void WriteToLog(string line)
    {
        if (!AppPaths.LogsWritable) return;
        Directory.CreateDirectory(AppPaths.LogsDir);
        var path = Path.Combine(AppPaths.LogsDir, LogName);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxLogBytes) File.Move(path, Path.Combine(AppPaths.LogsDir, OldLogName), overwrite: true);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    public void Dispose()
    {
        _stopped = true;
        Volatile.Write(ref _uiThreadId, -1);
    }

    /// <summary>The collections and the pause time so far, to tell what happened between two marks.</summary>
    private readonly record struct GcMark(int Gen0, int Gen1, int Gen2, TimeSpan Paused)
    {
        public static GcMark Take() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());

        public string Since(GcMark before)
        {
            int g0 = Gen0 - before.Gen0, g1 = Gen1 - before.Gen1, g2 = Gen2 - before.Gen2;
            if (g0 + g1 + g2 == 0) return "";
            return $" | GC: paused {(Paused - before.Paused).TotalMilliseconds:0} ms (gen0 +{g0}, gen1 +{g1}, gen2 +{g2})";
        }
    }
}
