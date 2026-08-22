using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    // 标题/备注编辑框的按键在隧道阶段转发（先于编辑框自身消化）：Tab 在两者间切换、备注框 Ctrl+Enter 提交
    private void TaskTitleEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox box && Window.GetWindow(box) is MainWindow window)
            window.HandleTaskTitleKey(box, e);
    }

    private void TaskNotesEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox box && Window.GetWindow(box) is MainWindow window)
            window.HandleTaskNotesKey(box, e);
    }

    // ContextMenu 在独立弹层视觉树中，Window.GetWindow 无法直接到达窗口，
    // 经 PlacementTarget（任务卡片，位于正常视觉树）中转
    private void FacetMenu_TagClick(object sender, RoutedEventArgs e) => ForwardFacetClick(sender, FacetKind.Tag);

    // 「截止…」：打开日期选择器（同 D 键/工具栏按钮路径）
    private void FacetMenu_DueClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: { } target } }
            && Window.GetWindow(target) is MainWindow window)
            window.OpenDuePicker();   // anchor 为 null：取鼠标位置
    }

    private void FacetMenu_ProjectClick(object sender, RoutedEventArgs e) => ForwardFacetClick(sender, FacetKind.Project);

    /// <summary>菜单打开时把带 Tag（命令名）的菜单项的快捷键提示刷新为键位表当前手势——
    /// 用户改键后提示不过期。</summary>
    private void FacetContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Tag is string name && Enum.TryParse<AppCommand>(name, out var command))
                item.InputGestureText = Keymap.Current.Describe(command, null) ?? "";
    }

    private static void ForwardFacetClick(object sender, FacetKind kind)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: { } target } }
            && Window.GetWindow(target) is MainWindow window)
        {
            window.OpenFacetPicker(kind);
        }
    }
}
