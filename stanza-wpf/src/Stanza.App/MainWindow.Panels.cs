using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Stanza.App.ViewModels;

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
            ClearButton.ToolTip = "再次点击确认清空";
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
        ClearButton.ToolTip = "清空";
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
