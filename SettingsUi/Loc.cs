using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EveMultiPreview.SettingsUi;

/// <summary>Shows the L.* resource named by Key, where the key itself can come from a binding
/// (<c>local:Loc.Key="{Binding LabelKey}"</c>). Uses SetResourceReference, so the text follows a
/// live language switch exactly like {DynamicResource} (spec §10).</summary>
public static class Loc
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(Loc), new PropertyMetadata(null, OnKeyChanged));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    /// <summary>Sets AutomationProperties.Name from the resource named by NameKey, so a screen reader reads the card's
    /// label in the current language and follows a live language switch (Plan 3d).</summary>
    public static readonly DependencyProperty NameKeyProperty = DependencyProperty.RegisterAttached(
        "NameKey", typeof(string), typeof(Loc), new PropertyMetadata(null, OnNameKeyChanged));

    public static string? GetNameKey(DependencyObject d) => (string?)d.GetValue(NameKeyProperty);
    public static void SetNameKey(DependencyObject d, string? value) => d.SetValue(NameKeyProperty, value);

    private static void OnNameKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (e.NewValue is string key && key.Length > 0) fe.SetResourceReference(AutomationProperties.NameProperty, key);
        else fe.ClearValue(AutomationProperties.NameProperty);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var target = d switch
        {
            TextBlock => TextBlock.TextProperty,
            HeaderedItemsControl => HeaderedItemsControl.HeaderProperty,
            HeaderedContentControl => HeaderedContentControl.HeaderProperty,
            ContentControl => ContentControl.ContentProperty,
            _ => null,
        };
        if (target == null || d is not FrameworkElement fe) return;
        if (e.NewValue is string key && key.Length > 0) fe.SetResourceReference(target, key);
        else fe.ClearValue(target);
    }
}
