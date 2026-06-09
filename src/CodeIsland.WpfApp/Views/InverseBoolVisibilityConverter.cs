using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodeIsland.WpfApp.Views;

public sealed class InverseBoolVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool boolean && boolean ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility visibility && visibility != Visibility.Visible;
}
