using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stanza.App.ViewModels;

namespace Stanza.App;

/// <summary>
/// 任务卡片的点击/双击/拖拽排序、+ 按钮点击与拖拽新建。
/// 拖拽是手写的鼠标状态机：按下记录 → 超过阈值进入拖拽 → 占位项实时预览落点 → 松开提交。
/// </summary>
public partial class MainWindow
{
    private const double DragThreshold = 7;   // 超过该位移才判定为拖拽

    // ---- 拖拽状态 ----
    private TaskViewModel? _downTask;      // 鼠标按下的任务（尚未判定为拖拽）
    private Point _downPos;                // 按下位置（相对窗口）
    private bool _taskDragging;            // 任务排序拖拽中
    private GapItem? _gap;                 // 位置预览占位
    private BlockViewModel? _dragBlock;    // 拖拽所在区块
    private int _originalIndex = -1;       // 任务拖拽前的索引
    private ScrollViewer? _taskScroll;

    private void InitializeDragInput()
    {
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ==================== 按下与判定 ====================

    private void TaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在输入框 / 按钮上时不启动拖拽（保留编辑与按钮行为）
        var source = e.OriginalSource as DependencyObject;
        if (VisualTreeEx.FindVisualAncestor<TextBoxBase>(source) != null
            || VisualTreeEx.FindVisualAncestor<ButtonBase>(source) != null)
            return;

        if (VisualTreeEx.FindVisualAncestor<ListBoxItem>(source)?.DataContext is not TaskViewModel task)
            return;

        // 点击其他任务：收起当前展开的详情面板（点自身、输入框、勾选框不受影响）
        if (VM.ExpandedTask != null && VM.ExpandedTask != task)
            VM.CollapseExpanded();

        _downTask = task;
        _downPos = e.GetPosition(this);

        // Things 3 交互：单击只选中（高亮），快速双击才展开详情
        if (e.ClickCount == 2)
        {
            VM.ExpandTask(task);
            FocusTaskTitle(task);
        }
    }

