using System;
using System.Diagnostics;
using System.Windows;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

/// <summary>
/// Modal dialog shown when a new version is available.
/// Handles download with progress and triggers the self-update.
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateService _updateService;
    private readonly Action? _beforeApply;
    private bool _downloading = false;

    /// <param name="specificVersion">Opened from About → "Install a specific version" (an
    /// upgrade or a downgrade the user picked) rather than by an update check.</param>
    /// <param name="beforeApply">Runs after the download, right before the app exits to swap the exe.</param>
    public UpdateDialog(UpdateService updateService, bool specificVersion = false, Action? beforeApply = null)
    {
        InitializeComponent();
        _updateService = updateService;
        _beforeApply = beforeApply;

        TxtVersionInfo.Text = $"A new version of EVE MultiPreview is available!\n" +
                              $"Current: v{_updateService.CurrentVersion}  →  New: v{_updateService.LatestVersion}";

        if (specificVersion)
        {
            string cur = _updateService.CurrentVersion, target = _updateService.LatestVersion ?? "?";
            bool older = Version.TryParse(target, out var t) && Version.TryParse(cur, out var c) && t < c;
            Title = "EVE MultiPreview";
            TxtHeader.Text = string.Format(LocalizationService.Str("L.Upd.PickHeader", "⬇ Install v{0}"), target);
            TxtVersionInfo.Text = string.Format(older
                ? LocalizationService.Str("L.Upd.OlderInfo",
                    "You have v{0}. This installs the OLDER v{1}. Your settings are backed up to the Backups folder first; options added after v{1} are lost if it saves them. \"Check for updates when the program starts\" is turned off so v{1} does not offer to update straight back.")
                : LocalizationService.Str("L.Upd.PickInfo", "You have v{0}. This installs v{1}."), cur, target);
            BtnUpdateNow.Content = LocalizationService.Str("L.Upd.InstallBtn", "⬇ Install");
        }

        TxtReleaseNotes.Text = !string.IsNullOrWhiteSpace(_updateService.ReleaseNotes)
            ? _updateService.ReleaseNotes
            : "No release notes available.";
    }

    private void OnReleaseNotes(object s, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_updateService.ReleasePageUrl))
        {
            try { Process.Start(new ProcessStartInfo(_updateService.ReleasePageUrl) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void OnLater(object s, RoutedEventArgs e)
    {
        if (!_downloading) Close();
    }

    private async void OnUpdateNow(object s, RoutedEventArgs e)
    {
        if (_downloading) return;
        _downloading = true;
        var buttonLabel = BtnUpdateNow.Content;   // restored on failure, whichever mode this is

        // Disable buttons during download
        BtnUpdateNow.IsEnabled = false;
        BtnUpdateNow.Content = "⏳ Downloading...";
        BtnLater.IsEnabled = false;
        BtnReleaseNotes.IsEnabled = false;

        // Show progress bar
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p * 100;
                TxtProgressStatus.Text = $"Downloading... {p:P0}";
            });

            var downloadedPath = await _updateService.DownloadUpdateAsync(progress);

            // Download complete — apply update
            TxtProgressStatus.Text = "Download complete. Applying update...";
            TxtStatus.Text = "🔄 Restarting with updated version...";
            TxtStatus.Visibility = Visibility.Visible;

            // Brief pause so user sees the message
            await System.Threading.Tasks.Task.Delay(800);

            _beforeApply?.Invoke();
            _updateService.ApplyUpdate(downloadedPath);
        }
        catch (Exception ex)
        {
            _downloading = false;
            BtnUpdateNow.IsEnabled = true;
            BtnUpdateNow.Content = buttonLabel;
            BtnLater.IsEnabled = true;
            BtnReleaseNotes.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;

            TxtStatus.Text = $"❌ Update failed: {ex.Message}";
            TxtStatus.Visibility = Visibility.Visible;
            Debug.WriteLine($"[Update] Download/apply failed: {ex}");
        }
    }
}
