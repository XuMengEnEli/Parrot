using System.Globalization;
using Avalonia.Data.Converters;

namespace Parrot.UI.Converters;

/// <summary>
/// 0~1 进度值 × 总像素 → 填充高度（px）。
/// 存在的原因：Semi 主题的 ProgressBar 不支持 Orientation="Vertical"，
/// 竖条会把阅读页头部撑坏；迷你卡改用 Border 轨道 + 本换算的填充条。
/// </summary>
public sealed class FractionToPixels : IValueConverter
{
    public static readonly FractionToPixels Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double f
           && parameter is string s
           && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
            ? Math.Clamp(f, 0d, 1d) * total
            : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
