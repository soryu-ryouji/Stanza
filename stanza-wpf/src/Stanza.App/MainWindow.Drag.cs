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
    private IList<object>? _dragList;      // 占位项所在列表：区块任务集或面板任务集
    private BlockViewModel? _dragBlock;    // 区块模式：拖拽所在区块（侧栏悬停切换）
    private bool _dragInPanel;             // 面板模式：在分段面板内拖拽
    private BlockViewModel? _dragSourceBlock; // 面板模式：任务所属区块（取消时归还）
    private int _sourceIndexInBlock = -1;  // 面板模式：任务在所属区块中的原索引
    private int _originalIndex = -1;       // 任务拖拽前的索引
    private ScrollViewer? _taskScroll;

    private void InitializeDragInput()
    {
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        KeyDown += OnKeyDown;
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

    // 列表可能混有拖拽占位项（GapItem），用 OfType 过滤而非 Cast
    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    /// <summary>模态浮层打开中（VS Code when 上下文的对应物）：应用快捷键让位给浮层自身的按键处理。</summary>
    private bool ModalOverlayOpen
        => SettingsOverlay.Visibility == Visibility.Visible
           || ExitOverlay.Visibility == Visibility.Visible;

    // 应用级按键在路由前分发（命令优先，VS 语义）：与键盘焦点无关，窗口激活即生效——
    // 无焦点元素时按键根本不产生路由事件，挂在路由事件上的分发收不到。
    // 模态浮层打开时跳过：浮层的按键过滤/键位录制靠路由事件实现，不能绕过；
    // 非模态的 FacetPicker 不拦截，按键保持穿透（与路由时代行为一致）
    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (ModalOverlayOpen) return;
        if (e.StagingItem.Input is not KeyEventArgs k) return;

        // Ctrl+R 快速切换的确认：弹层内有高亮行时，松开 Ctrl 即打开该行（VS Code quick-open 语义）
        if (k.RoutedEvent == Keyboard.KeyUpEvent)
        {
            // 循环修饰键随键盘模式：Windows = Ctrl，macOS = Alt（扮演 Command）
            var cycleReleased = Keymap.Current.MacOsMode
                ? k.Key is Key.LeftAlt or Key.RightAlt
                : k.Key is Key.LeftCtrl or Key.RightCtrl;
            if (_recentCycleIndex >= 0 && RecentPopup.IsOpen && cycleReleased)
            {
                var path = VM.Recents.Items[_recentCycleIndex].Path;
                RecentPopup.IsOpen = false;   // Closed 事件里复位循环索引
                if (VM.Recents.OpenCommand.CanExecute(path)) VM.Recents.OpenCommand.Execute(path);
                e.Cancel();
            }
            return;
        }
        if (k.RoutedEvent != Keyboard.KeyDownEvent) return;

        // 应用级快捷键：查键位表分发到命令（Keymap.cs）。Alt 组合的主键在 SystemKey 上（同 CaptureGesture）
        var key = k.Key == Key.System ? k.SystemKey : k.Key;

        // 文本框内的 Emacs 编辑手势（TextEditKeys）是文本编辑语义：键位表中的应用命令
        // （如默认 Ctrl+N 新建任务）在编辑框内让路，与下方 Ctrl+Z 文本级撤销同一先例
        if (Keyboard.FocusedElement is TextBoxBase && TextEditKeys.IsEditingGesture(Keyboard.Modifiers, key))
            return;

        if (Keymap.Current.Resolve(key, Keyboard.Modifiers) is { } entry
            && VM.CommandFor(entry.Command) is { } command
            && command.CanExecute(entry.Parameter))
        {
            // 编辑框内的 Ctrl+Z 是文本级撤销（WPF 内建）：不消费，交给路由
            if (entry.Command == AppCommand.Undo
                && (Keyboard.FocusedElement is TextBoxBase || _taskDragging))
                return;   // 拖拽中不撤销：拖拽持有区块/占位项引用，全量重建会使其失效
            command.Execute(entry.Parameter);
            e.Cancel();   // 已消费，不再进入路由
            return;
        }

        // 任务作用域快捷键：同一张键位表，命中任务命令后按命令检查焦点作用域再执行。
        // 用户改键只改触发手势；作用域语义（编辑框内输入、浮层内的按键优先）不随之变化
        if (Keymap.Current.Resolve(key, Keyboard.Modifiers) is { } taskEntry
            && Keymap.IsTaskScoped(taskEntry.Command)
            && TryExecuteTaskCommand(taskEntry.Command, key))
        {
            e.Cancel();
            return;
        }

        // Shift+jk（vim 语义）扩展选中：字母键没有原生路由可借力，全程自处理（锚点+活动端）
        if (key is Key.J or Key.K && Keyboard.Modifiers == ModifierKeys.Shift
            && TryShiftSelectTasks(key))
        {
            e.Cancel();
            return;
        }

        // Shift+方向键扩展选中：焦点在任务条目容器上时由 WPF 原生扩展（Extended 模式）；
        // 焦点落空时（停在列表本体——关闭浮层后等）先把焦点放回选中边缘的容器，按键继续走原生路由
        if (ShiftArrowNeedsBridge(key))
            BridgeShiftArrow(key);
    }

    private TaskViewModel? _shiftAnchor;   // Shift+jk 扩展选中的锚点（区间固定端）
    private TaskViewModel? _shiftCursor;   // 活动端（随 Shift+jk 移动）

    /// <summary>Shift+jk 扩展批量选中：单选为锚点，按方向移动活动端，选中锚点..活动端区间；
    /// 活动端移回锚点即收缩。无选中时与裸 j/k 一致（j 选首项、k 选末项）。</summary>
    private bool TryShiftSelectTasks(Key key)
    {
        // 作用域同任务导航键：文本框（文本选择）、浮层、侧栏列表内不接管
        if (Keyboard.FocusedElement is DependencyObject focus)
        {
            if (focus is TextBoxBase) return false;
            if (VisualTreeEx.IsWithin(focus, FacetPickerLayer)) return false;
            if (focus is ListBox list && !ReferenceEquals(list, TaskList)) return false;
        }
        var down = key == Key.J;
        var tasks = TaskList.Items.OfType<TaskViewModel>().ToList();
        if (tasks.Count == 0) return true;   // 空列表：吞掉，避免焦点逃逸到工具栏
        var selected = VM.SelectedTasks.Select(t => tasks.IndexOf(t)).Where(i => i >= 0).ToList();
        if (selected.Count == 0)
        {
            var first = down ? tasks[0] : tasks[^1];
            VM.SelectedTask = first;
            FocusContainerOf(first);
            return true;
        }

        // 锚点/活动端已失效（点击、流转等改变了选中）：按当前选中重建——
        // 单选时两端同点；多选（鼠标圈选）取方向近端为锚，避免收缩掉既有区间
        var anchor = _shiftAnchor is { } a ? tasks.IndexOf(a) : -1;
        var cursor = _shiftCursor is { } c ? tasks.IndexOf(c) : -1;
        if (anchor < 0 || cursor < 0 || !selected.Contains(anchor) || !selected.Contains(cursor))
        {
            if (selected.Count == 1) { anchor = cursor = selected[0]; }
            else if (down) { anchor = selected.Min(); cursor = selected.Max(); }
            else { anchor = selected.Max(); cursor = selected.Min(); }
        }
        cursor = Math.Clamp(cursor + (down ? 1 : -1), 0, tasks.Count - 1);

        TaskList.SelectedItems.Clear();
        foreach (var i in Enumerable.Range(Math.Min(anchor, cursor), Math.Abs(cursor - anchor) + 1))
            TaskList.SelectedItems.Add(tasks[i]);
        _shiftAnchor = tasks[anchor];
        _shiftCursor = tasks[cursor];
        FocusContainerOf(tasks[cursor]);
        return true;
    }

    /// <summary>Shift+方向键是否需要借桥：焦点不在文本框（文本选择）、不在浮层、不在任务条目容器
    /// （原生扩展可用）、不在侧栏列表——即焦点落空（列表本体/按钮/窗口/已隐藏元素）时。</summary>
    private bool ShiftArrowNeedsBridge(Key key)
    {
        if (key is not (Key.Up or Key.Down or Key.Left or Key.Right)
            || Keyboard.Modifiers != ModifierKeys.Shift
            || RecentPopup.IsOpen)
            return false;
        if (Keyboard.FocusedElement is not DependencyObject focus) return true;
        if (focus is UIElement { IsVisible: false }) return true;
        if (focus is TextBoxBase) return false;
        if (VisualTreeEx.IsWithin(focus, FacetPickerLayer)) return false;
        if (VisualTreeEx.FindVisualAncestor<ListBoxItem>(focus) is { } item
            && VisualTreeEx.IsWithin(item, TaskList))
            return false;
        if (focus is ListBox list && !ReferenceEquals(list, TaskList)) return false;
        return true;
    }

    /// <summary>把焦点放回选中边缘（方向同侧）的条目容器；不消费按键——焦点从列表外进入不联动选中，
    /// 随后的原生路由由 ListBox 从该容器扩展选中（WPF Extended 模式接管锚点）。</summary>
    private void BridgeShiftArrow(Key key)
    {
        var tasks = TaskList.Items.OfType<TaskViewModel>().ToList();
        if (tasks.Count == 0) return;
        var forward = key is Key.Down or Key.Right;
        var indices = VM.SelectedTasks
            .Select(t => tasks.IndexOf(t))
            .Where(i => i >= 0)
            .ToList();
        var target = indices.Count == 0
            ? (forward ? tasks[0] : tasks[^1])
            : tasks[forward ? indices.Max() : indices.Min()];
        ((UIElement?)ContainerOf(target) ?? TaskList).Focus();
    }

    /// <summary>任务作用域命令的执行（含焦点作用域检查）。返回 false 表示当前上下文不分发，
    /// 按键继续走默认路由（如编辑框内的字母输入）。</summary>
    private bool TryExecuteTaskCommand(AppCommand command, Key key)
    {
        var focus = Keyboard.FocusedElement as DependencyObject;
        switch (command)
        {
            // 标记选中任务已完成（§9）。限任务列表焦点：编辑框内 Space 是输入、按钮上 Space 是激活
            case AppCommand.CompleteTask:
                if (RecentPopup.IsOpen || focus is null or TextBoxBase
                    || !VisualTreeEx.IsWithin(focus, TaskList)
                    || !VM.ScopeIsActive
                    || !VM.CompleteSelectionCommand.CanExecute(null))
                    return false;
                AnimateCompleteTasks(VM.SelectedTasks.ToList());
                return true;

            // 打开标签/项目选择器：编辑框内字母是输入，不拦截
            case AppCommand.OpenTagPicker:
            case AppCommand.OpenProjectPicker:
                if (RecentPopup.IsOpen || focus is null or TextBoxBase
                    || !VisualTreeEx.IsWithin(focus, TaskList)
                    || !VM.HasSelection)
                    return false;
                OpenFacetPicker(command == AppCommand.OpenTagPicker ? FacetKind.Tag : FacetKind.Project,
                    SelectedTaskAnchor());
                return true;

            // 移入 DELETE（回收站语义，§9）/ 彻底删除。编辑框内是删字、选择器面板内被面板自身吞掉
            case AppCommand.DiscardTask:
            case AppCommand.DeleteTask:
                if (focus is TextBoxBase
                    || focus != null && VisualTreeEx.IsWithin(focus, FacetPickerLayer)
                    || !VM.HasSelection)
                    return false;
                var deleteCommand = command == AppCommand.DiscardTask
                    ? VM.DiscardSelectionCommand
                    : VM.DeleteSelectionCommand;
                if (!deleteCommand.CanExecute(null)) return false;
                var index = FirstSelectedIndex();
                deleteCommand.Execute(null);
                FocusTaskAtIndex(index);
                return true;

            // 裸导航键：方向键与 vim hjkl 同语义映射。焦点无人消费时引入任务列表并移动选中；
            // 字母绑定没有 ListBox 默认导航可借力，选中条目聚焦时（方向键让位给默认导航的状态）也接管
            case AppCommand.NavigateUp or AppCommand.NavigateDown
                or AppCommand.NavigateLeft or AppCommand.NavigateRight:
                if (RecentPopup.IsOpen) return false;
                var isCharKey = key is >= Key.A and <= Key.Z;
                if (!NavKeysDeadOnFocus && !(isCharKey && FocusedTaskChrome)) return false;
                FocusTaskForArrow(command switch
                {
                    AppCommand.NavigateUp => Key.Up,
                    AppCommand.NavigateDown => Key.Down,
                    AppCommand.NavigateLeft => Key.Left,
                    _ => Key.Right,
                });
                return true;

            default:
                return false;
        }
    }

    /// <summary>裸导航键（方向键/hjkl）当前无人消费：无焦点；焦点残留在已隐藏/移除的元素上
    /// （WPF 不自动迁移焦点，按键仍会路由给它）；焦点在按钮/窗口/任务列表框本体上；
    /// 或焦点在任务列表的未选中条目上（启动/切区块时预置的「只聚焦不选中」状态，见 MainWindow 构造函数）。
    /// 编辑框（光标移动）、选中条目（默认方向导航接管）、侧栏列表、浮层内的焦点不接管。</summary>
    private bool NavKeysDeadOnFocus
    {
        get
        {
            if (Keyboard.FocusedElement is not DependencyObject focus) return true;
            if (focus is UIElement { IsVisible: false }) return true;
            if (focus is TextBoxBase) return false;
            if (VisualTreeEx.IsWithin(focus, FacetPickerLayer)) return false;
            if (VisualTreeEx.FindVisualAncestor<ListBoxItem>(focus) is { } item)
                return VisualTreeEx.IsWithin(focus, TaskList) && !item.IsSelected;
            if (focus is ListBox list && !ReferenceEquals(list, TaskList)) return false;
            return true;
        }
    }

    /// <summary>方向键定位任务列表选中：有选中时 Up/Down 相对当前项移动（Left/Right 只归位焦点）；
    /// 无选中但焦点已在某条目上时选中该焦点项；都没有时 Down/Right 选首项、Up/Left 选末项。</summary>
    private void FocusTaskForArrow(Key key)
    {
        var tasks = TaskList.Items.OfType<TaskViewModel>().ToList();
        if (tasks.Count == 0)
        {
            (TaskList as UIElement).Focus();
            return;
        }

        var i = VM.SelectedTask is { } current ? tasks.IndexOf(current) : -1;
        TaskViewModel target;
        if (i >= 0)
        {
            target = key switch
            {
                Key.Down => tasks[Math.Min(i + 1, tasks.Count - 1)],
                Key.Up => tasks[Math.Max(i - 1, 0)],
                _ => tasks[i],   // Left/Right 不移动
            };
        }
        else if (FocusedTask() is { } anchored && tasks.Contains(anchored))
        {
            target = anchored;   // 先让隐形的预置焦点成为选中，后续按键再走默认导航
        }
        else
        {
            target = key is Key.Up or Key.Left ? tasks[^1] : tasks[0];
        }
        // 显式选中：WPF 里焦点从列表外进入时不联动选中（Tab 进 ListBox 只聚焦不选中），
        // 只有列表内的焦点迁移选中才跟随。先选中再聚焦，入位后后续方向键走默认导航，选中继续跟随
        VM.SelectedTask = target;
        FocusContainerOf(target);
    }

    /// <summary>当前焦点所在条目的任务（焦点不在任务列表条目内时返回 null）。</summary>
    private TaskViewModel? FocusedTask()
        => Keyboard.FocusedElement is DependencyObject focus && VisualTreeEx.IsWithin(focus, TaskList)
            ? VisualTreeEx.FindVisualAncestor<ListBoxItem>(focus)?.DataContext as TaskViewModel
            : null;

    /// <summary>键盘打开选择器时的锚点：选中任务卡片的右上角。容器不可用（滚动外未生成）时
    /// 返回 null，由 OpenFacetPicker 回退到鼠标位置。</summary>
    private Point? SelectedTaskAnchor()
    {
        if (VM.SelectedTask is not { } task) return null;
        var container = ContainerOf(task);
        return container?.TranslatePoint(new Point(container.ActualWidth, 0), Root);
    }

    /// <summary>焦点在任务列表的条目容器上（勾选框等也可），但不在文本框内：
    /// hjkl 导航的附加作用域——编辑框输入字母优先，不参与导航。</summary>
    private bool FocusedTaskChrome
        => Keyboard.FocusedElement is not TextBoxBase && FocusedTask() != null;
    // 语义键（焦点相关，控件级按键）：挂在窗口 KeyDown（冒泡方向），焦点控件先处理，
    // 这里只收到无人认领的键——编辑框消化的键（多行框的 Enter）、按钮的 Enter/Space、
    // 浮层自行处理并 Handled 的键都不会到达这里，无需再按可见性/来源做特判。
    // Esc/Enter 上下文多义（退出编辑/取消选择 vs 展开/提交），不进键位表
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 最近文件弹层打开中：Esc 关闭不打开（键盘循环高亮随弹层关闭复位）
            if (RecentPopup.IsOpen)
            {
                var refocus = _recentCycleIndex >= 0;
                RecentPopup.IsOpen = false;
                // 循环模式下行按钮持有焦点，弹层关闭后焦点落空，停回任务列表
                if (refocus) ParkFocusOnTaskList();
                e.Handled = true;
                return;
            }

            if (_taskDragging) { CancelTaskDrag(); e.Handled = true; }
            else
            {
                ResetPressState();
                if (VM.ExpandedTask != null || VM.SelectedTask != null)
                {
                    // Esc 退出编辑（空草稿随之移除）
                    VM.CollapseExpanded();
                    VM.SelectedTask = null;
                    ParkFocusOnTaskList();
                    e.Handled = true;
                }
            }
            return;
        }

        if (e.Key == Key.Enter)
        {
            // 多行备注框的 Enter 是换行、按钮上的 Enter 是激活——都被控件消化，到不了这里
            if (VM.ExpandedTask != null)
            {
                CommitExpandedEdit();
                e.Handled = true;
                return;
            }

            // 回车展开当前选中任务（焦点在列表上时）
            if (VM.SelectedTask != null)
            {
                VM.ExpandTask(VM.SelectedTask);
                FocusTaskTitle(VM.SelectedTask);
                e.Handled = true;
            }
            return;
        }
    }

    /// <summary>标题编辑框按键（模板转发）：Tab 固定移交给备注编辑框——不走默认遍历
    /// （可能把焦点带出卡片）。Shift+Tab 保持默认。</summary>
    internal void HandleTaskTitleKey(TextBox box, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None) return;
        var item = VisualTreeEx.FindVisualAncestor<ListBoxItem>(box);
        var notes = item == null
            ? null
            : VisualTreeEx.FindVisualChildren<TextBox>(item).FirstOrDefault(t => t.AcceptsReturn);
        if (notes == null) return;
        notes.Focus();
        notes.CaretIndex = notes.Text.Length;
        e.Handled = true;
    }

    /// <summary>备注编辑框按键（模板转发）：Ctrl+Enter 提交任务（与标题框 Enter 一致）；
    /// 裸 Enter 换行——行首是列表记号时自动续接/退出（NotesListEditing），其余交给编辑框自身。</summary>
    internal void HandleTaskNotesKey(TextBox box, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;   // 隧道阶段拦截，编辑框不会插入换行
            CommitExpandedEdit();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None)
            e.Handled = NotesListEditing.TryHandleEnter(box);
    }

    /// <summary>确认当前展开的编辑：收起详情；空草稿被移除时焦点回到列表。
    /// 同步聚焦：BeginInvoke 的窗口期内按下的键会落入刚收起但仍持焦点的编辑框。</summary>
    private void CommitExpandedEdit()
    {
        VM.CollapseExpanded();
        var keep = VM.SelectedTask;
        // 收起只切换卡片内部模板，条目容器仍在视觉树中，同步聚焦立即可用
        if (keep != null) (ContainerOf(keep) as UIElement ?? (UIElement)TaskList).Focus();
        else ParkFocusOnTaskList();
    }

    /// <summary>可见列表中第一个选中项的索引（删除/流转后用于落位）。</summary>
    private int FirstSelectedIndex()
    {
        var tasks = TaskList.Items.OfType<TaskViewModel>().ToList();
        var first = VM.SelectedTasks.FirstOrDefault();
        if (first == null) return 0;
        var i = tasks.IndexOf(first);
        return i < 0 ? 0 : i;
    }

    /// <summary>把焦点交给指定任务的条目容器。只聚焦：焦点从列表外进入时不联动选中，
    /// 需要选中的场景（删除落位、方向键定位）须显式设置 VM.SelectedTask。</summary>
    private void FocusContainerOf(TaskViewModel task)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            (ContainerOf(task) as UIElement ?? (UIElement)TaskList).Focus();
        }));
    }

    private void ResetPressState()
    {
        _downTask = null;
    }

    /// <summary>焦点停回任务列表（Esc/点空白/浮层关闭后）：hjkl/方向键有确定的作用对象，
    /// 且 ListBox 基座样式（StanzaControlBase）带 IME 禁用，中文输入法不吞导航字母键。
    /// 不用 Keyboard.ClearFocus()：焦点为空时 WPF 恢复默认 IME 上下文，中文模式下字母键被吞；
    /// ListBox.Focus() 只聚焦列表元素本身，不会像 Tab 进入那样聚焦首项容器、跳动视图。</summary>
    private void ParkFocusOnTaskList() => (TaskList as UIElement).Focus();

    /// <summary>删除/移走任务后，把选中与焦点落到原位置的后续任务上（Delete/Backspace 可连续操作）；
    /// 列表空了则把焦点交给列表本身。</summary>
    private void FocusTaskAtIndex(int index)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var tasks = TaskList.Items.OfType<TaskViewModel>().ToList();
            if (tasks.Count == 0)
            {
                (TaskList as UIElement).Focus();
                return;
            }
            var task = tasks[Math.Clamp(index, 0, tasks.Count - 1)];
            // 被删条目的容器已随视觉树移除，焦点落空；从外部聚焦不联动选中，需显式设置
            VM.SelectedTask = task;
            (ContainerOf(task) as UIElement ?? (UIElement)TaskList).Focus();
        }));
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
