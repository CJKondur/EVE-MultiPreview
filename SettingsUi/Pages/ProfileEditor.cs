using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>App › Profiles &amp; backups › Current profile: pick, new, copy, delete (spec §5).
/// Replaces the old header profile combo and buttons, and Data Management's Copy Profile.</summary>
public sealed class ProfileEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private bool _loading;

    public ProfileEditor(SettingsContext ctx)
    {
        _ctx = ctx;
        ProfileList.SelectionChanged += (_, _) =>
        {
            if (!_loading && ProfileList.SelectedItem is string name && name != _ctx.S.LastUsedProfile)
                ProfileActions.Switch(_ctx, name);
        };
        Content = EditorCard.Build("app.profiles.current", ProfileList, EditorCard.Row(
            EditorCard.Button("L.S.Profile.New", () => ProfileActions.NewInteractive(_ctx)),
            EditorCard.Button("L.S.Profile.Copy", () => ProfileActions.CopyInteractive(_ctx)),
            EditorCard.Button("L.S.Profile.Delete", () => ProfileActions.DeleteInteractive(_ctx, _ctx.S.LastUsedProfile), destructive: true)));
        Refresh();
    }

    public ComboBox ProfileList { get; } = new() { MinWidth = 200 };

    /// <summary>Called by the page refresh after any profile switch (menu, hotkey, this editor).</summary>
    public void Refresh()
    {
        _loading = true;
        ProfileList.ItemsSource = _ctx.Service.GetProfileNames();
        ProfileList.SelectedItem = _ctx.S.LastUsedProfile;
        _loading = false;
    }
}
