using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EveMultiPreview.Services;

namespace EveMultiPreview.Linux;

/// <summary>"Update available" (the Windows UpdateDialog): the versions, what's new, and Update now / Release page / Later.
/// Update now downloads the Linux release file, which UpdateService checks against the SHA-256 GitHub publishes; a file
/// that isn't GitHub's is never installed, and one GitHub gave no checksum for is installed only on a second click. Then
/// the program is replaced and MultiPreview restarts. A program installed where this account can't write (system-wide)
/// gets the release page instead.</summary>
public sealed class UpdateDialog : Window
{
    private readonly UpdateService _service;
    private readonly Action _exit;
    private readonly Button _now, _page, _later;
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6, IsVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string? _downloaded;
    private bool _busy, _installUnverified;

    private static string T(string key, string english) => LocalizationService.Str(key, english);

    /// <param name="exit">Ends MultiPreview once the new program is in place (it starts when this one has gone).</param>
    public UpdateDialog(UpdateService service, Action exit)
    {
        _service = service;
        _exit = exit;
        Title = T("L.Upd.Title", "Update Available — EVE MultiPreview");
        Width = 560;
        Height = 480;
        MinWidth = 420;
        MinHeight = 320;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(new TextBlock { Text = T("L.Upd.Header", "Update available"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        top.Children.Add(new TextBlock { Text = string.Format(T("L.Upd.ReadyFmt", "Version {1} is ready. You have {0}."), service.CurrentVersion, service.LatestVersion), Opacity = 0.8 });
        top.Children.Add(new TextBlock { Text = T("L.Upd.WhatsNew", "What's new"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) });

        var notes = new ScrollViewer
        {
            Content = new SelectableTextBlock
            {
                Text = string.IsNullOrWhiteSpace(service.ReleaseNotes) ? T("L.Upd.NoNotes", "No release notes available.") : service.ReleaseNotes,
                TextWrapping = TextWrapping.Wrap,
            },
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.Parse("#14FFFFFF")),
        };

        _now = new Button { Content = T("L.Upd.UpdateNow", "Update now"), IsDefault = true };
        _page = new Button { Content = T("L.Upd.ReleasePage", "Release page") };
        _later = new Button { Content = T("L.Upd.Later", "Later"), IsCancel = true };
        _now.Click += (_, _) => UpdateNow();
        _page.Click += (_, _) => OpenReleasePage();
        _later.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_now);
        buttons.Children.Add(_page);
        buttons.Children.Add(_later);
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(_bar);
        bottom.Children.Add(_status);
        bottom.Children.Add(buttons);

        var layout = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(notes);
        Content = layout;

        // GitHub sent a checksum this code can't read: nothing will be downloaded; the release page is the way.
        if (service.UnreadableDigestRefusal() is not null)
        {
            _now.IsEnabled = false;
            Say(string.Format(T("L.Upd.DigestUnreadable", "GitHub's checksum for this release is in a form MultiPreview can't read, so MultiPreview will not download or install this release. Click {1} to download it from GitHub yourself."),
                T("L.Upd.UpdateNow", "Update now"), T("L.Upd.ReleasePage", "Release page")));
        }
    }

    private void Say(string text)
    {
        _status.Text = text;
        _status.IsVisible = true;
    }

    private void OpenReleasePage()
    {
        // Only this repository's own pages are opened, whatever GitHub's answer said.
        var url = _service.ReleasePageUrl is { } page && page.StartsWith(About.Repo + "/", StringComparison.Ordinal) ? page : About.Repo + "/releases";
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); } catch (Exception) { }
    }

    private async void UpdateNow()
    {
        if (_busy) return;
        _busy = true;
        var label = _now.Content;
        _now.IsEnabled = _later.IsEnabled = _page.IsEnabled = false;
        _now.Content = T("L.Upd.Downloading", "Downloading…");
        _bar.IsVisible = true;
        try
        {
            var progress = new Progress<double>(p =>
            {
                _bar.Value = p * 100;
                Say(string.Format(T("L.Upd.ProgressFmt", "Downloading… {0}"), p.ToString("P0", CultureInfo.CurrentCulture)));
            });
            var path = _downloaded ??= await _service.DownloadUpdateAsync(progress);   // a second click keeps the download

            // No checksum from GitHub: the second click installs anyway.
            if (_service.ExpectedSha256 == null && !_installUnverified)
            {
                _installUnverified = true;
                Again(label, string.Format(T("L.Upd.NoDigest", "GitHub gave no checksum for this release, so MultiPreview can't check the download. Click {0} again to install it anyway, or {1} to download it from GitHub yourself."),
                    label, T("L.Upd.ReleasePage", "Release page")));
                return;
            }

            Say(T("L.Upd.Applying", "Download complete. Applying the update…"));
            var result = await Task.Run(() => _service.InstallLinux(path, _installUnverified));
            if (result == UpdateService.LinuxInstall.Started)
            {
                Say(T("L.Upd.Restarting", "Restarting with the new version…"));
                await Task.Delay(600);
                _exit();
                return;
            }
            // Installed where this account can't write (a system-wide install): the player updates it the way it came.
            Again(label, string.Format(T("L.Upd.FailedFmt", "The update failed: {0}"),
                T("L.Linux.UpdateNotWritable", "MultiPreview's folder can't be written by your account. Download the new version from the release page.")));
            _now.IsEnabled = false;
        }
        catch (UpdateIntegrityException refusal)
        {
            ErrorLog.Shared.Record(refusal, "Update", DateTime.Now);
            _downloaded = null;
            Again(label, string.Format(T("L.Upd.DigestMismatch", "The downloaded file could not be confirmed as the one GitHub published, so it will not be installed. Click {0} to try again, or {1} to download it from GitHub yourself."),
                label, T("L.Upd.ReleasePage", "Release page")));
        }
        catch (Exception e)
        {
            Again(label, string.Format(T("L.Upd.FailedFmt", "The update failed: {0}"), e.Message));
        }
    }

    private void Again(object? label, string text)
    {
        _busy = false;
        _now.Content = label;
        _now.IsEnabled = _later.IsEnabled = _page.IsEnabled = true;
        _bar.IsVisible = false;
        Say(text);
    }
}

/// <summary>Where the About links point.</summary>
internal static class About
{
    public const string Repo = "https://github.com/CJKondur/EVE-MultiPreview";
}
