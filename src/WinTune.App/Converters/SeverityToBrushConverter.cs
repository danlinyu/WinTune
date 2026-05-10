using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WinTune.Core.Models;

namespace WinTune.App.Converters;

public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xE1, 0x57, 0x59));
    private static readonly SolidColorBrush Yellow = new(Color.FromRgb(0xE6, 0xC2, 0x29));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x59, 0xA1, 0x4F));
    private static readonly SolidColorBrush Default = new(Color.FromRgb(0x1F, 0x1F, 0x2E));

    static SeverityToBrushConverter()
    {
        Red.Freeze(); Yellow.Freeze(); Green.Freeze(); Default.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            Severity.Red => Red,
            Severity.Yellow => Yellow,
            Severity.Green => Green,
            _ => Default
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
