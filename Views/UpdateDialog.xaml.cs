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
    private bool _downloading = false;

    public UpdateDialog(UpdateService updateService)
    {
        InitializeComponent();
        _updateService = updateService;

        TxtVersionInfo.Text = $"A new version of EVE MultiPreview is available!\n" +
                              $"Current: v{_updateService.CurrentVersion}  →  New: v{_updateService.LatestVersion}";

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

            _updateService.ApplyUpdate(downloadedPath);
        }
        catch (Exception ex)
        {
            _downloading = false;
            BtnUpdateNow.IsEnabled = true;
            BtnUpdateNow.Content = "⬆ Update Now";
            BtnLater.IsEnabled = true;
            BtnReleaseNotes.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;

            TxtStatus.Text = $"❌ Update failed: {ex.Message}";
            TxtStatus.Visibility = Visibility.Visible;
            Debug.WriteLine($"[Update] Download/apply failed: {ex}");
        }
    }
}
