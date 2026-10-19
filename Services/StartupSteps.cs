using System;

namespace EveMultiPreview.Services;

/// <summary>One step of the deferred start-up (App.OnStartup's timer tick), run on its own (IDEAS P93). A step that
/// throws costs only itself: the next step runs. What is reported is what failed and the TYPE of what was thrown
/// (AlertWays.Failure), never the exception itself: its message may name a character, and neither may reach the error
/// log. A reporter that throws is ignored too. Never throws.</summary>
public static class StartupSteps
{
    /// <param name="name">A fixed text of the caller's ("Hotkeys"), never a player's text.</param>
    /// <param name="step">The work.</param>
    /// <param name="failed">Told what failed (App.LogError).</param>
    public static void Run(string name, Action step, Action<Exception> failed)
    {
        try { step(); }
        catch (Exception ex)
        {
            try { failed(AlertWays.Failure("Start-up step " + name, ex)); }
            catch { /* reporting must never cost the steps after this one */ }
        }
    }
}
