using System;
using System.Windows;
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
        // IDEAS #12: the look of a profile as one line of text, and a new profile from one.
        var codes = EditorCard.Row(
            EditorCard.Button("L.S.Profile.CopyLookCode", () => ProfileActions.CopyLookCode(_ctx, CopyText ?? ClipboardText.Set)),
            EditorCard.Button("L.S.Profile.FromCode", () => ProfileActions.FromCodeInteractive(_ctx)));
        codes.Margin = new Thickness(0, 8, 0, 0);
        var below = new StackPanel();
        below.Children.Add(EditorCard.Row(
            EditorCard.Button("L.S.Profile.New", () => ProfileActions.NewInteractive(_ctx)),
            EditorCard.Button("L.S.Profile.Copy", () => ProfileActions.CopyInteractive(_ctx)),
            EditorCard.Button("L.S.Profile.Compare", () => ProfileActions.CompareInteractive(_ctx)),
            EditorCard.Button("L.S.Profile.Delete", () => ProfileActions.DeleteInteractive(_ctx, _ctx.S.LastUsedProfile), destructive: true)));
        below.Children.Add(codes);
        Content = EditorCard.Build("app.profiles.current", ProfileList, below);
        Refresh();
    }

    public ComboBox ProfileList { get; } = new() { MinWidth = 200 };

    /// <summary>Test seam: stands in for ClipboardText.Set.</summary>
    public Func<string, bool>? CopyText { get; set; }

    /// <summary>Called by the page refresh after any profile switch (menu, hotkey, this editor).</summary>
    public void Refresh()
    {
        _loading = true;
        ProfileList.ItemsSource = _ctx.Service.GetProfileNames();
        ProfileList.SelectedItem = _ctx.S.LastUsedProfile;
        _loading = false;
    }
}
