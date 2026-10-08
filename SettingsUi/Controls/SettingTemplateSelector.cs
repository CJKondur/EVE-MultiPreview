using System.Windows;
using System.Windows.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>Picks the card template for an item VM: Fx.Card.&lt;Kind&gt; for settings.</summary>
public sealed class SettingTemplateSelector : DataTemplateSelector
{
    public static string? KeyFor(object item) => item switch
    {
        SettingVM s => "Fx.Card." + s.Kind,
        ActionRowVM => "Fx.Card.Actions",
        InfoVM => "Fx.Info",
        EditorVM => "Fx.Editor",
        _ => null,
    };

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        KeyFor(item) is { } key && container is FrameworkElement fe ? fe.TryFindResource(key) as DataTemplate : null;
}
