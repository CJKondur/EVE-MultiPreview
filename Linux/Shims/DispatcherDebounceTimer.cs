// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// Unlike the other stand-ins this one is real: the same debounce as the Windows type, on Avalonia's dispatcher.
using Avalonia.Threading;

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
