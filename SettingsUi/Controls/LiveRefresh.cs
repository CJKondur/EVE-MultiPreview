using System;
using System.Windows;
using System.Windows.Threading;

namespace EveMultiPreview.SettingsUi;

/// <summary>Polls while an editor is on screen, so lists of running clients follow clients that
/// start and close without a Refresh button. Callers compare a cheap signature in the tick and
/// rebuild only when it changed.</summary>
public static class LiveRefresh
{
    public static void Attach(FrameworkElement owner, Action tick, double seconds = 2)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => tick();
        owner.Loaded += (_, _) => timer.Start();
        owner.Unloaded += (_, _) => timer.Stop();
    }
}
