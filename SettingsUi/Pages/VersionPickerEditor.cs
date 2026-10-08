using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using EveMultiPreview.Services;
using EveMultiPreview.Views;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Install a specific version, ported unchanged from SettingsWindow.Panels2.cs:2097-2149.
/// The list loads only when the dropdown opens, so opening settings never calls the network.</summary>
public sealed class VersionPickerEditor : System.Windows.Controls.UserControl
{
    private readonly SettingsContext _ctx;
    private readonly ComboBox _versions = new() { MinWidth = 220 };
    private bool _loaded;

    public VersionPickerEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        _versions.DropDownOpened += async (_, _) => await LoadAsync();
        Content = EditorCard.Build("app.about.installVersion",
            EditorCard.Row(_versions, EditorCard.Button("L.S.Act.app.about.install", Install)));
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        _versions.Items.Clear();
        _versions.Items.Add(new ComboBoxItem { Content = LocalizationService.Str("L.About.LoadingVersions", "Loading…"), IsEnabled = false });
        try
        {
            var releases = await new UpdateService().ListReleasesAsync(_ctx.S.ReceivePreReleaseUpdates);
            _versions.Items.Clear();
            foreach (var r in releases)
            {
                bool installed = r.Version == AboutActions.VersionText;
                string label = "v" + r.Version
                    + (installed ? " " + LocalizationService.Str("L.About.Installed", "(installed)") : "")
                    + (r.PreRelease ? " " + LocalizationService.Str("L.About.PreReleaseTag", "(pre-release)") : "");
                _versions.Items.Add(new ComboBoxItem { Content = label, Tag = r, IsEnabled = !installed });
            }
        }
        catch (Exception ex)
        {
            _loaded = false;   // the next open retries
            _versions.Items.Clear();
            _ctx.Host.Toast(string.Format(LocalizationService.Str("L.About.VersionsFailed", "Could not load the version list: {0}"), ex.Message));
        }
    }

    private void Install()
    {
        if (_versions.SelectedItem is not ComboBoxItem { Tag: UpdateService.ReleaseInfo release }) return;
        var service = new UpdateService();
        service.Select(release);
        bool older = Version.Parse(release.Version) < Version.Parse(AboutActions.VersionText);
        // A downgrade turns off the startup update check, or the older version would offer to
        // update straight back on every launch. Saved synchronously: the app exits next.
        Action? beforeApply = older ? () => { _ctx.S.CheckForUpdatesOnStartup = false; _ctx.Service.Save(); } : null;
        new UpdateDialog(service, specificVersion: true, beforeApply) { Owner = _ctx.Host.DialogOwner }.ShowDialog();
    }
}
