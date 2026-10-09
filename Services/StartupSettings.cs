using System;
using System.Collections.Generic;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>The settings-open sequence of App.OnStartup (IDEAS #49, #55, #57), with everything injected so tests can run
/// it without windows: load (tried again for a file another program holds), then the restore prompt when the file
/// didn't load, or the newer-file question and a startup copy when it did, then the note beside the exe. Nothing in it
/// may stop the tray from coming up: a throw anywhere is passed to <c>error</c> and startup goes on with whatever
/// file is loaded.</summary>
public static class StartupSettings
{
    /// <summary>Tries after the first load, for a file a backup tool or cloud sync holds for a moment.</summary>
    public const int Retries = 2;

    public static void Open(SettingsService svc, SettingsHomeResult home, string exeDir, string? besideExe,
        Func<string, string, IReadOnlyList<string>, int?> ask, Func<string, string, string> str, DateTime now,
        Action<Exception> error, Action<TimeSpan> sleep, Action applyLanguage, Func<string> noteText)
    {
        try
        {
            // Only the last failed attempt counts as an error: one locked file is one event, not three.
            svc.Load(recordFailure: false);
            for (int retry = 0; retry < Retries && svc.LastLoadError is IOException; retry++)
            {
                sleep(TimeSpan.FromMilliseconds(500));
                svc.Load(recordFailure: false);
            }
            if (svc.LastLoadError is Exception failed) error(failed);
        }
        catch (Exception ex) { error(ex); }

        try
        {
            applyLanguage();   // before any window is shown (issue #86)
            if (!svc.LoadedSuccessfully)
            {
                // IDEAS #49: offer the backups instead of running the session on defaults.
                if (SettingsRestore.Offer(svc, besideExe, ask, str, now)) applyLanguage();
            }
            else
            {
                try
                {
                    if (home.OtherCopy != null && AsksAboutOtherCopy(home, exeDir)
                        && SettingsHome.OfferOtherCopy(svc, home.OtherCopy, ask, str, now))
                        applyLanguage();
                }
                catch (Exception ex) { error(ex); }
                SettingsUi.SettingsBackup.KeepStartupCopy(svc, now);   // IDEAS #49
            }
        }
        catch (Exception ex) { error(ex); }

        if (home.NoteNeeded)
        {
            try { SettingsHome.WriteNote(exeDir, noteText()); }
            catch (Exception ex) { error(ex); }
        }
    }

    /// <summary>The newer-file question is for the file in the other place. A portable install (portable.txt) is never
    /// asked, unless the note "Where are my settings.txt" sits beside the exe: that note proves this folder went through a
    /// move to AppData, so a newer AppData file is real work that the portable file doesn't have.</summary>
    public static bool AsksAboutOtherCopy(SettingsHomeResult home, string exeDir) =>
        !home.Portable || File.Exists(Path.Combine(exeDir, AppPaths.MovedNoteName));

    /// <summary>The setup wizard is for a first run. Not after a load that failed (started with defaults): the player has
    /// a setup, and nothing the wizard set could be saved over their file anyway.</summary>
    public static bool NeedsSetupWizard(SettingsService svc) => svc.LoadedSuccessfully && !svc.Settings.SetupCompleted;
}
