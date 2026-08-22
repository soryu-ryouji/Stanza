using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Stanza.App.Behaviors;
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
        var path = WriteTempDoc("# DOING\n\n2026-08-18 任务一 +Apollo\n\n任务二\n\n");
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

            // 绑定接线：卡片文本与模型一致（描述、项目 chip、带「截止」前缀的截止日期）
            var task0 = doing.Tasks.First();
            var card = UiTestHost.ContainerOf(window, task0)!;
            var texts = VisualTreeEx.FindVisualChildren<TextBlock>(card).Select(t => t.Text).ToList();
            Assert.Contains(texts, t => t.Contains("任务一"));
            Assert.Contains(texts, t => t.Contains("+Apollo"));
            // 截止日期带「截止」前缀（前缀与日期值是两个独立 TextBlock）
            Assert.Contains(texts, t => t == "截止");
            Assert.Contains(texts, t => t == "2026-08-18");
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
    public void DueDate_ColorFollowsUrgency_AndStaysOnHeaderRowWhenExpanded() => OnUi(() =>
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var path = WriteTempDoc($"# DOING\n\n{today:yyyy-MM-dd} 今天截止的任务\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = (MainViewModel)window.DataContext;
            var task = vm.Blocks[0].Tasks.Single();
            UiTestHost.PumpUntil(() => UiTestHost.ContainerOf(window, task) != null, "容器生成");
            var card = UiTestHost.ContainerOf(window, task)!;
            var warning = (Brush)Application.Current.FindResource("WarningBrush");
            var dateText = today.ToString("yyyy-MM-dd");

            // 截止 = 今天：日期值着色为橙（trigger 生效；默认色在 Style Setter 中才可被 trigger 覆盖），
            // 「截止」前缀同步同色（同一内容块统一着色）
            UiTestHost.PumpUntil(() => VisualTreeEx.FindVisualChildren<TextBlock>(card)
                .Where(t => t.Text == dateText)
                .Any(t => ReferenceEquals(t.Foreground, warning)), "今天截止日期着色为橙");
            Assert.True(VisualTreeEx.FindVisualChildren<TextBlock>(card)
                .Where(t => t.Text == "截止")
                .Any(t => ReferenceEquals(t.Foreground, warning)), "前缀应与日期同色");

            // 展开：截止日留在标题行右侧（EditHeader），不落入详情面板第二行
            vm.ExpandTask(task);
            UiTestHost.PumpUntil(() => task.IsExpanded, "任务展开");
            var editHeader = VisualTreeEx.FindVisualChildren<Grid>(card).First(g => g.Name == "EditHeader");
            Assert.Contains(VisualTreeEx.FindVisualChildren<TextBlock>(editHeader), t => t.Text == dateText);
            var details = VisualTreeEx.FindVisualChildren<StackPanel>(card).First(s => s.Name == "DetailsPanel");
            Assert.DoesNotContain(VisualTreeEx.FindVisualChildren<TextBlock>(details), t => t.Text == dateText);
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

    [Fact]
    public void NKey_CreatesTask_OutsideTextInput_TypesInsideEditor() => OnUi(() =>
    {
        var path = WriteTempDoc("# DOING\n\n任务一\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = VM(window);
            var doing = vm.Blocks[0];
            window.Activate();
            window.TaskList.Focus();
            UiTestHost.PumpUntil(() => Keyboard.FocusedElement != null, "焦点进入任务列表");

            // 非输入上下文按裸 N：新建任务（任务作用域分发，焦点检查通过）
            UiTestHost.SendKey(window, Key.N);
            UiTestHost.PumpUntil(() => doing.Tasks.Count() == 2, "裸 N 新建任务");
            var draft = doing.Tasks.Last();
            Assert.True(draft.IsExpanded);   // 新任务展开待编辑

            // 编辑框内按 N：任务作用域检查让位给文本输入，不触发新建
            // （合成输入不产生 WPF 文本输入事件，字符插入是内建行为不在此验证；断言「未消费」的外在表现）
            UiTestHost.PumpUntil(() => Keyboard.FocusedElement is TextBox, "焦点进新任务编辑框");
            UiTestHost.SendKey(window, Key.N);
            UiTestHost.Pump(200);
            Assert.Equal(2, doing.Tasks.Count());   // 未再次新建
            Assert.True(Keyboard.FocusedElement is TextBox);   // 焦点未被抢走
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void PriorityPicker_AcceleratorKeyAppliesAndCloses() => OnUi(() =>
    {
        var path = WriteTempDoc("# DOING\n\n任务一\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = VM(window);
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });

            window.Activate();
            window.OpenPriorityPicker();
            UiTestHost.PumpUntil(
                () => window.ChoicePickerPanel.Visibility == Visibility.Visible
                    && Keyboard.FocusedElement == window.ChoicePickerPanel,
                "选择面板打开并聚焦");
            // 行即时构建：象限 A-D + 无优先级，共 5 行
            Assert.Equal(5, window.ChoicePickerRows.Children.Count);

            UiTestHost.SendKey(window, Key.D2);   // 面板内加速键直达：2 = 象限 B

            Assert.Equal('B', task.Priority);
            Assert.Equal(Visibility.Collapsed, window.ChoicePickerPanel.Visibility);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void FacetPicker_TagToggleKeepsOpen_ProjectAppliesAndCloses() => OnUi(() =>
    {
        var path = WriteTempDoc("# DOING\n\n任务一 +Apollo\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = VM(window);
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });

            // 标签：点击行 = toggle 应用，浮层保持开启（连续切换语义）
            window.OpenFacetPicker(FacetKind.Tag);
            UiTestHost.PumpUntil(() => window.FacetPickerPanel.Visibility == Visibility.Visible, "标签选择器打开");
            Assert.True(window.FacetPickerRows.Children.Count > 0);   // 内置常用标签候选

            window.FacetPickerRows.Children.OfType<Button>().First()
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var tag = Assert.Single(task.Tags);
            Assert.NotEmpty(tag);
            Assert.Equal(Visibility.Visible, window.FacetPickerPanel.Visibility);
            Assert.True(window.FacetPickerRows.Children.Count > 0);   // 行已重建

            // 项目（文档候选仅 Apollo）：应用即关闭（每条任务至多一个项目）
            window.OpenFacetPicker(FacetKind.Project);
            UiTestHost.PumpUntil(() => window.FacetPickerPanel.Visibility == Visibility.Visible, "项目选择器打开");
            window.FacetPickerRows.Children.OfType<Button>().First()
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(Visibility.Collapsed, window.FacetPickerPanel.Visibility);
            Assert.NotNull(task.ProjectName);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void FacetJumpMode_PreviewHighlightUntilCommitted() => OnUi(() =>
    {
        var path = WriteTempDoc("# DOING\n\n任务一 +Apollo\n\n");
        var window = UiTestHost.CreateWindow(path);
        try
        {
            var vm = VM(window);
            window.Activate();

            // 无选中任务时按 P：进入侧栏跳转模式，预览高亮态激活（浅色，区别于正式选中）
            UiTestHost.SendKey(window, Key.P);
            UiTestHost.PumpUntil(
                () => PreviewHighlight.GetIsActive(window.Sidebar.ProjectList),
                "跳转模式预览高亮激活");
            Assert.NotNull(vm.SelectedFacet);   // 移动选中即预览面板

            // Enter 确认：预览高亮态退出，条目呈现正式选中色
            UiTestHost.SendKey(window, Key.Enter);
            UiTestHost.PumpUntil(
                () => !PreviewHighlight.GetIsActive(window.Sidebar.ProjectList),
                "确认后预览高亮退出");
            Assert.NotNull(vm.SelectedFacet);   // 面板保持（确认而非取消）
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });
}
