using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WinTune.App.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush  { get; set; } = new SolidColorBrush(Color.FromRgb(0xB0, 0x10, 0x10));
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
