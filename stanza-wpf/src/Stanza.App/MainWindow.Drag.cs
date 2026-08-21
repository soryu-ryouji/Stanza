using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stanza.App.ViewModels;

namespace Stanza.App;

/// <summary>
/// 任务卡片的点击/双击、拖拽排序与拖拽新建。拖拽是手写的鼠标状态机：
/// 按下记录 → 超过阈值进入拖拽 → 占位项实时预览落点（区块模式悬停侧栏切换目标区块，
/// 面板模式按分段决定目标状态）→ 松开提交。键盘分发与焦点管理见 MainWindow.Keyboard.cs。
/// </summary>
public partial class MainWindow
{
    private const double DragThreshold = 7;   // 超过该位移才判定为拖拽

    // ---- 拖拽状态 ----
    private TaskViewModel? _downTask;      // 鼠标按下的任务（尚未判定为拖拽）
    private Point _downPos;                // 按下位置（相对窗口）
    private bool _taskDragging;            // 任务排序拖拽中
    private GapItem? _gap;                 // 位置预览占位
    private IList<object>? _dragList;      // 占位项所在列表：区块任务集或面板任务集
    private BlockViewModel? _dragBlock;    // 区块模式：拖拽所在区块（侧栏悬停切换）
    private bool _dragInPanel;             // 面板模式：在分段面板内拖拽
    private BlockViewModel? _dragSourceBlock; // 面板模式：任务所属区块（取消时归还）
    private int _sourceIndexInBlock = -1;  // 面板模式：任务在所属区块中的原索引
    private int _originalIndex = -1;       // 任务拖拽前的索引
    private ScrollViewer? _taskScroll;

    // ==================== 按下与判定 ====================

    internal void TaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在输入框 / 按钮上时不启动拖拽（保留编辑与按钮行为）
        var source = e.OriginalSource as DependencyObject;
        if (VisualTreeEx.FindVisualAncestor<TextBoxBase>(source) != null
            || VisualTreeEx.FindVisualAncestor<ButtonBase>(source) != null)
            return;

        if (VisualTreeEx.FindVisualAncestor<ListBoxItem>(source)?.DataContext is not TaskViewModel task)
            return;

        // 点击其他任务：收起当前展开的详情面板（点自身、输入框、勾选框不受影响；空草稿随之移除）
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

    /// <summary>任务的条目容器（兼容面板分组视图：直接查视觉树，不依赖 ItemContainerGenerator 的分组行为）。</summary>
    private ListBoxItem? ContainerOf(object item)
        => VisualTreeEx.FindVisualChildren<ListBoxItem>(TaskList)
            .FirstOrDefault(c => ReferenceEquals(c.DataContext, item));

