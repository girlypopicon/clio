using System.Globalization;
using Avalonia.Data.Converters;
using Humanizer;

namespace CLIo.Converters;

public sealed class HumanizeDateConverter : IValueConverter
{
    public static readonly HumanizeDateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTimeOffset dto => dto.Humanize(),
            DateTime dt => dt.Humanize(),
            string s when DateTimeOffset.TryParse(s, out var parsed) => parsed.Humanize(),
            _ => value
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
