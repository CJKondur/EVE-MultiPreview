using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EveMultiPreview.Services;
using EveMultiPreview.SettingsUi;

namespace EveMultiPreview.Linux;

/// <summary>S8: About › Install a specific version, the Avalonia port of VersionPickerEditor (keep in sync): every GitHub
/// release that ships the Linux download (the shared UpdateService.ListReleasesAsync, which keeps only this repository's
/// own release files), the installed one greyed out, and Install, which opens the Linux update dialog aimed at the chosen
/// release. The list is asked for only when the player opens it, so showing the page never reaches the network. A
/// downgrade turns the startup update check off before the program is replaced (or the older version would offer to
/// update straight back), and an install that does not happen gives it back.</summary>
internal sealed partial class SettingsEditors
{
    internal Control VersionPickerCard(SettingsContext ctx)
    {
        var panel = Titled("app.about.installVersion");
        var versions = new ComboBox { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        bool loaded = false;

        async void Load()
        {
            if (loaded) return;
            loaded = true;
            versions.Items.Clear();
            versions.Items.Add(new ComboBoxItem { Content = LocalizationService.Str("L.About.LoadingVersions", "Loading…"), IsEnabled = false });
            try
            {
                var releases = await new UpdateService().ListReleasesAsync(ctx.S.ReceivePreReleaseUpdates);
                versions.Items.Clear();
                foreach (var r in releases)
                {
                    bool installed = r.Version == AboutActions.VersionText;
                    string label = "v" + r.Version
                        + (installed ? " " + LocalizationService.Str("L.About.Installed", "(installed)") : "")
                        + (r.PreRelease ? " " + LocalizationService.Str("L.About.PreReleaseTag", "(pre-release)") : "");
                    versions.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = label }, Tag = r, IsEnabled = !installed });
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                loaded = false;   // the next open tries again
                versions.Items.Clear();
                ctx.Host.Toast(string.Format(LocalizationService.Str("L.About.VersionsFailed", "Could not load the version list: {0}"), ex.Message));
            }
        }

        void Install()
        {
            if (versions.SelectedItem is not ComboBoxItem { Tag: UpdateService.ReleaseInfo release }) return;
            var service = new UpdateService();
            service.Select(release);
            bool older = Version.TryParse(release.Version, out var picked) && Version.TryParse(AboutActions.VersionText, out var running) && picked < running;
            bool checkWas = ctx.S.CheckForUpdatesOnStartup;
            service.CheckWas = checkWas;
            // Saved at once: the program is replaced and MultiPreview ends right after.
            Action? beforeApply = older ? () => { ctx.S.CheckForUpdatesOnStartup = false; ctx.Service.Save(); } : null;
            Action? undo = older ? () => { ctx.S.CheckForUpdatesOnStartup = checkWas; ctx.Service.Save(); } : null;
            _host.InstallVersion(service, beforeApply, undo);
        }

        versions.DropDownOpened += (_, _) => Load();
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        versions.Margin = new Thickness(0, 0, 12, 4);
        row.Children.Add(versions);
        row.Children.Add(Action("L.S.Act.app.about.install", Install));
        panel.Children.Add(row);
        return panel;
    }
}