    /// <summary>展开任务后把输入光标定位到标题编辑框末尾（Things 3 交互）。</summary>
    private void FocusTaskTitle(TaskViewModel task)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var container = ContainerOf(task);
            if (container == null) return;
            var box = VisualTreeEx.FindVisualChildren<TextBox>(container).FirstOrDefault();
            if (box == null) return;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }));
    }

    internal void TaskList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
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

    // 列表可能混有拖拽占位项（GapItem），用 OfType 过滤而非 Cast
    internal void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => VM.UpdateSelection(TaskList.SelectedItems.OfType<TaskViewModel>().ToList());

    // ==================== 移动与松开 ====================

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_taskDragging)
        {
            if (_dragInPanel)
            {
                // 面板模式：在分段面板内移动占位（落点分段决定目标状态与块内位置）
                MoveGapInPanel(e);
            }
            else
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
            }
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

    // ==================== 任务拖拽 ====================

    private void StartTaskDrag()
    {
        VM.PushUndoSnapshot();   // 拖拽从这里开始变更集合：快照必须是拖拽前状态，而非提交时
        var task = _downTask!;
        _downTask = null;
        _taskDragging = true;
        Mouse.Capture(this);

        _dragInPanel = VM.SelectedFacet != null;
        _dragSourceBlock = VM.Blocks.First(b => b.Items.Contains(task));
        _sourceIndexInBlock = _dragSourceBlock.Items.IndexOf(task);
        _dragList = _dragInPanel ? VM.PanelItems : _dragSourceBlock.Items;
        _dragBlock = _dragInPanel ? null : _dragSourceBlock;
        _originalIndex = _dragList.IndexOf(task);

        // 拖拽前收起展开态，让幽灵与占位保持紧凑。空草稿不收起：
        // 收起会将其移除，拖拽又会把它插回，行为怪异
        if (VM.ExpandedTask == task && !task.IsEmpty) VM.CollapseExpanded();
        VM.SelectedTask = null;

        var container = ContainerOf(task);
        GhostContent.Content = task;
        GhostContent.ContentTemplate = (DataTemplate)FindResource("TaskGhostTemplate");
        GhostCard.Width = container?.ActualWidth ?? 420;

        _gap = new GapItem { Height = 38, State = task.State };
        // 面板模式：任务先从所属区块移除（同一时刻只属于一个集合），占位项进入面板列表
        if (_dragInPanel) _dragSourceBlock.RemoveTask(task);
        _dragList.Remove(task);
        _dragList.Insert(Math.Clamp(_originalIndex, 0, _dragList.Count), _gap);

        GhostCanvas.Visibility = Visibility.Visible;
    }

    private void CommitTaskDrag()
    {
        var task = (TaskViewModel)GhostContent.Content;

        if (_dragInPanel)
        {
            // 落点 = 占位项所在分段：目标区块为其状态，
            // 块内索引为占位之前同状态任务数（面板内顺序与区块一致）
            var targetState = _gap!.State;
            var gapIndex = _dragList!.IndexOf(_gap);
            var indexInBlock = 0;
            for (var i = 0; i < gapIndex; i++)
                if (_dragList[i] is TaskViewModel t && t.State == targetState)
                    indexInBlock++;
            _dragList.Remove(_gap);
            var target = VM.Blocks.First(b => b.State == targetState);
            EndDragVisuals();
            VM.DropTask(task, target, indexInBlock);
            return;
        }

        var block = _dragBlock!;
        var list = block.Items;
        var gapIdx = list.IndexOf(_gap!);
        // 占位异常丢失时回退到拖拽前位置，避免任务被挪到列表开头
        if (gapIdx < 0) gapIdx = Math.Clamp(_originalIndex, 0, list.Count);
        list.Remove(_gap!);

        var insertAt = Math.Clamp(gapIdx, 0, list.Count);
        EndDragVisuals();
        VM.DropTask(task, block, insertAt);
    }

    private void CancelTaskDrag()
    {
        var task = (TaskViewModel)GhostContent.Content;
        var list = _dragList!;
        list.Remove(_gap!);
        list.Insert(Math.Clamp(_originalIndex, 0, list.Count), task);
        // 面板模式：任务还需归还所属区块（拖拽开始时会从中移除）
        if (_dragInPanel)
            _dragSourceBlock!.InsertTask(_sourceIndexInBlock, task);
        EndDragVisuals();
    }

    // ==================== 拖拽共用 ====================

    private void EndDragVisuals()
    {
        RemoveGap();
        _taskDragging = false;
        _downTask = null;
        _dragList = null;
        _dragBlock = null;
        _dragSourceBlock = null;
        _dragInPanel = false;
        _originalIndex = -1;
        _sourceIndexInBlock = -1;
        GhostContent.Content = null;
        GhostCanvas.Visibility = Visibility.Hidden;
        Mouse.Capture(null);
    }

    private void RemoveGap()
    {
        if (_gap != null && _dragList != null && _dragList.Contains(_gap))
            _dragList.Remove(_gap);
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

    /// <summary>面板模式的占位移动：按鼠标所在分段决定目标状态，按分段内位置决定块内落点。</summary>
    private void MoveGapInPanel(MouseEventArgs e)
    {
        if (_gap == null || _dragList == null || !_dragList.Contains(_gap)) return;
        var list = _dragList;
        var mouseY = e.GetPosition(TaskList).Y;

        // 鼠标所在分段；在最下方空白时归入最后一个分段
        var groups = VisualTreeEx.FindVisualChildren<GroupItem>(TaskList)
            .OrderBy(g => g.TranslatePoint(new Point(0, 0), TaskList).Y)
            .ToList();
        if (groups.Count == 0) return;
        var targetGroup = groups.FirstOrDefault(g =>
            mouseY < g.TranslatePoint(new Point(0, 0), TaskList).Y + g.ActualHeight) ?? groups[^1];

        // 分段状态：取组内任一任务的状态；组内只剩占位项时保持占位项当前状态
        var containers = VisualTreeEx.FindVisualChildren<ListBoxItem>(targetGroup)
            .OrderBy(c => c.TranslatePoint(new Point(0, 0), TaskList).Y)
            .ToList();
        var targetState = containers.Select(c => c.DataContext).OfType<TaskViewModel>()
            .FirstOrDefault()?.State ?? _gap.State;

        // 组内插入位置：第一个垂直中线低于鼠标的任务之前
        var posInGroup = 0;
        foreach (var c in containers)
        {
            if (c.DataContext is not TaskViewModel) continue;
            var top = c.TranslatePoint(new Point(0, 0), TaskList).Y;
            if (mouseY < top + c.ActualHeight / 2) break;
            posInGroup++;
        }

        // 换算为面板任务序列（不含占位项）中的位置；面板内同状态任务连续排列
        var tasks = list.OfType<TaskViewModel>().ToList();
        if (!tasks.Any(t => t.State == targetState)) return;   // 组内没有任务：位置不变
        var insertPos = tasks.Count;
        var seen = 0;
        for (var i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].State != targetState) continue;
            if (seen == posInGroup) { insertPos = i; break; }
            seen++;
        }

        // 占位项当前在任务序列中的位置
        var gapPos = list.Take(list.IndexOf(_gap)).OfType<TaskViewModel>().Count();
        if (insertPos == gapPos && _gap.State == targetState) return;

        _gap.State = targetState;
        list.Remove(_gap);
        list.Insert(Math.Clamp(insertPos, 0, list.Count), _gap);
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
        // 等首帧渲染布局完成（新容器排版、展开终态就位）后再滚动与聚焦：
        // Loaded 优先级早于本次布局，此时 ScrollIntoView/聚焦会基于未排版的几何计算，
        // 可能把内容滚到错误位置（表现为新卡片位置异常，切换区块重排后才恢复）
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            // 区块视图：任务不在当前区块时不处理；面板视图：面板已含该任务（创建时已预填项目/标签）
            if (VM.SelectedFacet == null
                && (VM.SelectedBlock == null || !VM.SelectedBlock.Items.Contains(task))) return;
            TaskList.ScrollIntoView(task);
            TaskList.UpdateLayout();
            FocusTaskTitle(task);
        }));
    }
}
