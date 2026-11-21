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

// Keeps a step option visible but disabled unless the selected step's type matches the
// parameter kind (the vendor editor's behavior: minutes and Reset timer apply only to
// Gathering, Smart patrol only to Kinah Farming).
public sealed class StepTypeEnabledConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is InstructionType type && parameter is string kind
            && System.Enum.TryParse<InstructionType>(kind, out var expected) && type == expected;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LocalizeConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is string s ? Translations.Localize(s) : value ?? "";
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
