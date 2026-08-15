using System.Windows;
using System.Windows.Controls;

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
}
