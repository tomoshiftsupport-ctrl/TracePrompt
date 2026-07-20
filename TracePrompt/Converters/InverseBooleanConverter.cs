using System.Globalization;
using System.Windows.Data;

namespace TracePrompt.Converters;

/// <summary>bool を反転します。「すべて／自動」トグルがオンの間、隣接する数値入力欄を無効化するために使います。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value!;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value!;
}
