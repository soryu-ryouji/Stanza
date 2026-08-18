using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Stanza.App.Services;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App;

/// <summary>
/// 勾选完成动画、最近文件弹层、外部文件拖入、空白点击收起展开面板。
/// </summary>
public partial class MainWindow
{
    // ==================== 空白点击：关闭弹层 / 收起展开 ====================

    private void ContentArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // 最近文件弹层：点击弹层以外的任意位置关闭（最近文件按钮自身由 Click 切换，不在此处理）。
        // 弹层内容在独立视觉树中，事件经逻辑树隧道路由到此；必须排除弹层内部的点击，
        // 否则点条目时弹层在 Preview 阶段被关闭，条目自身的 Click/Command 来不及触发。
        if (RecentPopup.IsOpen
            && !ReferenceEquals(VisualTreeEx.FindVisualAncestor<ButtonBase>(source), RecentButton)
            && !VisualTreeEx.IsWithin(source, RecentPanel))
            RecentPopup.IsOpen = false;

        // 点击空白区域：收起展开的任务。点在任务卡片、输入框、按钮（含侧栏条目）上时不算空白
        if (VisualTreeEx.FindVisualAncestor<ListBoxItem>(source) != null
            || VisualTreeEx.FindVisualAncestor<TextBoxBase>(source) != null
            || VisualTreeEx.FindVisualAncestor<ButtonBase>(source) != null)
            return;

