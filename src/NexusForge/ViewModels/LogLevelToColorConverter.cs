using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Sylnar.ViewModels;

public class LogLevelToColorConverter : IValueConverter
{
    public static readonly LogLevelToColorConverter Instance = new();

    private static readonly IBrush Info  = new ImmutableSolidColorBrush(Color.Parse("#6CA8FF"));
    private static readonly IBrush Warn  = new ImmutableSolidColorBrush(Color.Parse("#F2B84B"));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.Parse("#F2685C"));
    private static readonly IBrush Debug = new ImmutableSolidColorBrush(Color.Parse("#5E6A7A"));
    private static readonly IBrush Other = new ImmutableSolidColorBrush(Color.Parse("#9AA5B4"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            "INFO"  => Info,
            "WARN"  => Warn,
            "ERROR" => Error,
            "DEBUG" => Debug,
            _       => Other
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
