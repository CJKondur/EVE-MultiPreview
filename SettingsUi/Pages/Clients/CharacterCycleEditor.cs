using CheckBox = System.Windows.Controls.CheckBox;

namespace EveMultiPreview.SettingsUi;

/// <summary>Character page › Include in cycling: the session-only exclusion (Shift+click by default).
/// Greyed out without a running ThumbnailManager.</summary>
public sealed class CharacterCycleEditor : System.Windows.Controls.UserControl, IRefreshable
{
    private readonly SettingsContext _ctx;
    private readonly string _name;
    private readonly CheckBox _toggle;
    private bool _loading;

    public CharacterCycleEditor(SettingsContext ctx, string name)
    {
        _ctx = ctx;
        _name = name;
        _toggle = EditorCard.Toggle(true, on => { if (!_loading) CharacterStores.SetInCycle(_ctx, _name, on); });
        Content = EditorCard.Build("character.cycle", _toggle);
        Refresh();
    }

    public void Refresh()
    {
        _loading = true;
        var state = CharacterStores.IsInCycle(_ctx, _name);
        _toggle.IsChecked = state ?? true;
        _toggle.IsEnabled = state != null;
        _loading = false;
    }
}