        if (VM.SelectedTask != null || VM.ExpandedTask != null)
        {
            // 点击空白退出编辑（空草稿随之移除）
            VM.CollapseExpanded();
            VM.SelectedTask = null;
            ParkFocusOnTaskList();
        }
    }

    // ==================== 侧栏：项目/标签选择互斥 ====================

    // 两个列表的选中互斥由代码管理，SelectedItem 不设绑定：单向/双向绑定写入不在 Items 中的值时，
    // Selector 会保留旧选中（等待条目出现），造成两个列表同时「选中」的假象
    private bool _syncingFacetSelection;

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFacetSelection) return;
        if (ProjectList.SelectedItem is FacetItemViewModel f) VM.SelectedFacet = f;
    }

    private void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFacetSelection) return;
        if (TagList.SelectedItem is FacetItemViewModel f) VM.SelectedFacet = f;
    }

    /// <summary>SelectedFacet 变化后同步两个列表的可见选中：当前选中的项目/标签高亮，另一个列表清空。</summary>
    private void SyncFacetSelection()
    {
        var f = VM.SelectedFacet;
        _syncingFacetSelection = true;
        try
        {
            ProjectList.SelectedItem = f is { Kind: FacetKind.Project } ? f : null;
            TagList.SelectedItem = f is { Kind: FacetKind.Tag } ? f : null;
        }
        finally
        {
            _syncingFacetSelection = false;
        }
    }

    // ==================== 勾选完成 ====================

    /// <summary>由 Themes/TaskTemplates 资源字典中的勾选框转发调用。</summary>
    // ==================== 完成动画：勾选 → 变灰 → 淡出 → 收起补位 ====================

    /// <summary>动画进行中（尚未提交）的任务：防止动画期间被重复完成。</summary>
    private readonly HashSet<TaskViewModel> _completingTasks = new();

    internal void HandleTaskCheck(CheckBox checkBox, RoutedEventArgs e)
    {
        if (checkBox.DataContext is not TaskViewModel task) return;
        e.Handled = true;
        AnimateCompleteTasks(new[] { task });
    }

    /// <summary>以「勾选 → 变灰 → 淡出 → 收起」动画完成一组任务：先补上勾选视觉（点击勾选框的路径
    /// 已由 Click 置位 IsChecked），整卡降至半透明（白底上即灰化），再渐隐，最后高度收起、
    /// 下方任务匀速补位。全部结束后统一提交流转（§9），选中/焦点落位到空缺处。</summary>
    private void AnimateCompleteTasks(IReadOnlyList<TaskViewModel> tasks)
    {
        var pending = tasks.Where(t => _completingTasks.Add(t)).ToList();
        if (pending.Count == 0) return;
        var focusIndex = FirstSelectedIndex();   // 提交后的落位（空缺处）
        var remaining = pending.Count;
        foreach (var task in pending)
        {
            var item = ContainerOf(task);
            if (item == null)
            {
                // 容器不可见（滚动外/刚切换视图）：不参与动画，直接计入提交
                if (--remaining == 0) CommitCompleteTasks(pending, focusIndex);
                continue;
            }

            // 勾选：Space/命令路径未经过勾选框点击，这里补齐视觉；IsEnabled 防止动画期间重复点击
            var box = VisualTreeEx.FindVisualChildren<CheckBox>(item).FirstOrDefault();
            if (box != null) { box.IsChecked = true; box.IsEnabled = false; }

            item.ClipToBounds = true;
            item.MaxHeight = item.ActualHeight;   // 锁定当前高度，淡出结束后由此收起

            // 变灰 → 淡出：先降至 0.45 停顿出「灰化」观感，再渐隐至消失
            item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new LinearDoubleKeyFrame(1.0, TimeSpan.Zero),
                    new LinearDoubleKeyFrame(0.45, TimeSpan.FromMilliseconds(180)),
                    new LinearDoubleKeyFrame(0.0, TimeSpan.FromMilliseconds(430)),
                },
            });

            var shrink = new DoubleAnimation(item.ActualHeight, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                BeginTime = TimeSpan.FromMilliseconds(430),
            };
            shrink.Completed += (_, _) =>
            {
                if (--remaining == 0) CommitCompleteTasks(pending, focusIndex);
            };
            item.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
        }
    }

    /// <summary>动画结束后的统一提交：流转至 DONE（§9），落位到空缺处。
    /// 动画期间被其他操作移走的任务直接跳过。</summary>
    private void CommitCompleteTasks(List<TaskViewModel> tasks, int focusIndex)
    {
        foreach (var t in tasks) _completingTasks.Remove(t);
        var alive = tasks.Where(t => VM.Blocks.Any(b => b.Items.Contains(t))).ToList();
        if (alive.Count > 0) VM.CompleteTasks(alive);
        FocusTaskAtIndex(focusIndex);
    }

    // ==================== 撤销回归动画：让位展开 → 灰态浮现 → 颜色恢复 → 取消勾选 ====================

    /// <summary>带回归动画的撤销：全量重建后按内容键做多重集 diff，「回来的任务」播
    /// 完成动画的倒放（高度展开让位、灰态渐显、颜色恢复、勾选框取消勾选）。</summary>
    private void UndoWithAnimation()
    {
        var before = TaskKeyCounts();
        // 选中按排序位置记忆（而非任务引用）：任务流走后位置由下一个占据，
        // 撤销把任务送回原位时，选中落在同一位置——自然回到被操作的任务
        var selectedIndex = VM.SelectedTask is { } st
            ? TaskList.Items.OfType<TaskViewModel>().ToList().IndexOf(st)
            : -1;
        var scroll = VisualTreeEx.FindVisualChildren<ScrollViewer>(TaskList).FirstOrDefault();
        var offset = scroll?.VerticalOffset ?? 0;
        VM.Undo();   // 同步重建文档（TaskViewModel 全部为新实例，只能按内容键比较）
        var restored = new List<TaskViewModel>();
        foreach (var t in VM.Blocks.SelectMany(b => b.Tasks))
        {
            var key = TaskKey(t);
            if (before.TryGetValue(key, out var n) && n > 0) before[key] = n - 1;
            else restored.Add(t);
        }
        // 快照重建不携带视图状态：选中位置与滚动位置按原值恢复
        if (selectedIndex >= 0)
        {
            var tasks = TaskList.Items.OfType<TaskViewModel>().ToList();
            VM.SelectedTask = tasks.Count == 0
                ? null
                : tasks[Math.Clamp(selectedIndex, 0, tasks.Count - 1)];
        }
        if (restored.Count == 0 && scroll == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            scroll?.ScrollToVerticalOffset(offset);
            foreach (var task in restored) AnimateRestoreTask(task);
        }));
    }

    /// <summary>任务内容键（状态 + 主行 + 备注）：撤销全量重建后识别「同一任务」的多重集键。</summary>
    private static string TaskKey(TaskViewModel t)
        => $"{t.State}\n{t.HeaderText}\n{t.NotesText}";

    private Dictionary<string, int> TaskKeyCounts()
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in VM.Blocks.SelectMany(b => b.Tasks))
        {
            var key = TaskKey(t);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    /// <summary>单个任务的回归动画：高度从 0 展开让位、灰态随展开浮现、颜色快速恢复。
    /// 勾选框全程保持未勾选（回归即「回到进行中」的直接呈现，不做「先勾后取消」的两段式）。</summary>
    private void AnimateRestoreTask(TaskViewModel task)
    {
        var item = ContainerOf(task);
        if (item == null) return;

        var height = item.ActualHeight;
        item.ClipToBounds = true;
        item.Opacity = 0;
        item.MaxHeight = 0;

        // 让位展开：其他任务随布局匀速让开；灰态同步浮现
        var grow = new DoubleAnimation(0, height, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        grow.Completed += (_, _) =>
        {
            // 先移除动画时钟（HoldEnd 会让终值压住属性），再清动画前写入的本地值 0，
            // 否则卡片被钉在折叠高度（时钟残留）或塌成 0（本地值残留）
            item.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            item.ClearValue(FrameworkElement.MaxHeightProperty);
        };
        item.BeginAnimation(FrameworkElement.MaxHeightProperty, grow);

        // 颜色恢复：灰（0.45）随展开出现，再快速渐变回正常
        item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0.0, TimeSpan.Zero),
                new LinearDoubleKeyFrame(0.45, TimeSpan.FromMilliseconds(160)),
                new LinearDoubleKeyFrame(1.0, TimeSpan.FromMilliseconds(300)),
            },
        });
    }

    // ==================== 最近文件弹出层 ====================

    private int _recentCycleIndex = -1;   // 弹层内当前高亮行（-1 = 无键盘循环高亮）

    private void InitializeRecentPopup()
    {
        // 弹层打开时：从底部向上滑入 + 淡入
        RecentPopup.Opened += (_, _) =>
        {
            RecentPanel.RenderTransform.BeginAnimation(
                System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            RecentPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        };
        // 任意关闭路径（点空白/点条目/Esc/确认打开）统一复位循环高亮
        RecentPopup.Closed += (_, _) => _recentCycleIndex = -1;
    }

    /// <summary>Ctrl+R（VS Code quick-open 语义，两种键盘模式一致）：弹层未开则打开并高亮「下一个」文件
    /// （MRU 首行是当前文件，快速一开一松即切换到上一文件）；已开则高亮循环下移（到底回顶）。
    /// 松开 Ctrl 打开高亮行（OnPreProcessInput 的 KeyUp 处理）；Esc 关闭不打开。</summary>
    private void OpenOrCycleRecent()
    {
        if (VM.Recents.Items.Count == 0)
        {
            RecentPopup.IsOpen = true;   // 空列表无可循环项，仅展示空态与新建入口
            return;
        }
        _recentCycleIndex = RecentPopup.IsOpen
            ? (_recentCycleIndex + 1) % VM.Recents.Items.Count
            : VM.Recents.Items.Count > 1 ? 1 : 0;   // 首高亮落在下一个文件（首行是当前文件）
        RecentPopup.IsOpen = true;
        // Popup 首帧布局完成后行容器才可用；同优先级回调按投递顺序执行，连按时停在最后投递的行
        var index = _recentCycleIndex;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusRecentRow(index)));
    }

    /// <summary>把键盘焦点（高亮）移到最近文件弹层的第 index 行。</summary>
    private void FocusRecentRow(int index)
    {
        if (!RecentPopup.IsOpen || index < 0 || index >= VM.Recents.Items.Count) return;
        if (RecentItems.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return;
        if (VisualTreeEx.FindVisualChildren<Button>(container).FirstOrDefault() is { } row)
            Keyboard.Focus(row);
    }

    // 弹层的关闭路径：再点按钮切换、点弹层内条目（RecentItem_Click）、
    // 点窗口其他位置（ContentArea_PreviewMouseLeftButtonDown）
    private void RecentButton_Click(object sender, RoutedEventArgs e)
        => RecentPopup.IsOpen = !RecentPopup.IsOpen;

    // 条目内「移除」按钮的 Click 会冒泡到行按钮，但其 OriginalSource 是移除按钮自身；
    // 仅条目本体（或底部「新建文件」按钮）被点击时才关闭弹层——移除记录后弹层保持开启，便于连续清理
    private void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        RecentPopup.IsOpen = false;
    }

    // ==================== 标签/项目选择器 ====================

    private FacetKind _pickerKind;

    private sealed record PickerRow(string Display, string Name, bool Applied, bool Highlighted);

    // ---- 键盘高亮（VS Code quick-open 语义） ----
    // 焦点始终留在输入框：方向键只移动虚拟高亮行，不产生焦点迁移/转发/吞键问题。
    // Space 选择高亮项，Enter 确认；输入过滤文本时高亮清空（Enter 回到文本提交语义）

    private const string ClearSentinel = "\0";   // 高亮键的哨兵值：「清除」按钮

    private string? _highlightKey;   // 当前高亮：行名 / ClearSentinel / null（无高亮，输入态）

    /// <summary>由右键菜单（标签…/项目…）在鼠标位置打开选择器（Themes/TaskTemplates 转发调用），
    /// 或由快捷键（T/P）在选中任务旁打开（anchor 为锚点，null 时取鼠标位置）。
    /// 选择器是与主窗口同一视觉树的应用内浮层，不受 ContextMenu 关闭时的焦点回收影响。</summary>
    internal void OpenFacetPicker(FacetKind kind, Point? anchor = null)
    {
        if (!VM.HasSelection) return;
        CloseMovePicker();   // 浮层互斥：同一时刻只开一个选择器
        _pickerKind = kind;
        _highlightKey = null;
        FacetPickerInput.Tag = Loc.Get(kind == FacetKind.Tag ? "Picker_Tag" : "Picker_Project");
        FacetPickerInput.Text = "";
        FacetPickerError.Visibility = Visibility.Collapsed;
        RefreshFacetPicker();

        // 在鼠标附近落位，夹取到窗口内
        // 参照物用 Root（始终已布局）：Collapsed 的浮层自身 ActualWidth/Height 为 0，不能作为参照
        var pos = anchor ?? Mouse.GetPosition(Root);
        Canvas.SetLeft(FacetPickerPanel,
            Math.Clamp(pos.X, 0, Math.Max(0, Root.ActualWidth - FacetPickerPanel.Width - 8)));
        Canvas.SetTop(FacetPickerPanel,
            Math.Clamp(pos.Y, 0, Math.Max(0, Root.ActualHeight - 320)));

        FacetPickerPanel.Visibility = Visibility.Visible;
        UpdatePickerLayerVisibility();
        // 与 ExitOverlay 同款：同一视觉树内直接聚焦输入框
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Keyboard.Focus(FacetPickerInput)));
    }

    private bool FacetPickerOpen => FacetPickerPanel.Visibility == Visibility.Visible;

    /// <summary>浮层承载两个选择器面板（标签/项目、状态），任一可见即需要浮层拦截点击。</summary>
    private void UpdatePickerLayerVisibility()
        => PickerLayer.Visibility =
            FacetPickerPanel.Visibility == Visibility.Visible
            || MovePickerPanel.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;

    /// <summary>关闭选择器。焦点残留在已隐藏的面板内时停回任务列表（WPF 不自动迁移焦点，
    /// 否则焦点留在不可见的输入框上，Esc 关闭后 T/P 等裸键分发全部失效）。</summary>
    private void CloseFacetPicker()
    {
        FacetPickerPanel.Visibility = Visibility.Collapsed;
        UpdatePickerLayerVisibility();
        if (Keyboard.FocusedElement is DependencyObject focus
            && VisualTreeEx.IsWithin(focus, FacetPickerPanel))
            ParkFocusOnTaskList();
    }

    /// <summary>点选择器卡片以外的区域关闭（点卡片内部不处理，由行/按钮自身响应）。</summary>
    private void PickerLayer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (!VisualTreeEx.IsWithin(source, FacetPickerPanel))
            CloseFacetPicker();
        if (!VisualTreeEx.IsWithin(source, MovePickerPanel))
            CloseMovePicker();
    }

    private void RefreshFacetPicker()
    {
        var filter = FacetPickerInput.Text.Trim();
        var prefix = _pickerKind == FacetKind.Tag ? "#" : "+";
        // 高亮状态随行进 ItemsSource（行按钮 Tag 绑定 Highlighted）：容器异步生成后
        // 新按钮自带正确高亮，不依赖重建后的代码遍历（同步遍历会落在未生成的旧容器上）
        var rows = VM.FacetNames(_pickerKind)
            .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(n => new PickerRow(prefix + n, n, VM.SelectionHasFacet(_pickerKind, n), n == _highlightKey))
            .ToList();
        FacetPickerList.ItemsSource = rows;
        // 高亮行被过滤掉或随 toggle 消失时，高亮回落到输入态
        if (_highlightKey != ClearSentinel && rows.All(r => r.Name != _highlightKey))
            _highlightKey = null;
        FacetPickerClear.Visibility = VM.SelectionHasAnyFacet(_pickerKind) ? Visibility.Visible : Visibility.Collapsed;
        FacetPickerClear.Tag = _highlightKey == ClearSentinel;
    }

    /// <summary>方向键移动高亮：无高亮（输入态）→ 首行 → … → 末行/「清除」（可见时）；
    /// 从首行再向上回到输入态，底部停住不循环。</summary>
    private void MoveHighlight(int delta)
    {
        var keys = FacetPickerList.Items.OfType<PickerRow>().Select(r => r.Name).ToList();
        if (FacetPickerClear.Visibility == Visibility.Visible) keys.Add(ClearSentinel);
        if (keys.Count == 0) return;
        var i = _highlightKey == null ? -1 : keys.IndexOf(_highlightKey);
        var next = Math.Clamp(i + delta, -1, keys.Count - 1);
        SetHighlight(next < 0 ? null : keys[next]);
    }

    private void SetHighlight(string? key)
    {
        if (_highlightKey == key) return;
        _highlightKey = key;
        UpdateHighlightVisuals();
    }

    /// <summary>不重建 ItemsSource 的高亮迁移（方向键/悬停）：遍历既有行按钮覆写 Tag
    /// （局部值覆盖绑定；下次 RefreshFacetPicker 重建后由绑定恢复）。</summary>
    private void UpdateHighlightVisuals()
    {
        foreach (var btn in VisualTreeEx.FindVisualChildren<Button>(FacetPickerList))
            btn.Tag = btn.DataContext is PickerRow r && r.Name == _highlightKey;
        FacetPickerClear.Tag = _highlightKey == ClearSentinel;
    }

    private void FacetPickerRow_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PickerRow row) SetHighlight(row.Name);
    }

    private void FacetPickerClear_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => SetHighlight(ClearSentinel);

    private void FacetPickerRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PickerRow row) return;
        SetHighlight(row.Name);
        if (_pickerKind == FacetKind.Tag)
        {
            VM.ToggleTag(row.Name);
            RefreshFacetPicker();   // 浮层保持开启（连续切换），高亮按名称保留
            Keyboard.Focus(FacetPickerInput);   // 焦点锁输入框：点击后键盘立即可用
        }
        else
        {
            VM.SetProjectForSelection(row.Name);
            CloseFacetPicker();
        }
    }

    /// <summary>应用当前高亮项。toggle=true（Space，选择）：标签切换并保持打开（连续选择）、
    /// 项目应用并关闭；toggle=false（Enter，确认）：「清除」执行清空、项目应用高亮项并关闭、
    /// 标签仅关闭——选择已由 Space 完成，Enter 不再切换（否则会撤销刚勾选的标签）。</summary>
    private void ApplyHighlighted(bool toggle)
    {
        if (_highlightKey == ClearSentinel) { ApplyPickerClear(); return; }
        if (_highlightKey is not { } name) return;
        if (_pickerKind == FacetKind.Tag)
        {
            if (!toggle) { CloseFacetPicker(); return; }
            VM.ToggleTag(name);
            RefreshFacetPicker();
        }
        else
        {
            VM.SetProjectForSelection(name);
            CloseFacetPicker();
        }
    }

    private void FacetPickerInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        FacetPickerError.Visibility = Visibility.Collapsed;
        _highlightKey = null;   // 输入过滤时清空高亮：Enter 保持文本提交语义（创建/精确匹配）
        if (FacetPickerOpen) RefreshFacetPicker();
    }

    // 挂在弹层面板上（隧道）：焦点锁定在输入框，按键统一在此处理
    private void FacetPicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFacetPicker();
            return;
        }
        // 方向键与 Alt+J/K（vim 语义）、Alt+N/P（VS Code quick-open 的 next/previous 语义）移动高亮行。
        // Alt 组合的主键在 SystemKey 上（同 OnPreProcessInput）
        var navDelta = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.K => -1,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.J => 1,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.P => -1,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.N => 1,
            _ => 0,
        };
        if (navDelta != 0)
        {
            e.Handled = true;   // 先于输入框的光标移动（单行框的 Home/End 语义）拦截
            MoveHighlight(navDelta);
            return;
        }
        // Space 选择高亮项；无高亮时放行，作为输入框文本（标签/项目名不允许空格，提交时校验）
        if (e.Key == Key.Space && _highlightKey != null)
        {
            e.Handled = true;
            ApplyHighlighted(toggle: true);
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_highlightKey != null) ApplyHighlighted(toggle: false);   // 确认
            else CommitFacetPickerInput();   // 无高亮：提交输入文本（精确匹配或新建）
            return;
        }
        // 焦点不在输入框时（点击行到回焦之间的窗口期）拦截 Delete/Backspace，避免穿透到主列表删除任务；
        // 在输入框时放行，由编辑框自身消化（过滤输入的删字）
        if ((e.Key is Key.Back or Key.Delete) && e.OriginalSource is not TextBoxBase)
            e.Handled = true;
    }

    /// <summary>回车提交输入：精确命中既有名称（大小写不敏感）则直接应用，否则按新名称创建（校验 RFC 名称规则）。</summary>
    private void CommitFacetPickerInput()
    {
        var text = FacetPickerInput.Text.Trim();
        if (text.Length == 0) return;

        var existing = VM.FacetNames(_pickerKind)
            .FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        var name = existing ?? text;

        if (existing == null)
        {
            var valid = _pickerKind == FacetKind.Tag
                ? StanzaPatterns.IsValidTagName(name)
                : StanzaPatterns.IsValidProjectName(name);
            if (!valid)
            {
                FacetPickerError.Text = Loc.Get(_pickerKind == FacetKind.Tag
                    ? "Picker_ErrorTag"
                    : "Picker_ErrorProject");
                FacetPickerError.Visibility = Visibility.Visible;
                return;
            }
        }

        if (_pickerKind == FacetKind.Tag)
            VM.ToggleTag(name);
        else
            VM.SetProjectForSelection(name);
        CloseFacetPicker();   // 回车提交后关闭浮层（鼠标点选标签的连续切换路径不受影响）
    }

    private void FacetPickerClear_Click(object sender, RoutedEventArgs e) => ApplyPickerClear();

    /// <summary>「清除」：清空选中任务的该类 facet（标签全清 / 项目置空）并关闭浮层。</summary>
    private void ApplyPickerClear()
    {
        if (_pickerKind == FacetKind.Tag) VM.ClearTagsForSelection();
        else VM.SetProjectForSelection(null);
        CloseFacetPicker();
    }

    // ==================== 状态选择器（移到…） ====================

    private TaskState? _moveHighlight;   // 键盘高亮行（打开时按选中任务状态预置）

    /// <summary>由快捷键（M，锚点为选中任务右上角）或右键菜单「状态…」（鼠标位置）打开。
    /// 目标是四个固定状态：无输入框（无需过滤/新建），行在打开时按规范序即时构建——
    /// 本地化名称与「当前状态」标记始终新鲜，也无需 Loc.Changed 的刷新挂钩。</summary>
    internal void OpenMovePicker(Point? anchor = null)
    {
        if (!VM.HasSelection) return;
        CloseFacetPicker();   // 浮层互斥：同一时刻只开一个选择器

        RebuildMovePickerRows();

        // 落位与夹取同标签选择器（参照物用已布局的 Root，Collapsed 面板自身无尺寸）
        var pos = anchor ?? Mouse.GetPosition(Root);
        Canvas.SetLeft(MovePickerPanel,
            Math.Clamp(pos.X, 0, Math.Max(0, Root.ActualWidth - MovePickerPanel.Width - 8)));
        Canvas.SetTop(MovePickerPanel,
            Math.Clamp(pos.Y, 0, Math.Max(0, Root.ActualHeight - 150)));

        MovePickerPanel.Visibility = Visibility.Visible;
        UpdatePickerLayerVisibility();
        // 无输入框：焦点锁面板本身，全部按键在 MovePicker_KeyDown 统一处理
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Keyboard.Focus(MovePickerPanel)));
    }

    private void CloseMovePicker()
    {
        MovePickerPanel.Visibility = Visibility.Collapsed;
        UpdatePickerLayerVisibility();
        if (Keyboard.FocusedElement is DependencyObject focus
            && VisualTreeEx.IsWithin(focus, MovePickerPanel))
            ParkFocusOnTaskList();
    }

    /// <summary>按规范序构建四行：状态色点 + 名称 +（当前状态）✓ + 数字提示。
    /// 初始高亮：选中任务状态一致时落在当前状态行，否则落在第一行（DOING）。</summary>
    private void RebuildMovePickerRows()
    {
        MovePickerRows.Children.Clear();
        var states = VM.SelectedTasks.Select(t => t.State).Distinct().ToList();
        var current = states.Count == 1 ? states[0] : (TaskState?)null;
        _moveHighlight = current ?? TaskStateNames.CanonicalOrder[0];

        for (var i = 0; i < TaskStateNames.CanonicalOrder.Length; i++)
        {
            var state = TaskStateNames.CanonicalOrder[i];
            var row = new Button
            {
                Style = (Style)FindResource("PickerRowButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                DataContext = state,
                Content = MakeMovePickerRowContent(state, i + 1, current == state),
            };
            row.Click += MovePickerRow_Click;
            row.MouseEnter += MovePickerRow_MouseEnter;
            MovePickerRows.Children.Add(row);
        }
        UpdateMoveHighlightVisuals();
    }

    /// <summary>行内容：色点 + 本地化状态名 + 当前状态 ✓ + 右侧数字键提示。
    /// 文字前景色绑定行按钮：高亮（悬停/键盘 Tag=true）时随按钮反白。</summary>
    private Grid MakeMovePickerRowContent(TaskState state, int digit, bool isCurrent)
    {
        var foreground = new Binding(nameof(Button.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = StateToBrushConverter.Of(state),
        };
        var name = new TextBlock
        {
            Text = Loc.StateName(state),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
        };
        name.SetBinding(TextBlock.ForegroundProperty, foreground);
        var check = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
        };
        check.SetBinding(TextBlock.ForegroundProperty, foreground);
        var key = new TextBlock
        {
            Text = digit.ToString(),
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        key.SetBinding(TextBlock.ForegroundProperty, foreground);

        Grid.SetColumn(name, 1);
        Grid.SetColumn(check, 2);
        Grid.SetColumn(key, 3);
        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(check);
        grid.Children.Add(key);
        return grid;
    }

    // 焦点锁在面板上：数字 1-4 直达（含小键盘）、方向键/jk 移动高亮、Enter/Space 确认、
    // Esc 或再按 M 关闭（开关语义）
    private void MovePicker_KeyDown(object sender, KeyEventArgs e)
    {
        var digit = e.Key switch
        {
            >= Key.D1 and <= Key.D4 => e.Key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad4 => e.Key - Key.NumPad1 + 1,
            _ => 0,
        };
        if (digit > 0)
        {
            e.Handled = true;
            ApplyMoveChoice(TaskStateNames.CanonicalOrder[digit - 1]);
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
            case Key.M:
                e.Handled = true;
                CloseMovePicker();
                return;
            case Key.Up:
            case Key.K:
                e.Handled = true;
                CycleMoveHighlight(-1);
                return;
            case Key.Down:
            case Key.J:
                e.Handled = true;
                CycleMoveHighlight(1);
                return;
            case Key.Enter:
            case Key.Space:
                e.Handled = true;
                if (_moveHighlight is { } state) ApplyMoveChoice(state);
                return;
        }
    }

    private void CycleMoveHighlight(int delta)
    {
        var order = TaskStateNames.CanonicalOrder;
        var i = Array.IndexOf(order, _moveHighlight ?? order[0]);
        _moveHighlight = order[Math.Clamp(i + delta, 0, order.Length - 1)];
        UpdateMoveHighlightVisuals();
    }

    /// <summary>高亮迁移：覆写行按钮 Tag（PickerRowButton 样式以 Tag=true 渲染高亮行）。</summary>
    private void UpdateMoveHighlightVisuals()
    {
        foreach (var row in MovePickerRows.Children.OfType<Button>())
            row.Tag = row.DataContext is TaskState s && s == _moveHighlight;
    }

    private void MovePickerRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button { DataContext: TaskState state })
        {
            _moveHighlight = state;
            UpdateMoveHighlightVisuals();
        }
    }

    private void MovePickerRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TaskState state })
            ApplyMoveChoice(state);
    }

    /// <summary>流转并关闭；焦点落位到空缺处（同 Delete/Backspace 路径，支持连续操作）。</summary>
    private void ApplyMoveChoice(TaskState state)
    {
        var index = FirstSelectedIndex();
        VM.MoveSelectionTo(state);
        CloseMovePicker();
        FocusTaskAtIndex(index);
    }

    // ==================== 底部工具栏：清空二次确认 ====================

    private bool _clearArmed;
    private DispatcherTimer? _clearTimer;

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            // 第一次点击：进入待确认状态（图标变红 + 提示变化），3 秒无操作自动恢复
            _clearArmed = true;
            ClearButton.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            ClearButton.ToolTip = Loc.Get("Tip_ClearConfirm");
            _clearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _clearTimer.Tick -= ClearTimer_Tick;
            _clearTimer.Tick += ClearTimer_Tick;
            _clearTimer.Start();
            return;
        }

        DisarmClear();
        if (VM.ClearBlockCommand.CanExecute(null)) VM.ClearBlockCommand.Execute(null);
    }

    private void ClearTimer_Tick(object? sender, EventArgs e) => DisarmClear();

    private void DisarmClear()
    {
        _clearTimer?.Stop();
        _clearArmed = false;
        ClearButton.ClearValue(Control.ForegroundProperty);
        ClearButton.ToolTip = Loc.Get("Tip_Clear");
    }

    // ==================== 文件拖放 ====================

    private static bool IsSupportedFile(string path)
        => path.EndsWith(".stanza", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateFileDrag(e);

    private void Window_DragOver(object sender, DragEventArgs e) => UpdateFileDrag(e);

    private void UpdateFileDrag(DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files.Any(IsSupportedFile);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            var file = files.FirstOrDefault(IsSupportedFile);
            if (file != null) VM.OpenFile(file);
        }
        e.Handled = true;
    }
}