    /// <summary>展开任务后把输入光标定位到标题编辑框末尾（Things 3 交互）。</summary>
    private void FocusTaskTitle(TaskViewModel task)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var container = TaskList.ItemContainerGenerator.ContainerFromItem(task) as DependencyObject;
            var box = container == null
                ? null
                : VisualTreeEx.FindVisualChildren<TextBox>(container).FirstOrDefault();
            if (box == null) return;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }));
    }

    private void TaskList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右键：未选中的项先单选它（已选中的项保留多选），再交给卡片上的 ContextMenu
        var source = e.OriginalSource as DependencyObject;
        if (VisualTreeEx.FindVisualAncestor<ListBoxItem>(source)?.DataContext is TaskViewModel task)
        {
            if (!VM.SelectedTasks.Contains(task))
                TaskList.SelectedItem = task;
            if (VM.ExpandedTask != null && VM.ExpandedTask != task)
                VM.CollapseExpanded();
        }
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => VM.UpdateSelection(TaskList.SelectedItems.Cast<TaskViewModel>().ToList());

    // ==================== 移动与松开 ====================

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_taskDragging)
        {
            // 拖到侧栏某个区块上：切换目标区块，占位随之迁移过去
            var hoverBlock = BlockUnderMouse(e);
            if (hoverBlock != null && hoverBlock != _dragBlock)
            {
                _dragBlock!.Items.Remove(_gap!);
                _dragBlock = hoverBlock;
                hoverBlock.Items.Add(_gap!);
                VM.SelectedBlock = hoverBlock;
            }
            MoveGapToMouse(e);
            UpdateGhostPosition(e);
            AutoScroll(e);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetPressState();
            return;
        }

        if (_downTask != null
            && (e.GetPosition(this) - _downPos).Length > DragThreshold)
        {
            StartTaskDrag();
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_taskDragging)
        {
            CommitTaskDrag();
            return;
        }

        _downTask = null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_taskDragging) { CancelTaskDrag(); e.Handled = true; }
            else
            {
                ResetPressState();
                if (VM.ExpandedTask != null || VM.SelectedTask != null)
                {
                    VM.CollapseExpanded();
                    VM.SelectedTask = null;
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
            }
            return;
        }

        // 回车展开当前选中任务（焦点在列表上时）
        if (e.Key == Key.Enter && VM.SelectedTask != null && VM.ExpandedTask == null)
        {
            VM.ExpandTask(VM.SelectedTask);
            FocusTaskTitle(VM.SelectedTask);
            e.Handled = true;
        }
    }

    private void ResetPressState()
    {
        _downTask = null;
    }

    // ==================== 任务拖拽 ====================

    private void StartTaskDrag()
    {
        var task = _downTask!;
        _downTask = null;
        _taskDragging = true;
        Mouse.Capture(this);

        _dragBlock = VM.Blocks.First(b => b.Items.Contains(task));
        _originalIndex = _dragBlock.Items.IndexOf(task);

        // 拖拽前收起展开态，让幽灵与占位保持紧凑
        if (VM.ExpandedTask == task) VM.CollapseExpanded();
        VM.SelectedTask = null;

        var container = TaskList.ItemContainerGenerator.ContainerFromItem(task) as FrameworkElement;
        GhostContent.Content = task;
        GhostContent.ContentTemplate = (DataTemplate)FindResource("TaskGhostTemplate");
        GhostCard.Width = container?.ActualWidth ?? 420;

        _gap = new GapItem { Height = 38 };
        _dragBlock.Items.Remove(task);
        _dragBlock.Items.Insert(Math.Clamp(_originalIndex, 0, _dragBlock.Items.Count), _gap);

        GhostCanvas.Visibility = Visibility.Visible;
    }

    private void CommitTaskDrag()
    {
        var task = (TaskViewModel)GhostContent.Content;
        var block = _dragBlock!;
        var list = block.Items;
        var gapIndex = list.IndexOf(_gap!);
        // 占位异常丢失时回退到拖拽前位置，避免任务被挪到列表开头
        if (gapIndex < 0) gapIndex = Math.Clamp(_originalIndex, 0, list.Count);
        list.Remove(_gap!);

        var insertAt = Math.Clamp(gapIndex, 0, list.Count);
        EndDragVisuals();
        VM.DropTask(task, block, insertAt);
    }

    private void CancelTaskDrag()
    {
        var task = (TaskViewModel)GhostContent.Content;
        var list = _dragBlock!.Items;
        list.Remove(_gap!);
        list.Insert(Math.Clamp(_originalIndex, 0, list.Count), task);
        EndDragVisuals();
    }

    // ==================== 拖拽共用 ====================

    private void EndDragVisuals()
    {
        RemoveGap();
        _taskDragging = false;
        _downTask = null;
        _dragBlock = null;
        _originalIndex = -1;
        GhostContent.Content = null;
        GhostCanvas.Visibility = Visibility.Hidden;
        Mouse.Capture(null);
    }

    private void RemoveGap()
    {
        if (_gap != null && _dragBlock != null && _dragBlock.Items.Contains(_gap))
            _dragBlock.Items.Remove(_gap);
        _gap = null;
    }

    private BlockViewModel? BlockUnderMouse(MouseEventArgs e)
    {
        for (var i = 0; i < BlockList.Items.Count; i++)
        {
            if (BlockList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c)
                continue;
            var p = e.GetPosition(c);
            if (p.X >= 0 && p.Y >= 0 && p.X <= c.ActualWidth && p.Y <= c.ActualHeight)
                return BlockList.Items[i] as BlockViewModel;
        }
        return null;
    }

    /// <summary>根据鼠标位置移动占位项；其他任务随布局自动避让。</summary>
    private void MoveGapToMouse(MouseEventArgs e)
    {
        if (_gap == null || _dragBlock == null) return;
        var list = _dragBlock.Items;
        if (!list.Contains(_gap)) return;

        var mouseY = e.GetPosition(TaskList).Y;

        // 目标位置 = 非占位项序列中，第一个垂直中线低于鼠标的项之前
        var insertAt = -1;
        var nonGapSeen = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is GapItem) continue;
            if (TaskList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c)
            {
                var top = c.TranslatePoint(new Point(0, 0), TaskList).Y;
                if (mouseY < top + c.ActualHeight / 2)
                {
                    insertAt = nonGapSeen;
                    break;
                }
            }
            nonGapSeen++;
        }
        if (insertAt < 0) insertAt = nonGapSeen;   // 末尾

        // 占位项当前在“非占位序列”中的位置
        var gapIndex = list.IndexOf(_gap);
        var gapPos = 0;
        for (var i = 0; i < gapIndex; i++)
            if (list[i] is not GapItem) gapPos++;

        if (insertAt == gapPos) return;

        list.Remove(_gap);
        list.Insert(Math.Clamp(insertAt, 0, list.Count), _gap);
    }

    private void UpdateGhostPosition(MouseEventArgs e)
    {
        var p = e.GetPosition(GhostCanvas);
        Canvas.SetLeft(GhostCard, p.X - 24);
        Canvas.SetTop(GhostCard, p.Y - 16);
    }

    private void AutoScroll(MouseEventArgs e)
    {
        _taskScroll ??= VisualTreeEx.FindVisualChildren<ScrollViewer>(TaskList).FirstOrDefault();
        if (_taskScroll == null) return;

        var y = e.GetPosition(_taskScroll).Y;
        if (y < 36) _taskScroll.ScrollToVerticalOffset(_taskScroll.VerticalOffset - 16);
        else if (y > _taskScroll.ViewportHeight - 36) _taskScroll.ScrollToVerticalOffset(_taskScroll.VerticalOffset + 16);
    }

    // ==================== 新建任务：滚动 + 聚焦 ====================

    private void OnTaskCreated(object? sender, TaskViewModel task)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            TaskList.ScrollIntoView(task);
            TaskList.UpdateLayout();
            var container = TaskList.ItemContainerGenerator.ContainerFromItem(task) as DependencyObject;
            var box = container == null ? null : VisualTreeEx.FindVisualChildren<TextBox>(container).FirstOrDefault();
            box?.Focus();
        }));
    }
}
