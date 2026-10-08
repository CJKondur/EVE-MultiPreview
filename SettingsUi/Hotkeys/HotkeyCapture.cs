using System;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Runs the hotkey dialog with every MultiPreview hotkey released, so the key the player
/// presses reaches the dialog (and its clash warning) instead of switching clients.</summary>
public static class HotkeyCapture
{
    /// <summary>The hotkeys come back however the dialog ends: OK, Clear, Cancel, Esc, the X,
    /// Alt+F4 or an exception.</summary>
    public static string? Run(HotkeyService? hotkeys, Func<string?> showDialog)
    {
        try
        {
            hotkeys?.BeginCapture();   // inside the try: it raises the depth first, so a throw part-way still gets its EndCapture
            return showDialog();
        }
        finally { hotkeys?.EndCapture(); }
    }
}
