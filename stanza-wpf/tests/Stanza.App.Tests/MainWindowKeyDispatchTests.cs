using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Stanza.App;
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
    public void DKey_OpensDuePicker_PresetAndClearApply() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });
            vm.SelectedTask = task;
            var today = DateOnly.FromDateTime(DateTime.Today);

            window.OpenDuePicker();   // Shift+T 入口（合成输入无法注入修饰键，直调打开方法）
            UiTestHost.PumpUntil(
                () => window.DatePickerPanel.Visibility == Visibility.Visible,
                "D 打开日期选择器");
            // 预设三行（无日期后缀）；无当前截止时清除按钮隐藏；输入框空时显示「日期」水印
            Assert.Equal(3, window.DatePickerRows.Children.Count);
            Assert.Equal(Visibility.Collapsed, window.DueClearButton.Visibility);
            UiTestHost.PumpUntil(() => VisualTreeEx.FindVisualChildren<TextBlock>(window.DatePickerInput)
                .Any(w => w.Name == "Watermark" && w.Text == "日期" && w.Visibility == Visibility.Visible),
                "空输入显示「日期」水印");

            // 点「明天」行：应用并关闭
            window.DatePickerRows.Children.OfType<Button>().ElementAt(1)
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(today.AddDays(1), task.Due);
            Assert.Equal(Visibility.Collapsed, window.DatePickerPanel.Visibility);

            // 再打开：预填当前截止；「清除截止」在面板最底部出现，点击清除
            window.OpenDuePicker();   // Shift+T 入口（合成输入无法注入修饰键，直调打开方法）
            UiTestHost.PumpUntil(
                () => window.DatePickerPanel.Visibility == Visibility.Visible,
                "再次打开日期选择器");
            Assert.Equal(3, window.DatePickerRows.Children.Count);
            Assert.Equal(today.AddDays(1).ToString("yyyy-MM-dd"), window.DatePickerInput.Text);
            Assert.Equal(Visibility.Visible, window.DueClearButton.Visibility);
            window.DueClearButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Null(task.Due);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void DuePicker_DefaultGesture_IsShiftT()
    {
        // 键位映射基线（合成输入无法注入修饰键，链路在此钉住；打开后的行为由其他用例覆盖）
        var entry = Keymap.Current.DefaultEntries.First(e => e.Command == AppCommand.OpenDuePicker);
        Assert.Equal(ModifierKeys.Shift, entry.Modifiers);
        Assert.Equal(Key.T, entry.Key);
        Assert.True(Keymap.IsTaskScoped(AppCommand.OpenDuePicker));   // 编辑框内让位
    }

    [Fact]
    public void DueMenuItem_ShowsShiftTGestureHint() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            UiTestHost.PumpUntil(() => UiTestHost.ContainerOf(window, task) != null, "容器生成");
            var card = VisualTreeEx.FindVisualChildren<Border>(UiTestHost.ContainerOf(window, task)!)
                .First(b => b.Name == "Card");
            var menu = card.ContextMenu;
            menu.IsOpen = true;   // 触发 Opened：按 Tag 刷新快捷键提示
            try
            {
                var dueItem = menu.Items.OfType<MenuItem>().First(m => m.Tag as string == "OpenDuePicker");
                Assert.Equal("Shift+T", dueItem.InputGestureText);
            }
            finally
            {
                menu.IsOpen = false;
            }
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void DuePicker_CalendarSelectionApplies() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });
            vm.SelectedTask = task;

            window.OpenDuePicker();   // Shift+T 入口（合成输入无法注入修饰键，直调打开方法）
            UiTestHost.PumpUntil(
                () => window.DatePickerPanel.Visibility == Visibility.Visible,
                "D 打开日期选择器");

            // 月历点选：找到目标日期格点击（程序化设 SelectedDate 是初始化路径，不触发应用）
            var picked = DateOnly.FromDateTime(DateTime.Today).AddDays(5);
            UiTestHost.PumpUntil(() => VisualTreeEx.FindVisualChildren<Button>(window.DueCalendar)
                .Any(b => b.DataContext is Views.WeekView.DateCell c && c.Date == picked), "日期格生成");
            VisualTreeEx.FindVisualChildren<Button>(window.DueCalendar)
                .First(b => b.DataContext is Views.WeekView.DateCell c && c.Date == picked)
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(picked, task.Due);
            Assert.Equal(Visibility.Collapsed, window.DatePickerPanel.Visibility);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void DuePicker_WeekWindow_FourWeeksFromCurrentWeek() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });
            vm.SelectedTask = task;

            window.OpenDuePicker();   // Shift+T 入口（合成输入无法注入修饰键，直调打开方法）
            UiTestHost.PumpUntil(
                () => window.DatePickerPanel.Visibility == Visibility.Visible,
                "D 打开日期选择器");

            var cal = window.DueCalendar;
            var today = DateOnly.FromDateTime(DateTime.Today);

            // 粗略视图：不显示年月，四星期窗口 = 27 日期格 + 末格内嵌 ›（翻页）；起点为本周首日（含今天）
            Assert.Equal(28, cal.Days.Count);
            Assert.Equal(27, cal.Days.Count(c => c.Pager == 0));
            Assert.Equal(1, cal.Days.Count(c => c.Pager == 1));
            Assert.Equal("日", cal.WeekdayNames[0]);   // 周列头跟随界面语言（中文），周日为首
            Assert.True(cal.Days[0].Date <= today && cal.Days[0].Date.AddDays(7) > today,
                "首页起点应在本周首曰");
            Assert.Contains(cal.Days, c => c.IsToday && c.Date == today);
            Assert.All(cal.Days.Where(c => c.Pager == 0 && c.Date < today), c => Assert.True(c.IsPast));   // 过去禁用

            // 翻页：› 向后三星期（步长 21：周首对齐且被替代日期在邻页可见）；翻页后首格为 ‹ 回翻
            var firstBefore = cal.Days[0].Date;
            cal.NextPage();
            Assert.Equal(-1, cal.Days[0].Pager);   // 首格变为回翻
            Assert.Equal(firstBefore.AddDays(22), cal.Days[1].Date);
        }
        finally
        {
            UiTestHost.CloseWindow(window);
        }
    });

    [Fact]
    public void DuePicker_EnterCommitsTypedDate() => OnUi(() =>
    {
        var window = WindowWithTasks(out var vm);
        try
        {
            var task = vm.Blocks[0].Tasks.First();
            vm.UpdateSelection(new[] { task });
            vm.SelectedTask = task;

            window.OpenDuePicker();   // Shift+T 入口（合成输入无法注入修饰键，直调打开方法）
            UiTestHost.PumpUntil(
                () => window.DatePickerPanel.Visibility == Visibility.Visible,
                "D 打开日期选择器");
            UiTestHost.PumpUntil(
                () => Keyboard.FocusedElement == window.DatePickerInput,
                "焦点锁进日期输入框");   // 聚焦是 BeginInvoke 异步迁移，需先等位再发键

            // 合成输入不产生文本输入事件，直接设值模拟手输；Enter 提交
            window.DatePickerInput.Text = "2026-08-18";
            UiTestHost.SendKey(window, Key.Enter);

            Assert.Equal(new DateOnly(2026, 8, 18), task.Due);
            Assert.Equal(Visibility.Collapsed, window.DatePickerPanel.Visibility);
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
