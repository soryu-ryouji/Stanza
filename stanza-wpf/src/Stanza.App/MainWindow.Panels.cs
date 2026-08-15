using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
            // 只清焦点：把焦点交给 ListBox 会让它聚焦首项并把视图跳回顶部
            Keyboard.ClearFocus();
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
    internal void HandleTaskCheck(CheckBox checkBox, RoutedEventArgs e)
    {
        if (checkBox.DataContext is not TaskViewModel task) return;
        e.Handled = true;
        checkBox.IsEnabled = false;   // 防止动画期间重复点击

        // 渐变消失 + 高度收起（下面的任务随布局自动上移），动画结束后才进入 DONE
        var item = VisualTreeEx.FindVisualAncestor<ListBoxItem>(checkBox);
        if (item == null)
        {
            VM.CompleteTask(task);
            return;
        }

        item.ClipToBounds = true;
        item.MaxHeight = item.ActualHeight;   // 锁定当前高度，再收到 0

        item.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));

        var shrink = new DoubleAnimation(item.ActualHeight, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            BeginTime = TimeSpan.FromMilliseconds(60),
        };
        shrink.Completed += (_, _) => VM.CompleteTask(task);
        item.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
    }

    // ==================== 最近文件弹出层 ====================

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

    private sealed record PickerRow(string Display, string Name, bool Applied);

    /// <summary>由右键菜单（标签…/项目…）在鼠标位置打开选择器（Themes/TaskTemplates 转发调用）。
    /// 选择器是与主窗口同一视觉树的应用内浮层，不受 ContextMenu 关闭时的焦点回收影响。</summary>
    internal void OpenFacetPicker(FacetKind kind)
    {
        if (!VM.HasSelection) return;
        _pickerKind = kind;
        FacetPickerInput.Tag = Loc.Get(kind == FacetKind.Tag ? "Picker_Tag" : "Picker_Project");
        FacetPickerInput.Text = "";
        FacetPickerError.Visibility = Visibility.Collapsed;
        RefreshFacetPicker();

        // 在鼠标附近落位，夹取到窗口内
        // 参照物用 Root（始终已布局）：Collapsed 的浮层自身 ActualWidth/Height 为 0，不能作为参照
        var pos = Mouse.GetPosition(Root);
        Canvas.SetLeft(FacetPickerPanel,
            Math.Clamp(pos.X, 0, Math.Max(0, Root.ActualWidth - FacetPickerPanel.Width - 8)));
        Canvas.SetTop(FacetPickerPanel,
            Math.Clamp(pos.Y, 0, Math.Max(0, Root.ActualHeight - 320)));

        FacetPickerLayer.Visibility = Visibility.Visible;
        // 与 ExitOverlay 同款：同一视觉树内直接聚焦输入框
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Keyboard.Focus(FacetPickerInput)));
    }

    private bool FacetPickerOpen => FacetPickerLayer.Visibility == Visibility.Visible;

    private void CloseFacetPicker() => FacetPickerLayer.Visibility = Visibility.Collapsed;

    /// <summary>点选择器卡片以外的区域关闭（点卡片内部不处理，由行/按钮自身响应）。</summary>
    private void FacetPickerLayer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!VisualTreeEx.IsWithin(e.OriginalSource as DependencyObject, FacetPickerPanel))
            CloseFacetPicker();
    }

    private void RefreshFacetPicker()
    {
        var filter = FacetPickerInput.Text.Trim();
        var prefix = _pickerKind == FacetKind.Tag ? "#" : "+";
        FacetPickerList.ItemsSource = VM.FacetNames(_pickerKind)
            .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(n => new PickerRow(prefix + n, n, VM.SelectionHasFacet(_pickerKind, n)))
            .ToList();
        FacetPickerClear.Visibility = VM.SelectionHasAnyFacet(_pickerKind) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FacetPickerRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PickerRow row) return;
        if (_pickerKind == FacetKind.Tag)
        {
            VM.ToggleTag(row.Name);
            RefreshFacetPicker();   // 浮层保持开启，便于连续切换多个标签
        }
        else
        {
            VM.SetProjectForSelection(row.Name);
            CloseFacetPicker();
        }
    }

    private void FacetPickerInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        FacetPickerError.Visibility = Visibility.Collapsed;
        if (FacetPickerOpen) RefreshFacetPicker();
    }

    // 挂在弹层面板上（隧道）：焦点在输入框或列表行上时 Enter/Esc 都生效
    private void FacetPicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFacetPicker();
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitFacetPickerInput();
            return;
        }
        // 焦点不在输入框时（如点击列表行后）拦截 Delete/Backspace，避免穿透到主列表删除任务；
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

    private void FacetPickerClear_Click(object sender, RoutedEventArgs e)
    {
        if (_pickerKind == FacetKind.Tag) VM.ClearTagsForSelection();
        else VM.SetProjectForSelection(null);
        CloseFacetPicker();
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
