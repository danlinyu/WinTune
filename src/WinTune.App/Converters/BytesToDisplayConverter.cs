using System.Globalization;
using System.Windows.Data;

namespace WinTune.App.Converters;

public sealed class BytesToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "";
        long bytes = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
        const long KB = 1024L;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB) return string.Format(CultureInfo.InvariantCulture, "{0:N2} GB", bytes / (double)GB);
        if (bytes >= MB) return string.Format(CultureInfo.InvariantCulture, "{0:N2} MB", bytes / (double)MB);
        if (bytes >= KB) return string.Format(CultureInfo.InvariantCulture, "{0:N2} KB", bytes / (double)KB);
        return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
