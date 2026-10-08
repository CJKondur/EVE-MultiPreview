using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EveMultiPreview.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace EveMultiPreview.SettingsUi;

/// <summary>Colour text in any format the app accepts (#RRGGBB, 0xRRGGBB, rgb()) → swatch brush.</summary>
public sealed class ColorTextToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && Setting.IsColor(s) ? new SolidColorBrush(AppSettings.ParseColor(s)) : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Visible when the value is not null (the breadcrumb's group part on non-Home pages).</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
