using System;

namespace EveMultiPreview.SettingsUi;

/// <summary>IDEAS #12: text on the clipboard, for the code buttons. Written, never read: a player pastes a code into a box.
/// Never called by a test (the editors take a seam).</summary>
public static class ClipboardText
{
    /// <summary>False when Windows would not let it (another program holds the clipboard at that moment).</summary>
    public static bool Set(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            return false;
        }
    }
}
