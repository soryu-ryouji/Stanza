using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>集合计数为 0 时可见（用于空区块提示）。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>状态区块的点缀色（极克制地用于小圆点）。</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly Brush Doing = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0xE9)));
    private static readonly Brush Wait = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));
    private static readonly Brush Done = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)));
    private static readonly Brush Delete = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is TaskState s ? s switch
        {
            TaskState.Doing => Doing,
            TaskState.Wait => Wait,
            TaskState.Done => Done,
            TaskState.Delete => Delete,
            _ => Delete,
        } : Delete;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

/// <summary>保存状态文字颜色。</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    private static readonly Brush Faint = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    private static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));
    private static readonly Brush Danger = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SaveStatus s ? s switch
        {
            SaveStatus.Dirty or SaveStatus.Info => Warning,
            SaveStatus.Error => Danger,
            _ => Faint,
        } : Faint;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

/// <summary>状态枚举 → 区块名（面板视图分组头）。</summary>
public sealed class StateToNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is TaskState s ? TaskStateNames.ToHeader(s) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>任务列表模板选择：任务是卡片，GapItem 是拖拽位置预览。</summary>
public sealed class TaskListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TaskTemplate { get; set; }
    public DataTemplate? GapTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
        => item is GapItem ? GapTemplate : TaskTemplate;
}
