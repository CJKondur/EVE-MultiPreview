using FrameworkElement = System.Windows.FrameworkElement;

namespace EveMultiPreview.SettingsUi;

/// <summary>Windows only (never in SharedSources.props): an editor card's view-model, which holds the WPF control
/// EditorFactories builds. The Linux app has its own EditorVM, holding an Avalonia control.</summary>
public sealed class EditorVM : ItemVM
{
    private FrameworkElement? _element;
    public EditorVM(EditorItem def, SettingsContext ctx) : base(def.Id, ctx) => Def = def;
    public EditorItem Def { get; }
    public override bool IsVisible => Def.ShowIf?.Invoke(Ctx.S) ?? true;
    public FrameworkElement Element => _element ??= EditorFactories.Create(Def, Ctx);
    public override void Refresh() { (_element as IRefreshable)?.Refresh(); RaiseAll(); }
}
