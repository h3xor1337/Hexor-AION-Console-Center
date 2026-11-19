using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hexor;

// Binds a radio button to a verified enum value stored as int or string:
// checked ⇔ bound value equals the option value. Unchecking never pushes a value
// (WPF unchecks the sibling automatically), so ConvertBack returns DoNothing for false.
public sealed class EqualToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? parameter! : Binding.DoNothing;
}
