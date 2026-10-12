using System;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>Runs one piece of work at a time on the thread pool, never on the thread that asks for it (IDEAS batch
/// 6). The work is a "drain": it takes what is waiting somewhere else (the utterances to speak, the notifications
/// to show) until nothing is left. <see cref="Wake"/> starts it unless it is running; a wake that comes while it
/// runs makes it run once more afterwards, so nothing that arrived during a run is left waiting. The pump holds no
/// queue of its own, so nothing can pile up in it.</summary>
public sealed class WorkPump
{
    private readonly Func<Task> _drain;
    private readonly Action<Exception> _failed;
    private readonly object _lock = new();
    private bool _running, _again, _stopped;
    private TaskCompletionSource _idle = Done();

    /// <param name="drain">The work. What it throws is reported and costs that run; the next wake runs it again.</param>
    /// <param name="failed">Is told what the work threw (App logs it).</param>
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
                try { _failed(ex); } catch { /* reporting must never stop the pump */ }
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
