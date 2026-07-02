using System.Globalization;
using System.Windows.Data;

namespace DupeHunter.Gui.Views;

/// <summary>Formats a byte count (long) as human-readable units for display columns.</summary>
public sealed class BytesToHumanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? Format.Bytes(bytes) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
