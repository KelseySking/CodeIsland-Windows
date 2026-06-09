using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodeIsland.WpfApp.Views;

public sealed class BoolVisibilityConverter : IValueConverter
{
    public static BoolVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
