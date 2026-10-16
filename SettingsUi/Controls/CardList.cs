using System.Windows;
using System.Windows.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>The cards of a section (spec §5.4). Each card's container takes its template by resource reference
/// (Fx.Card.&lt;Kind&gt;, Fx.Card.Actions, Fx.Info, Fx.Editor), so a card follows a change of look without the list
/// being rebuilt, and keeps its editor and its state.</summary>
public sealed class CardList : ItemsControl
{
    /// <summary>The resource key of an item's template, or null for an item that has none.</summary>
    public static string? TemplateKeyFor(object item) => item switch
    {
        SettingVM s => "Fx.Card." + s.Kind,
        ActionRowVM => "Fx.Card.Actions",
        InfoVM => "Fx.Info",
        EditorVM => "Fx.Editor",
        _ => null,
    };

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ContentPresenter presenter && TemplateKeyFor(item) is { } key)
            presenter.SetResourceReference(ContentPresenter.ContentTemplateProperty, key);
    }
}
