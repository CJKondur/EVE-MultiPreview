using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Views;

/// <summary>
/// Dialog shown when a new version is available, or for a version picked in About.
/// Handles download with progress and triggers the self-update. Plan 4 restyled it after the F2 reference; the buttons
/// and what they do are unchanged.
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateService _updateService;
    private readonly Action? _beforeApply;
    private readonly Action? _undoBeforeApply;
    private bool _beforeApplyRan;
    private bool _applied;
    private bool _downloading = false;
    private string? _downloadedPath;
    private bool _installAnyway;
    private bool _installUnverified;

    /// <param name="specificVersion">Opened from About → "Install a specific version" (an
    /// upgrade or a downgrade the user picked) rather than by an update check.</param>
    /// <param name="beforeApply">Runs after the download, right before the app exits to swap the exe.</param>
    /// <param name="undoBeforeApply">Puts back what <paramref name="beforeApply"/> changed when the dialog closes without installing.</param>
    public UpdateDialog(UpdateService updateService, bool specificVersion = false, Action? beforeApply = null, Action? undoBeforeApply = null)
    {
        InitializeComponent();
        FontFamily = UiFont.For(LocalizationService.CurrentLanguage);
        _updateService = updateService;
        _beforeApply = beforeApply;
        _undoBeforeApply = undoBeforeApply;

        TxtVersionInfo.Text = string.Format(Str("L.Upd.ReadyFmt", "Version {1} is ready. You have {0}."),
            _updateService.CurrentVersion, _updateService.LatestVersion);

        if (specificVersion)
        {
            string cur = _updateService.CurrentVersion, target = _updateService.LatestVersion ?? "?";
            bool older = Version.TryParse(target, out var t) && Version.TryParse(cur, out var c) && t < c;
            Title = "EVE MultiPreview";
            TxtHeader.Text = string.Format(Str("L.Upd.PickHeader", "Install v{0}"), target);
            TxtVersionInfo.Text = string.Format(older
                ? Str("L.Upd.OlderInfo",
                    "You have v{0}. This installs the OLDER v{1}. Your settings are backed up to the Backups folder first; options added after v{1} are lost if it saves them. \"Check for updates when the program starts\" is turned off so v{1} does not offer to update straight back.")
                : Str("L.Upd.PickInfo", "You have v{0}. This installs v{1}."), cur, target);
            BtnUpdateNow.Content = Str("L.Upd.InstallBtn", "Install");
        }

        ShowNotes(_updateService.ReleaseNotes);
    }

    /// <summary>The dialog the startup update check opens (App). It appears without taking focus, so a player in a fight
    /// keeps typing into EVE (spec §6, §11.7); the About page's dialogs open in front as before.</summary>
    public static UpdateDialog ForStartupCheck(UpdateService updateService) => new(updateService) { ShowActivated = false };

    /// <summary>"What's new", then the release notes formatted: headings and **labels** bold, bullets as bullets.</summary>
    private void ShowNotes(string? markdown)
    {
        NotesPanel.Children.Clear();
        NotesPanel.Children.Add(new TextBlock
        {
            Text = Str("L.Upd.WhatsNew", "What's new"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = FluentPalette.Brush("Fx.Text"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        var blocks = ReleaseNotesMarkup.Parse(markdown);
        if (blocks.Count == 0)
        {
            NotesPanel.Children.Add(Line(Str("L.Upd.NoNotes", "No release notes available.")));
            return;
        }
        foreach (var block in blocks)
        {
            var text = Line(null);
            if (block is HelpBullet)
            {
                text.Margin = new Thickness(12, 2, 0, 2);
                text.Inlines.Add(new Run("•  "));
            }
            foreach (var run in block.Runs)
                text.Inlines.Add(run.Bold
                    ? new Bold(new Run(run.Text)) { Foreground = FluentPalette.Brush("Fx.Text") }
                    : new Run(run.Text));
            NotesPanel.Children.Add(text);
        }
    }

    private static TextBlock Line(string? text) => new()
    {
        Text = text ?? "",
        FontSize = 13,
        LineHeight = 20,
        TextWrapping = TextWrapping.Wrap,
        Foreground = FluentPalette.Brush("Fx.TextSecondary"),
        Margin = new Thickness(0, 4, 0, 4),
    };

    private static string Str(string key, string english) => LocalizationService.Str(key, english);

    private void OnReleaseNotes(object s, RoutedEventArgs e)
    {
        var url = _updateService.ReleasePageUrl;
        if (!IsSafeReleaseUrl(url))
        {
            if (!string.IsNullOrEmpty(url)) App.LogError(new InvalidOperationException("Release page URL refused (not https://github.com): " + url));
            return;
        }
        try { Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>The URL comes from GitHub's JSON and is opened with shell execute: only an https github.com page is allowed.</summary>
    public static bool IsSafeReleaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private void OnLater(object s, RoutedEventArgs e)
    {
        if (!_downloading) Close();
    }

    /// <summary>The settings couldn't be given to the older version (IDEAS #57; the caller logs why): warn in the status line, and
    /// let the next click on Update now install anyway. Never blocks the downgrade outright.</summary>
    public void ShowHandBackFailed(object? buttonLabel = null)
    {
        _installAnyway = true;
        _downloading = false;
        BtnUpdateNow.IsEnabled = true;
        if (buttonLabel != null) BtnUpdateNow.Content = buttonLabel;
        BtnLater.IsEnabled = true;
        BtnReleaseNotes.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        TxtStatus.Text = string.Format(Str("L.Upd.HandBackFailed",
            "Your current settings couldn't be given to the older version. It will start with the settings last saved beside the exe. Click {0} again to install anyway."),
            BtnUpdateNow.Content);   // "Update now", or "Install" when a version was picked
        TxtStatus.Visibility = Visibility.Visible;
    }

    /// <summary>GitHub's answer held no checksum for this release, so the download can't be checked (IDEAS #53): say so
    /// in the status line, and let the next click on the button install it anyway. Nothing was installed.</summary>
    public void ShowNoDigest(object? buttonLabel = null)
    {
        _installUnverified = true;
        ShowStatus(buttonLabel, "L.Upd.NoDigest",
            "GitHub gave no checksum for this release, so MultiPreview can't check the download. Click {0} again to install it anyway, or {1} to download it from GitHub yourself.");
    }

    /// <summary>The download isn't the file GitHub published (IDEAS #53). It is deleted and nothing was installed;
    /// the next click downloads again. A mismatch found while downloading has already deleted the file; one found
    /// at the install (the file changed in between) has not, so it is deleted here and the line shown stays true.</summary>
    public void ShowDigestMismatch(object? buttonLabel = null)
    {
        if (_downloadedPath != null)
        {
            try { System.IO.File.Delete(_downloadedPath); } catch { }
        }
        _downloadedPath = null;
        _installUnverified = false;
        ShowStatus(buttonLabel, "L.Upd.DigestMismatch",
            "The download doesn't match the checksum GitHub publishes for this release. It was deleted and nothing was installed. Click {0} to try again, or {1} to download it from GitHub yourself.");
    }

    /// <summary>Back to the state before the click, with a line under the notes: {0} is the install button's label,
    /// {1} the release page button's.</summary>
    private void ShowStatus(object? buttonLabel, string key, string english)
    {
        _downloading = false;
        BtnUpdateNow.IsEnabled = true;
        if (buttonLabel != null) BtnUpdateNow.Content = buttonLabel;
        BtnLater.IsEnabled = true;
        BtnReleaseNotes.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Collapsed;
        TxtStatus.Text = string.Format(Str(key, english), BtnUpdateNow.Content, BtnReleaseNotes.Content);
        TxtStatus.Visibility = Visibility.Visible;
    }

    /// <summary>True after the no-checksum warning: the next click installs the unchecked download.</summary>
    public bool InstallsUnverified => _installUnverified;

    /// <summary>Runs <c>beforeApply</c> (a downgrade turns the startup update check off) and remembers it ran, so closing the
    /// dialog without installing can undo it.</summary>
    public void BeginApply()
    {
        _beforeApply?.Invoke();
        _beforeApplyRan = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_beforeApplyRan && !_applied)
        {
            try { _undoBeforeApply?.Invoke(); } catch (Exception ex) { Debug.WriteLine($"[Update] undo failed: {ex.Message}"); }
        }
    }

    /// <summary>True after a failed hand-back: the next click installs anyway.</summary>
    public bool InstallsAnyway => _installAnyway;

    /// <summary>The error-log line for a hand-back that carries a reason: the settings couldn't be handed back, or they
    /// are in place (or were already the same) and only the layout presets couldn't go along.</summary>
    public static string HandBackLogLine(HandBackResult handBack) => handBack.State == HandBackState.Failed
        ? "Settings hand-back to the older version failed: " + handBack.Reason
        : "Settings hand-back to the older version: only the layout presets were not handed back. " + handBack.Reason;

    private async void OnUpdateNow(object s, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        var buttonLabel = BtnUpdateNow.Content;   // restored on failure, whichever mode this is

        // Disable buttons during download
        BtnUpdateNow.IsEnabled = false;
        BtnUpdateNow.Content = Str("L.Upd.Downloading", "Downloading…");
        BtnLater.IsEnabled = false;
        BtnReleaseNotes.IsEnabled = false;

        // Show progress bar
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p * 100;
                TxtProgressStatus.Text = string.Format(Str("L.Upd.ProgressFmt", "Downloading… {0}"), p.ToString("P0", CultureInfo.CurrentCulture));
            });

            var downloadedPath = _downloadedPath ??= await _updateService.DownloadUpdateAsync(progress);   // a second click keeps the download

            // IDEAS #53: a download that doesn't match GitHub's checksum never gets here (DownloadUpdateAsync deleted it
            // and threw). One that can't be checked, because GitHub gave no checksum, waits for a second click.
            if (_updateService.ExpectedSha256 == null && !_installUnverified)
            {
                ShowNoDigest(buttonLabel);
                return;
            }

            // Download complete — apply update
            TxtProgressStatus.Text = Str("L.Upd.Applying", "Download complete. Applying the update…");
            TxtStatus.Text = Str("L.Upd.Restarting", "Restarting with the new version…");
            TxtStatus.Visibility = Visibility.Visible;

            // Brief pause so user sees the message
            await System.Threading.Tasks.Task.Delay(800);

            BeginApply();
            bool wasInstallAnyway = _installAnyway;
            // Before the call: an install shuts the app down from inside ApplyUpdate, and this dialog closes (OnClosed)
            // before the call returns. It must not take that for a cancel and undo beforeApply.
            _applied = true;
            var handBack = _updateService.ApplyUpdate(downloadedPath, _installAnyway, _installUnverified);
            if (handBack.Reason != null) App.LogError(new System.IO.IOException(HandBackLogLine(handBack)));
            if (handBack.State == HandBackState.Failed && !wasInstallAnyway)
            {
                _applied = false;                  // nothing was installed: closing the dialog now undoes beforeApply
                ShowHandBackFailed(buttonLabel);   // the second click installs anyway, and the app shuts down: no second warning
            }
        }
        catch (Exception ex)
        {
            _applied = false;
            if (ex is UpdateIntegrityException)
            {
                // The download isn't GitHub's file (IDEAS #53): it is deleted, nothing was installed, and the line
                // under the notes says so in the player's language.
                App.LogError(ex);
                ShowDigestMismatch(buttonLabel);
                return;
            }
            _downloading = false;
            BtnUpdateNow.IsEnabled = true;
            BtnUpdateNow.Content = buttonLabel;
            BtnLater.IsEnabled = true;
            BtnReleaseNotes.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;

            TxtStatus.Text = string.Format(Str("L.Upd.FailedFmt", "The update failed: {0}"), ex.Message);
            TxtStatus.Visibility = Visibility.Visible;
            Debug.WriteLine($"[Update] Download/apply failed: {ex}");
        }
    }
}
