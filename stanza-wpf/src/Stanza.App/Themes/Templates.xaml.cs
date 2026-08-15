using System.Windows;
using System.Windows.Controls;
using Stanza.App.ViewModels;

namespace Stanza.App.Themes;

/// <summary>
/// 任务卡片等数据模板的资源字典。带 code-behind 是因为模板内含事件处理器；
/// 处理器统一转发给 MainWindow 的交互逻辑。
/// </summary>
public partial class TaskTemplates : ResourceDictionary
{
    public TaskTemplates()
    {
        InitializeComponent();
    }

    private void TaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox
            && Window.GetWindow(checkBox) is MainWindow window)
        {
            window.HandleTaskCheck(checkBox, e);
        }
    }

    // ContextMenu 在独立弹层视觉树中，Window.GetWindow 无法直接到达窗口，
    // 经 PlacementTarget（任务卡片，位于正常视觉树）中转
    private void FacetMenu_TagClick(object sender, RoutedEventArgs e) => ForwardFacetClick(sender, FacetKind.Tag);

    private void FacetMenu_ProjectClick(object sender, RoutedEventArgs e) => ForwardFacetClick(sender, FacetKind.Project);

    private static void ForwardFacetClick(object sender, FacetKind kind)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: { } target } }
            && Window.GetWindow(target) is MainWindow window)
        {
            window.OpenFacetPicker(kind);
        }
    }
}
