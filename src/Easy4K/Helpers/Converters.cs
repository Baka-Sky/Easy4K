using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Easy4K.Helpers;

/// <summary>bool → Visibility</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>double 0..100 → "xx%"</summary>
public sealed class PercentTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is double d ? $"{d:0.#}%" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>LogLevel → 前景色（Info 灰/Success 绿/Warning 黄/Error 红/Command 浅蓝）</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = new(Color.FromArgb(255, 180, 180, 180));
    private static readonly SolidColorBrush Success = new(Color.FromArgb(255, 80, 200, 100));
    private static readonly SolidColorBrush Warning = new(Color.FromArgb(255, 230, 180, 40));
    private static readonly SolidColorBrush Error = new(Color.FromArgb(255, 230, 80, 80));
    private static readonly SolidColorBrush Command = new(Color.FromArgb(255, 120, 180, 255));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Models.LogLevel lv)
        {
            return lv switch
            {
                Models.LogLevel.Info => Info,
                Models.LogLevel.Success => Success,
                Models.LogLevel.Warning => Warning,
                Models.LogLevel.Error => Error,
                Models.LogLevel.Command => Command,
                _ => Info
            };
        }
        return Info;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
