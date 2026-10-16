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

    /// <summary>A card's template, found by resource reference. The card's ContentTemplate is set from it only when the
    /// template is another one: a ContentPresenter whose ContentTemplate is itself a resource reference makes its content
    /// anew whenever it is moved, though the template is the same (measured, plan S2 Task 7), and a look whose section
    /// template differs moves every list of cards. While the list is between two parents the reference finds nothing:
    /// that is no template, and the card keeps the one it has.</summary>
    private static readonly DependencyProperty TemplateOfTheCardProperty = DependencyProperty.RegisterAttached(
        "TemplateOfTheCard", typeof(DataTemplate), typeof(CardList),
        new PropertyMetadata(null, (d, e) => { if (e.NewValue is DataTemplate t) ((ContentPresenter)d).ContentTemplate = t; }));

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ContentPresenter presenter && TemplateKeyFor(item) is { } key)
            presenter.SetResourceReference(TemplateOfTheCardProperty, key);
    }
}
