using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AravalsStream.App.Controls;

public sealed class PeakBrushConverter : IValueConverter
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(49, 214, 164));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(247, 189, 106));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(242, 135, 146));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var peak = value is double number ? number : 0;
        return peak >= 0.9 ? Red : peak >= 0.7 ? Amber : Green;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
