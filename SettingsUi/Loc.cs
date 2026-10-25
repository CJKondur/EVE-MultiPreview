using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using EveMultiPreview.Services;

namespace EveMultiPreview.SettingsUi;

/// <summary>Shows the L.* resource named by Key, where the key itself can come from a binding
/// (<c>local:Loc.Key="{Binding LabelKey}"</c>). Uses SetResourceReference, so the text follows a
/// live language switch exactly like {DynamicResource} (spec §10). With Case Upper (a text style sets it from a
/// token, UI styles spec §5.4) a TextBlock shows that text in capitals, by the UI language's rules.</summary>
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

    /// <summary>How a TextBlock writes the text of its Key: as it is, or in capitals (UI styles spec §5.4).</summary>
    public static readonly DependencyProperty CaseProperty = DependencyProperty.RegisterAttached(
        "Case", typeof(TextCase), typeof(Loc), new PropertyMetadata(TextCase.Normal, OnCaseChanged));

    public static TextCase GetCase(DependencyObject d) => (TextCase)d.GetValue(CaseProperty);
    public static void SetCase(DependencyObject d, TextCase value) => d.SetValue(CaseProperty, value);

    /// <summary>The text of the Key while Case is Upper: the resource reference lands here, and the TextBlock shows it
    /// in capitals.</summary>
    private static readonly DependencyProperty RawTextProperty = DependencyProperty.RegisterAttached(
        "RawText", typeof(string), typeof(Loc), new PropertyMetadata(null, OnRawTextChanged));

    /// <summary>Set while the case put the words as written into AutomationProperties.Name (a screen reader would spell a
    /// word in capitals), so the case takes back only a name it gave.</summary>
    private static readonly DependencyProperty NamedByCaseProperty = DependencyProperty.RegisterAttached(
        "NamedByCase", typeof(bool), typeof(Loc), new PropertyMetadata(false));

    private static void OnNameKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.ClearValue(NamedByCaseProperty);
        if (e.NewValue is string key && key.Length > 0) fe.SetResourceReference(AutomationProperties.NameProperty, key);
        else fe.ClearValue(AutomationProperties.NameProperty);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Show(d, e.NewValue as string);

    /// <summary>Only the text of a Key is re-shown: a text bound or set another way is never touched (plan S1, ruling 16).</summary>
    private static void OnCaseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (GetKey(d) is { Length: > 0 } key) Show(d, key);
    }

    private static void Show(DependencyObject d, string? key)
    {
        if (d is TextBlock upper && GetCase(upper) == TextCase.Upper)
        {
            if (key is { Length: > 0 }) upper.SetResourceReference(RawTextProperty, key);
            else
            {
                upper.ClearValue(RawTextProperty);
                upper.ClearValue(TextBlock.TextProperty);
            }
            return;
        }
        d.ClearValue(RawTextProperty);
        if (d.GetValue(NamedByCaseProperty) is true)
        {
            d.ClearValue(AutomationProperties.NameProperty);
            d.ClearValue(NamedByCaseProperty);
        }
        var target = d switch
        {
            TextBlock => TextBlock.TextProperty,
            AccessText => AccessText.TextProperty,     // a button's label: "_" marks its access key (IDEAS #51)
            HeaderedItemsControl => HeaderedItemsControl.HeaderProperty,
            HeaderedContentControl => HeaderedContentControl.HeaderProperty,
            ContentControl => ContentControl.ContentProperty,
            _ => null,
        };
        if (target == null || d is not FrameworkElement fe) return;
        if (key is { Length: > 0 }) fe.SetResourceReference(target, key);
        else fe.ClearValue(target);
    }

    private static void OnRawTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || GetCase(text) != TextCase.Upper) return;
        text.Text = Upper(e.NewValue as string);
        // A screen reader reads the words as written. A name of the text's own (NameKey) stays.
        if (text.GetValue(NamedByCaseProperty) is true || DependencyPropertyHelper.GetValueSource(text, AutomationProperties.NameProperty).BaseValueSource == BaseValueSource.Default)
        {
            text.SetValue(AutomationProperties.NameProperty, e.NewValue as string ?? "");
            text.SetValue(NamedByCaseProperty, true);
        }
    }

    /// <summary>Capitals by the rules of the UI language (Turkish "i" is "İ"); Chinese, Japanese and Korean have none.
    /// German "ß" has no capital in the language's own rules (.NET leaves it as it is): it is written "SS".</summary>
    public static string Upper(string? text)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(LocalizationService.CurrentLanguage); }
        catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
        return (text ?? "").ToUpper(culture).Replace("ß", "SS");
    }
}
