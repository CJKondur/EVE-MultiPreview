using System.Windows;

namespace EveMultiPreview.SettingsUi;

/// <summary>Two values the kit's flat shapes read that are not tokens (UI styles plan S3): the size of a check box's or a
/// radio button's box, which a look's implicit style of the control sets (each look has one; the contract's 95 tokens hold
/// no size of a box), and whether a switched-off box or dropdown is drawn with a dashed edge (Neon; spec §6.3, "the
/// outline shapes draw it dashed").</summary>
public static class Kit
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(Kit), new PropertyMetadata(16.0));

    public static double GetSize(DependencyObject d) => (double)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, double value) => d.SetValue(SizeProperty, value);

    public static readonly DependencyProperty DashedOffProperty = DependencyProperty.RegisterAttached(
        "DashedOff", typeof(bool), typeof(Kit), new PropertyMetadata(false));

    public static bool GetDashedOff(DependencyObject d) => (bool)d.GetValue(DashedOffProperty);
    public static void SetDashedOff(DependencyObject d, bool value) => d.SetValue(DashedOffProperty, value);
}
