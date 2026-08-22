using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Stanza.App.Views;

/// <summary>
/// 任务区视图：拖拽条、标题区（ScopeTitle）、任务列表与底部工具栏。
/// 纯视觉结构组件：DataContext 继承窗口（MainViewModel），事件经 Window.GetWindow 转发给
/// MainWindow 的同名方法处理——拖拽状态机/键盘分发等交互跨组件，由窗口统筹（同模板转发模式）。
/// </summary>
public partial class TaskAreaView : UserControl
{
    public TaskAreaView() => InitializeComponent();

    private MainWindow Host => (MainWindow)System.Windows.Window.GetWindow(this);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Host.TitleBar_MouseLeftButtonDown(sender, e);
    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.TaskList_SelectionChanged(sender, e);
    private void TaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Host.TaskList_PreviewMouseLeftButtonDown(sender, e);
    private void TaskList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) => Host.TaskList_PreviewMouseRightButtonDown(sender, e);
    private void ClearButton_Click(object sender, RoutedEventArgs e) => Host.ClearButton_Click(sender, e);
    private void DueDateButton_Click(object sender, RoutedEventArgs e) => Host.DueDateButton_Click(sender, e);
    private void MoveStateButton_Click(object sender, RoutedEventArgs e) => Host.MoveStateButton_Click(sender, e);
}
