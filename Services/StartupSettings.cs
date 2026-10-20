using System;
using System.Collections.Generic;
using System.IO;

namespace EveMultiPreview.Services;

/// <summary>The settings-open sequence of App.OnStartup (IDEAS #49, #55, #57), with everything injected so tests can run
/// it without windows: the missing-file prompt when the file is gone but copies of it exist, then load (tried again for
/// a file another program holds), then the restore prompt when the file didn't load, or the newer-file question and a
/// startup copy when it did, then the note beside the exe. Nothing in it may stop the tray from coming up: a throw
/// anywhere is passed to <c>error</c> and startup goes on with whatever file is loaded.</summary>
public static class StartupSettings
{
    /// <summary>Tries after the first load, for a file a backup tool or cloud sync holds for a moment.</summary>
    public const int Retries = 2;

    public static void Open(SettingsService svc, SettingsHomeResult home, string exeDir, string? besideExe,
        Func<string, string, IReadOnlyList<string>, int?> ask, Func<string, string, string> str, DateTime now,
        Action<Exception> error, Action<TimeSpan> sleep, Action applyLanguage, Func<string> noteText)
    {
        // Its own guard: a throw while setting the language skips neither a question nor the startup copy.
        void Language()
        {
            try { applyLanguage(); }
            catch (Exception ex) { error(ex); }
        }

        // A settings file that is missing while copies of it exist: the player decides, before anything loads, because
        // Load makes a file of defaults when there is none. Unanswered (closed, or the prompt couldn't be shown), this
        // session runs on defaults and nothing is loaded or written, so the prompt returns on the next start.
        bool unanswered = false, startedFresh = false;
        try
        {
            if (MissingWithCopies(svc.SettingsPath, besideExe))
            {
                Language();   // before any window is shown (issue #86); nothing is loaded yet, so Windows' language
                var answer = SettingsRestore.OfferMissing(svc, besideExe, ask, str, now);
                unanswered = answer == MissingFileAnswer.Closed;
                startedFresh = answer == MissingFileAnswer.StartFresh;
            }
        }
        catch (Exception ex)
        {
            error(ex);
            unanswered = true;
        }

        if (!unanswered)
        {
            try
            {
                // Only the last failed attempt counts as an error: one locked file is one event, not three. "Start
                // fresh" lands here with no file: Load makes one of defaults, the first-run path.
                svc.Load(recordFailure: false);
                for (int retry = 0; retry < Retries && svc.LastLoadError is IOException; retry++)
                {
                    sleep(TimeSpan.FromMilliseconds(500));
                    svc.Load(recordFailure: false);
                }
                if (svc.LastLoadError is Exception failed) error(failed);
            }
            catch (Exception ex) { error(ex); }
        }

        Language();   // before any window is shown (issue #86)
        // Unanswered: on defaults with saving blocked (nothing was loaded), and no restore prompt on top of the one just closed.
        if (!unanswered)
        {
            try
            {
                if (!svc.LoadedSuccessfully)
                {
                    // IDEAS #49: offer the backups instead of running the session on defaults.
                    if (SettingsRestore.Offer(svc, besideExe, ask, str, now, home.OtherBackups)) Language();
                }
                else
                {
                    try
                    {
                        if (home.OtherCopy != null && AsksAboutOtherCopy(home, exeDir)
                            && SettingsHome.OfferOtherCopy(svc, home.OtherCopy, ask, str, now))
                            Language();
                    }
                    catch (Exception ex) { error(ex); }
                    // IDEAS P43: its own question, after the settings one. The layout presets are read later (they aren't
                    // loaded here), so nothing needs loading again.
                    try
                    {
                        if (home.OtherPresets != null)
                            SettingsHome.OfferOtherPresets(Path.Combine(home.ConfigDir, SettingsHome.PresetsFileName), home.OtherPresets, ask, str, now);
                    }
                    catch (Exception ex) { error(ex); }
                    // IDEAS #49. Not for the file of defaults "Start fresh" just made: it would push a real copy out.
                    if (!startedFresh) SettingsUi.SettingsBackup.KeepStartupCopy(svc, now);
                }
            }
            catch (Exception ex) { error(ex); }
        }

        if (home.NoteNeeded)
        {
            try { SettingsHome.WriteNote(exeDir, noteText()); }
            catch (Exception ex) { error(ex); }
        }
    }

    /// <summary>The missing-file prompt is due: the settings file is not there, and a copy of it can be restored (a file
    /// in the settings folder's Backups that loads, or the settings file beside the exe). With no copy anywhere it is
    /// a first run, and nothing is asked.</summary>
    public static bool MissingWithCopies(string settingsPath, string? besideExe) =>
        !File.Exists(settingsPath)
        && SettingsRestore.Choices(Path.Combine(Path.GetDirectoryName(settingsPath) ?? "", "Backups"), besideExe).Count > 0;

    /// <summary>The newer-file question is for the file in the other place. A portable install (portable.txt) is never
    /// asked, unless the note "Where are my settings.txt" sits beside the exe: that note proves this folder went through a
    /// move to AppData, so a newer AppData file is real work that the portable file doesn't have.</summary>
    public static bool AsksAboutOtherCopy(SettingsHomeResult home, string exeDir) =>
        !home.Portable || File.Exists(Path.Combine(exeDir, AppPaths.MovedNoteName));

    /// <summary>The setup wizard is for a first run. Not after a load that failed or a missing-file prompt that was
    /// closed (started with defaults): the player has a setup, and nothing the wizard set could be saved anyway.</summary>
    public static bool NeedsSetupWizard(SettingsService svc) => svc.LoadedSuccessfully && !svc.Settings.SetupCompleted;
}
