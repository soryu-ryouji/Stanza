using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Stanza.App.Services;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App.Tests;

/// <summary>
/// 视图接线测试：真实窗口与视觉树，覆盖 VM 层够不着的链路——
/// XAML 绑定求值、键盘分发（PreProcessInput → 键位表 → 焦点作用域检查）、模板事件转发。
/// 业务规则断言归 VM 层测试；这里只钉「接线」。
/// </summary>
[Collection("AppData")]
public class MainWindowWiringTests : StaTestHost.StaFactBase
{
    private static string WriteTempDoc(string text)
    {
        var dir = Path.Combine(Path.GetTempPath(), "stanza-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "todo.stanza");
        File.WriteAllText(path, text);
        return path;
    }

    private static MainViewModel VM(MainWindow window) => (MainViewModel)window.DataContext;

    [Fact]
    public void Window_LoadsDocument_RendersTaskCards() => OnUi(() =>
    {
        var path = WriteTempDoc("# DOING\n\n任务一 +Apollo\n\n任务二\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = VM(window);
            var doing = vm.Blocks.First(b => b.State == TaskState.Doing);

            // 容器生成（非虚拟化列表，Loaded 后全量生成）
            UiTestHost.PumpUntil(
                () => doing.Tasks.Select(t => UiTestHost.ContainerOf(window, t)).All(c => c != null),
                "任务卡片容器生成");
            Assert.Equal(2, window.TaskList.Items.Count);

            // 绑定接线：卡片文本与模型一致（描述、项目 chip）
            var card = UiTestHost.ContainerOf(window, doing.Tasks.First())!;
            var texts = VisualTreeEx.FindVisualChildren<TextBlock>(card).Select(t => t.Text).ToList();
            Assert.Contains(texts, t => t.Contains("任务一"));
            Assert.Contains(texts, t => t.Contains("+Apollo"));

            // 标题区绑定：ScopeTitle = 当前区块本地化名称
            var titles = VisualTreeEx.FindVisualChildren<TextBlock>(window).Select(t => t.Text);
            Assert.Contains(titles, t => t == Loc.StateName(TaskState.Doing));
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void SpaceKey_CompletesSelectedTask_ViaKeyDispatch() => OnUi(() =>
    {
        var path = WriteTempDoc("# DOING\n\n任务一\n\n任务二\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = VM(window);
            var doing = vm.Blocks.First(b => b.State == TaskState.Doing);
            var target = doing.Tasks.First();
            vm.UpdateSelection(new[] { target });

            // 任务作用域命令（Space 完成）要求焦点在任务列表内（非编辑框）
            window.Activate();
            window.TaskList.Focus();
            UiTestHost.PumpUntil(
                () => Keyboard.FocusedElement is DependencyObject f && VisualTreeEx.IsWithin(f, window.TaskList),
                "焦点进入任务列表");

            // 合成按键走完整分发：PreProcessInput → 键位表解析 → 焦点作用域检查 → 完成动画
            UiTestHost.SendKey(window, Key.Space);

            // 动画（勾选 → 变灰 → 淡出）结束后才提交流转（§9：DONE 顶部）
            UiTestHost.PumpUntil(() => target.State == TaskState.Done, "完成动画提交流转");
            Assert.Same(target, vm.Blocks.First(b => b.State == TaskState.Done).Tasks.First());
            Assert.DoesNotContain(target, doing.Tasks);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void TaskCheckClick_CompletesTask_ViaTemplateForwarding() => OnUi(() =>
    {
        var path = WriteTempDoc("# DOING\n\n任务一\n\n任务二\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = VM(window);
            var doing = vm.Blocks.First(b => b.State == TaskState.Doing);
            var target = doing.Tasks.First();
            UiTestHost.PumpUntil(() => UiTestHost.ContainerOf(window, target) != null, "任务卡片容器生成");

            // 模板勾选框 Click → TaskTemplates.TaskCheck_Click → MainWindow.HandleTaskCheck → 完成动画
            var box = VisualTreeEx.FindVisualChildren<CheckBox>(UiTestHost.ContainerOf(window, target)!).First();
            box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            UiTestHost.PumpUntil(() => target.State == TaskState.Done, "勾选完成提交流转");
            Assert.Same(target, vm.Blocks.First(b => b.State == TaskState.Done).Tasks.First());
            Assert.DoesNotContain(target, doing.Tasks);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });
}
