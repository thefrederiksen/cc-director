using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CcDirectorSetup.Converters;

public class HexColorToBrushConverter : IValueConverter
{
    public static readonly HexColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
            return SolidColorBrush.Parse(hex);
        return SolidColorBrush.Parse("#888888");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
