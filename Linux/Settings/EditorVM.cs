using Avalonia.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>The Linux side of the Windows EditorVM (SettingsUi/ViewModels/EditorVM.cs, Windows-only), with the same name so the
/// shared PageVM builds it: an editor card's view-model. The settings window builds the Avalonia control once (LinuxEditors)
/// and keeps it here, so coming back to a page shows the same editor.</summary>
public sealed class EditorVM : ItemVM
{
    public EditorVM(EditorItem def, SettingsContext ctx) : base(def.Id, ctx) => Def = def;
    public EditorItem Def { get; }
    public override bool IsVisible => Def.ShowIf?.Invoke(Ctx.S) ?? true;

    /// <summary>The editor, once the window has built it.</summary>
    public Control? Element { get; set; }

    public override void Refresh() { (Element as IRefreshable)?.Refresh(); RaiseAll(); }
}
