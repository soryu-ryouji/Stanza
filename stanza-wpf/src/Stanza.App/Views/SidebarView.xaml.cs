using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Stanza.App.Views;

/// <summary>
/// 侧栏视图：区块列表、项目/标签分组、底部工具按钮与最近文件弹层。
/// 纯视觉结构组件：DataContext 继承窗口（MainViewModel），事件经 Window.GetWindow 转发给
/// MainWindow 的同名方法处理——键盘分发/焦点/弹层动画等交互跨组件，由窗口统筹（同模板转发模式）。
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    private MainWindow Host => (MainWindow)System.Windows.Window.GetWindow(this);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Host.TitleBar_MouseLeftButtonDown(sender, e);
    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.ProjectList_SelectionChanged(sender, e);
    private void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host.TagList_SelectionChanged(sender, e);
    private void FacetList_PreviewKeyDown(object sender, KeyEventArgs e) => Host.FacetList_PreviewKeyDown(sender, e);
    private void FacetList_FocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e) => Host.FacetList_FocusWithinChanged(sender, e);
    private void RecentButton_Click(object sender, RoutedEventArgs e) => Host.RecentButton_Click(sender, e);
    private void RecentItem_Click(object sender, RoutedEventArgs e) => Host.RecentItem_Click(sender, e);
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => Host.SettingsButton_Click(sender, e);
}
