using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Stanza.App.Behaviors;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App.Tests;

/// <summary>
/// 键盘分发链路测试：裸键（导航/双语义/废弃/面板/Esc）经 PreProcessInput → 任务作用域检查 → 执行。
/// 修饰键组合（Ctrl 系）受合成输入限制不在覆盖内（修饰键取自真实键盘设备）。
/// </summary>
[Collection("AppData")]
public class MainWindowKeyDispatchTests : StaTestHost.StaFactBase
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

    /// <summary>含三个任务（带项目/标签）的文档窗口；焦点已确认停在任务列表。</summary>
    private static MainWindow WindowWithTasks(out MainViewModel vm)
    {
        var window = UiTestHost.CreateWindow(WriteTempDoc(
            "# DOING\n\n任务一 +Apollo #紧急\n\n任务二\n\n任务三\n\n"));
        window.Activate();
        window.TaskList.Focus();
        UiTestHost.PumpUntil(() => Keyboard.FocusedElement != null, "窗口焦点就绪");
        vm = VM(window);
        return window;
    }

    [Fact]
    public void JkKeys_NavigateTaskSelection() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var doing = vm.Blocks[0];
            var tasks = doing.Tasks.ToList();

            // 焦点落空（列表本体）按 j：选中首项并聚焦容器
            UiTestHost.SendKey(window, Key.J);
            UiTestHost.PumpUntil(() => vm.SelectedTask == tasks[0], "j 选中首项");

            // 焦点在选中条目上再按 j：字母键经 FocusedTaskChrome 作用域接管，移动选中
            UiTestHost.SendKey(window, Key.J);
            UiTestHost.PumpUntil(() => vm.SelectedTask == tasks[1], "j 移动选中到第二项");

            UiTestHost.SendKey(window, Key.K);
            UiTestHost.PumpUntil(() => vm.SelectedTask == tasks[0], "k 移回首项");
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void TKey_WithoutSelection_EntersJumpMode_EscRestores() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            // 无选中任务按 T：侧栏标签列表跳转模式（预览态）
            UiTestHost.SendKey(window, Key.T);
            UiTestHost.PumpUntil(
                () => PreviewHighlight.GetIsActive(window.Sidebar.TagList),
                "T 进入标签跳转模式");
            Assert.NotNull(vm.SelectedFacet);   // 面板预览中

            // Esc 取消：恢复进入前的区块视图
            UiTestHost.SendKey(window, Key.Escape);
            UiTestHost.PumpUntil(
                () => !PreviewHighlight.GetIsActive(window.Sidebar.TagList),
                "Esc 退出跳转模式");
            Assert.Null(vm.SelectedFacet);
            Assert.NotNull(vm.SelectedBlock);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void TKey_WithSelection_OpensTagPicker_EscCloses() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });
            vm.SelectedTask = task;

            UiTestHost.SendKey(window, Key.T);
            UiTestHost.PumpUntil(
                () => window.FacetPickerPanel.Visibility == Visibility.Visible,
                "T 打开标签选择器");
            UiTestHost.PumpUntil(
                () => Keyboard.FocusedElement is TextBox,
                "焦点锁进选择器输入框");   // 聚焦是 BeginInvoke 异步迁移，需先等位再发键

            UiTestHost.SendKey(window, Key.Escape);
            UiTestHost.PumpUntil(
                () => window.FacetPickerPanel.Visibility == Visibility.Collapsed,
                "Esc 关闭选择器");
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void BackKey_DiscardsSelectedTask_FocusFallsToNext() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var doing = vm.Blocks[0];
            var tasks = doing.Tasks.ToList();
            vm.UpdateSelection(new[] { tasks[1] });
            vm.SelectedTask = tasks[1];

            UiTestHost.SendKey(window, Key.Back);   // 废弃：移入 DELETE（§9 回收站语义）

            UiTestHost.PumpUntil(
                () => tasks[1].State == TaskState.Delete, "任务移入 DELETE");
            Assert.DoesNotContain(tasks[1], doing.Tasks);
            // 焦点/选中落位到原位置的后续任务（连续操作语义）
            UiTestHost.PumpUntil(() => vm.SelectedTask == tasks[2], "选中落位到后续任务");
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void SelectionHighlight_DimsWhenFocusLeavesTaskList() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            vm.SelectedTask = task;
            var container = UiTestHost.ContainerOf(window, task)!;
            container.Focus();
            UiTestHost.PumpUntil(() => Keyboard.FocusedElement == container, "焦点进任务条目");

            var card = VisualTreeEx.FindVisualChildren<Border>(container).First(b => b.Name == "Card");
            var activeBrush = (Brush)Application.Current.FindResource("TaskSelectedBrush");
            var inactiveBrush = (Brush)Application.Current.FindResource("InactiveSelectionBrush");
            UiTestHost.PumpUntil(() => ReferenceEquals(card.Background, activeBrush), "持焦选中为完整色");

            // 焦点移到侧栏：选中高亮降为失焦淡色（比预览态更弱）
            UiTestHost.SendKey(window, Key.Left);
            UiTestHost.PumpUntil(
                () => Keyboard.FocusedElement is DependencyObject f
                    && VisualTreeEx.IsWithin(f, window.Sidebar.BlockList),
                "焦点移到区块列表");
            UiTestHost.PumpUntil(() => ReferenceEquals(card.Background, inactiveBrush), "失焦选中转淡色");

            // 焦点回到任务区：恢复完整色
            UiTestHost.SendKey(window, Key.Right);
            UiTestHost.PumpUntil(() => ReferenceEquals(card.Background, activeBrush), "回焦恢复完整色");
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void MKey_WithSelection_OpensMovePicker() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });
            vm.SelectedTask = task;

            UiTestHost.SendKey(window, Key.M);
            UiTestHost.PumpUntil(
                () => window.ChoicePickerPanel.Visibility == Visibility.Visible,
                "M 打开状态选择面板");
            Assert.Equal(4, window.ChoicePickerRows.Children.Count);   // 四状态
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void LeftKey_FromTaskArea_MovesFocusToBlockList() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            // 焦点在任务条目上按左：回侧栏区块列表（区块视图无跳转预览概念）
            var task = vm.Blocks[0].Tasks.First();
            vm.SelectedTask = task;
            var container = UiTestHost.ContainerOf(window, task)!;
            container.Focus();
            UiTestHost.PumpUntil(
                () => Keyboard.FocusedElement == container, "焦点进任务条目");

            UiTestHost.SendKey(window, Key.Left);

            UiTestHost.PumpUntil(
                () => Keyboard.FocusedElement is DependencyObject f
                    && VisualTreeEx.IsWithin(f, window.Sidebar.BlockList),
                "焦点移到区块列表");
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void RightKey_FromBlockList_MovesFocusToTaskList() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var block = vm.Blocks[0];
            var blockContainer = window.Sidebar.BlockList.ItemContainerGenerator
                .ContainerFromItem(block) as UIElement;
            blockContainer!.Focus();
            UiTestHost.PumpUntil(
                () => Keyboard.FocusedElement == blockContainer, "焦点进区块条目");

            // 侧栏按右：确认进任务区——无选中时选中首项
            UiTestHost.SendKey(window, Key.Right);

            UiTestHost.PumpUntil(
                () => vm.SelectedTask == block.Tasks.First(), "右移选中首项");
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void LeftRight_InFacetPanel_PreviewAndCommit() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            // 进入项目面板，焦点放任务区
            var facet = vm.Projects.Single(p => p.Name == "Apollo");
            vm.SelectedFacet = facet;
            window.TaskList.Focus();
            UiTestHost.PumpUntil(() => Keyboard.FocusedElement != null, "焦点就绪");

            // 任务区按左：回 facet 列表并进入跳转预览（浅色高亮）
            UiTestHost.SendKey(window, Key.Left);
            UiTestHost.PumpUntil(
                () => PreviewHighlight.GetIsActive(window.Sidebar.ProjectList),
                "左移进入跳转预览");

            // 预览中按右：确认（同 Enter），预览态退出、焦点进任务区并选中首项
            UiTestHost.SendKey(window, Key.Right);
            UiTestHost.PumpUntil(
                () => !PreviewHighlight.GetIsActive(window.Sidebar.ProjectList),
                "右移确认退出预览态");
            Assert.Same(facet, vm.SelectedFacet);   // 面板保持
            Assert.NotNull(vm.SelectedTask);        // 已选中面板首项
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });
}
