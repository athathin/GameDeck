using System.Globalization;
using System.Windows.Data;

namespace GameDeck.Controls;

/// <summary>
/// Turns the tile scale into a size: scale * a + b. The parameter is "a;b" (b is optional), e.g. "160;16" for a
/// 160 px wide tile with 8 px margin on each side.
/// </summary>
public class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = ((string)parameter).Split(';');
        var multiplier = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var offset = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;

        return (double)value * multiplier + offset;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
