using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sylnar.ViewModels;

public class LogLevelToColorConverter : IValueConverter
{
    public static readonly LogLevelToColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string level)
        {
            return level switch
            {
                "INFO"  => new SolidColorBrush(Color.Parse("#3FB950")),
                "WARN"  => new SolidColorBrush(Color.Parse("#E3B341")),
                "ERROR" => new SolidColorBrush(Color.Parse("#F85149")),
                "DEBUG" => new SolidColorBrush(Color.Parse("#484F58")),
                _       => new SolidColorBrush(Color.Parse("#8B949E"))
            };
        }
        return new SolidColorBrush(Color.Parse("#B0B0C0"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
