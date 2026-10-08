using System;
using System.Windows.Threading;

namespace EveMultiPreview.Services;

/// <summary>What a settings change needs re-applied live.</summary>
[Flags]
public enum ApplyScope
{
    None = 0,
    /// <summary>Only thumbnail opacity changed (cheap path).</summary>
    Opacity = 1,
    /// <summary>Anything else that affects thumbnails: full ReapplySettings.</summary>
    Thumbnails = 2,
    /// <summary>Crop definitions or visibility changed: CropManager.Refresh.</summary>
    Crops = 4,
    /// <summary>Only save. Nothing to re-apply: the setting is read where it is used,
    /// or App re-reads it when Applied fires (hotkeys, alert hub).</summary>
    Save = 8,
}

public interface IDebounceTimer
{
    /// <summary>(Re)start the countdown; <paramref name="onElapsed"/> runs once when it ends.</summary>
    void Restart(Action onElapsed);
    void Stop();
}

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

/// <summary>The one path from "a setting changed" to "it is saved and live":
/// debounce, save, reapply, refresh crops, raise Applied. Shared by the old
/// and new settings windows (settings UI rework, spec §8.2).</summary>
public sealed class SettingsApplier
{
    private readonly Action _save;
    private readonly Action _reapplyThumbnails;
    private readonly Action _reapplyOpacity;
    private readonly Action _refreshCrops;
    private readonly IDebounceTimer _timer;
    private ApplyScope _pending;

    public SettingsApplier(Action save, Action reapplyThumbnails, Action reapplyOpacity, Action refreshCrops, IDebounceTimer timer)
    {
        _save = save;
        _reapplyThumbnails = reapplyThumbnails;
        _reapplyOpacity = reapplyOpacity;
        _refreshCrops = refreshCrops;
        _timer = timer;
    }

    /// <summary>Raised after every apply. App re-registers hotkeys and syncs the alert hub on it.</summary>
    public event Action? Applied;

    public bool HasPending => _pending != ApplyScope.None;

    public void Request(ApplyScope scope)
    {
        if (scope == ApplyScope.None) return;
        _pending |= scope;
        _timer.Restart(Flush);
    }

    public void ApplyNow(ApplyScope extra = ApplyScope.None)
    {
        _timer.Stop();
        _pending |= extra;
        Flush();
    }

    private void Flush()
    {
        var scope = _pending;
        _pending = ApplyScope.None;
        _save();
        if (scope.HasFlag(ApplyScope.Thumbnails)) _reapplyThumbnails();
        else if (scope.HasFlag(ApplyScope.Opacity)) _reapplyOpacity();
        if (scope.HasFlag(ApplyScope.Crops)) _refreshCrops();
        Applied?.Invoke();
    }
}
