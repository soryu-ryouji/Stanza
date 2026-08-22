using System.Windows;

namespace Stanza.App.Behaviors;

/// <summary>
/// 预览高亮态：P/T 跳转模式中，facet 列表的选中项是「移动预览」（右侧面板实时跟随），
/// 与正式选中（Enter 确认/鼠标点击）用不同浓度的视觉区分——预览为浅蓝底 + 蓝字正常字重，
/// 正式选中为蓝底 + 蓝字加粗。由 MainWindow.Panels.cs 在进入/退出跳转模式时设置；
/// 样式触发器经 AncestorType=ListBox 绑定读取（Inherits 保证模板内可取到列表上的值）。
/// </summary>
public static class PreviewHighlight
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(PreviewHighlight),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsActive(DependencyObject obj) => (bool)obj.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject obj, bool value) => obj.SetValue(IsActiveProperty, value);
}
