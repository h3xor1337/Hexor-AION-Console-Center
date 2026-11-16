using System.Globalization;
using System.Windows.Data;
namespace Hexor;
public sealed class OrdinalConverter : IValueConverter
{
 public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is int index?$"{index+1:00}":"";
 public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
