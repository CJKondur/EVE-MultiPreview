using System;
using EveMultiPreview.Interop;
using EveMultiPreview.Models;
using EveMultiPreview.Views;

namespace EveMultiPreview.SettingsUi;

/// <summary>"Edit area" for a crop window (the old PickCropArea): hide the settings window, bring the
/// client to the front, let the player drag over it (CropPickerOverlay), store the part they picked and
/// bring the settings window back, through a CropPickSession.</summary>
public static class CropAreaPick
{
    /// <summary>Returns the L.* key of why picking couldn't start ({0} = the character), or null once the
    /// picker is open. <paramref name="picked"/> runs after a part was stored, before the window returns.</summary>
    public static string? Start(SettingsContext ctx, string character, CropDefinition def, Action picked)
    {
        var thumb = ctx.Services.Thumbnails?.GetThumbnailForCharacter(character);
        if (thumb == null) return "L.S.Crops.NeedsClient";
        var (cx, cy, cw, ch) = CropPickerOverlay.QueryClientScreenRect(thumb.EveHwnd);
        if (cw <= 0 || ch <= 0) return "L.S.Crops.NoClientArea";

        // Hidden so it can't come back over the client while the player picks; then shown and activated
        // again, as the old window did.
        var window = ctx.Host.DialogOwner;
        bool wasVisible = window?.IsVisible == true;
        var session = new CropPickSession(
            hide: () => { if (wasVisible) window?.Hide(); },
            restore: () =>
            {
                if (!wasVisible || window == null) return;
                window.Show();
                window.Activate();
            });

        void Store((int X, int Y, int W, int H) r)
        {
            CropEdits.ApplyPick(def, r, DpiHelper.GetScaleFactorForPoint(cx, cy));
            picked();
        }

        CropPickerOverlay? overlay = null;
        try
        {
            User32.SetForegroundWindow(thumb.EveHwnd);
            overlay = new CropPickerOverlay(cx, cy, cw, ch, cw, ch);
            overlay.Completed += r => Finish(session, r, Store, ctx.Host.Toast);
            overlay.Closed += (_, _) => Finish(session, null, Store, ctx.Host.Toast);   // Alt+F4, or anything else that closes it
            overlay.Show();
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CropAreaPick] picker failed: {ex.Message}");
            try { overlay?.Close(); } catch { }   // a half-open overlay would keep eating the mouse
            session.End(null, Store);              // once: Closed may already have ended it
            return "L.S.Crops.PickFailed";
        }
    }

    /// <summary>Ends the session from an overlay event. Nothing escapes: an exception thrown while storing
    /// would skip the overlay's own Close() and reach the dispatcher, so it becomes a toast instead. The
    /// session has already brought the settings window back by then.</summary>
    public static void Finish(CropPickSession session, (int X, int Y, int W, int H)? picked,
        Action<(int X, int Y, int W, int H)> store, Action<string> toast)
    {
        try
        {
            session.End(picked, store);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CropAreaPick] storing the area failed: {ex.Message}");
            toast(SettingsContext.Str("L.S.Crops.PickFailed"));
        }
    }
}
