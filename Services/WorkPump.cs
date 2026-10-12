using System;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>Runs one piece of work at a time on the thread pool, never inside <see cref="Wake"/> (IDEAS batch 6):
/// Wake asks for a run and returns, so whoever wakes the pump never waits for the work. (Not "on another thread":
/// the log reader's thread is a pool thread itself, and a run may land on it once it is back in the pool.) The work
/// is a "drain": it takes what is waiting somewhere else (the utterances to speak, the notifications to show) until
/// nothing is left. <see cref="Wake"/> starts it unless it is running; a wake that comes while it runs makes it run
/// once more afterwards, so nothing that arrived during a run is left waiting. The pump holds no queue of its own,
/// so nothing can pile up in it.</summary>
public sealed class WorkPump
{
    private readonly Func<Task> _drain;
    private readonly Action<Exception> _failed;
    private readonly object _lock = new();
    private bool _running, _again, _stopped;
    private TaskCompletionSource _idle = Done();

    /// <param name="drain">The work. What it throws is reported and costs that run; the next wake runs it again.</param>
    /// <param name="failed">Is told that the work threw, and the TYPE of what it threw (with the type at its root,
    /// its error code and its stack: AlertWays.Failure), never its message: the work speaks and shows characters'
    /// names, so a message can hold one, App writes what it is told to error_log.txt, and players post that file.</param>
    public WorkPump(Func<Task> drain, Action<Exception>? failed = null)
    {
        _drain = drain;
        _failed = failed ?? (_ => { });
    }

    private static TaskCompletionSource Done()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>There may be work: run the drain, unless it is running (then: once more when it has finished).
    /// Returns at once, on any thread.</summary>
    public void Wake()
    {
        lock (_lock)
        {
            if (_stopped) return;
            if (_running)
            {
                _again = true;
                return;
            }
            _running = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = Task.Run(Run);
    }

    private async Task Run()
    {
        while (true)
        {
            try { await _drain().ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { _failed(AlertWays.Failure("Alert ways: the work of a way", ex)); }
                catch { /* reporting must never stop the pump */ }
            }
            TaskCompletionSource idle;
            lock (_lock)
            {
                if (_again && !_stopped)
                {
                    _again = false;
                    continue;
                }
                _running = false;
                idle = _idle;
            }
            idle.TrySetResult();
            return;
        }
    }

    /// <summary>Completes when the pump is not running (tests wait for it; App does not).</summary>
    public Task Idle
    {
        get { lock (_lock) return _idle.Task; }
    }

    /// <summary>Nothing more is started. A run that is under way finishes by itself.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            _again = false;
        }
    }
}
