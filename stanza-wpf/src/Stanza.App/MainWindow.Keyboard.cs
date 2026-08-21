using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stanza.App.ViewModels;

namespace Stanza.App;

/// <summary>
/// 键盘分发与焦点管理。应用级快捷键在路由前分发（PreProcessInput，无焦点也可用）；
/// 任务作用域命令逐命令检查焦点作用域；Esc/Enter 语义键在窗口 KeyDown 处理；
/// Shift+jk / Shift+方向键扩展选中。焦点停回任务列表的约定（ParkFocusOnTaskList 等）
/// 是整套键盘体系的地基，改动时需同步检查。
/// 拖拽状态机与鼠标交互见 MainWindow.Drag.cs。
/// </summary>
public partial class MainWindow
{
    private TaskViewModel? _shiftAnchor;   // Shift+jk 扩展选中的锚点（区间固定端）
    private TaskViewModel? _shiftCursor;   // 活动端（随 Shift+jk 移动）

    /// <summary>输入总入口：拖拽（PreviewMouseMove/Up）与键盘（PreProcessInput/KeyDown）处理器统一注册。</summary>
    private void InitializeDragInput()
    {
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        KeyDown += OnKeyDown;
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
            // 循环修饰键跟随 OpenRecent 绑定：两种键盘模式同为 Ctrl（VS Code macOS 惯例，见 Keymap）
            var cycleReleased = k.Key is Key.LeftCtrl or Key.RightCtrl;
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

        // 侧栏项目/标签列表与选择面板内的 Ctrl+N/P 是列表导航（quick-open 语义）：应用命令让路
        // （Windows 模式下 Ctrl+N 默认新建任务），与文本编辑手势同一先例
        if (Keyboard.FocusedElement is DependencyObject facetFocus
            && (VisualTreeEx.IsWithin(facetFocus, ProjectList) || VisualTreeEx.IsWithin(facetFocus, TagList)
                || VisualTreeEx.IsWithin(facetFocus, ChoicePickerPanel))
            && key is Key.N or Key.P
            && Keyboard.Modifiers == ModifierKeys.Control)
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

    /// <summary>Shift+jk 扩展批量选中：单选为锚点，按方向移动活动端，选中锚点..活动端区间；
    /// 活动端移回锚点即收缩。无选中时与裸 j/k 一致（j 选首项、k 选末项）。</summary>
    private bool TryShiftSelectTasks(Key key)
    {
        // 作用域同任务导航键：文本框（文本选择）、浮层、侧栏列表内不接管
        if (Keyboard.FocusedElement is DependencyObject focus)
        {
            if (focus is TextBoxBase) return false;
            if (VisualTreeEx.IsWithin(focus, PickerLayer)) return false;
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
        if (VisualTreeEx.IsWithin(focus, PickerLayer)) return false;
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

            // 标签/项目键的双语义：有选中任务 = 打开设置器（任务操作，选中是更强的信号，不限焦点位置）；
            // 无选中任务 = 跳转对应侧栏列表（导航，任意焦点位置可用——侧栏、落空均可）。
            // 编辑框内字母是输入、浮层内按键归面板自身，不拦截
            case AppCommand.OpenTagPicker:
            case AppCommand.OpenProjectPicker:
                if (RecentPopup.IsOpen || _taskDragging || focus is TextBoxBase
                    || focus != null && VisualTreeEx.IsWithin(focus, PickerLayer))
                    return false;
                if (VM.HasSelection)
                {
                    OpenFacetPicker(command == AppCommand.OpenTagPicker ? FacetKind.Tag : FacetKind.Project,
                        SelectedTaskAnchor());
                    return true;
                }
                EnterFacetJumpMode(command == AppCommand.OpenTagPicker ? FacetKind.Tag : FacetKind.Project);
                return true;

            // 打开状态选择器（移到…）：作用域同标签/项目选择器；
            // 拖拽中不打开：被拖任务已脱离区块，流转会因找不到所属区块而失败
            case AppCommand.OpenMovePicker:
                if (RecentPopup.IsOpen || _taskDragging || focus is null or TextBoxBase
                    || !VisualTreeEx.IsWithin(focus, TaskList)
                    || !VM.HasSelection)
                    return false;
                OpenMovePicker(SelectedTaskAnchor());
                return true;

            // 打开优先级选择器（Shift+P）：作用域同其他选择器；优先级只属于活跃任务，
            // 全归档选中不响应。拖拽中不打开：排序重排会使拖拽持有的引用失效
            case AppCommand.OpenPriorityPicker:
                if (RecentPopup.IsOpen || _taskDragging || focus is null or TextBoxBase
                    || !VisualTreeEx.IsWithin(focus, TaskList)
                    || !VM.HasActiveSelection)
                    return false;
                OpenPriorityPicker(SelectedTaskAnchor());
                return true;

            // 移入 DELETE（回收站语义，§9）/ 彻底删除。编辑框内是删字、选择器面板内被面板自身吞掉
            case AppCommand.DiscardTask:
            case AppCommand.DeleteTask:
                if (focus is TextBoxBase
                    || focus != null && VisualTreeEx.IsWithin(focus, PickerLayer)
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
            if (VisualTreeEx.IsWithin(focus, PickerLayer)) return false;
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

            // 项目/标签列表跳转模式：Esc 取消并恢复进入前视图
            if (_facetJumpActive)
            {
                CancelFacetJump();
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
            // 项目/标签列表跳转模式：Enter 确认预览中的面板，焦点进任务列表
            if (_facetJumpActive)
            {
                CommitFacetJump();
                e.Handled = true;
                return;
            }

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
}
