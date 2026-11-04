using System;
using System.Windows.Threading;

namespace EveMultiPreview.Services;

public sealed class DispatcherDebounceTimer : IDebounceTimer
{
    private readonly DispatcherTimer _timer;
    private Action? _onElapsed;

    public DispatcherDebounceTimer(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var action = _onElapsed;
            _onElapsed = null;
            action?.Invoke();
        };
    }

    public void Restart(Action onElapsed)
    {
        _onElapsed = onElapsed;
        _timer.Stop();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _onElapsed = null;
    }
}
