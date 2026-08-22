using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Stanza.App.Behaviors;
using Stanza.App.ViewModels;

namespace Stanza.App;

/// <summary>
/// 侧栏导航与窗口级交互：空白点击收起展开、项目/标签列表选中互斥、无选中任务时按 P/T 的
/// 侧栏快速跳转模式、外部文件拖入打开。完成/撤销动画见 MainWindow.Animations.cs，
/// 最近文件弹层见 MainWindow.Recent.cs，选择器见 MainWindow.Pickers.cs。
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

    internal void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFacetSelection) return;
        if (ProjectList.SelectedItem is FacetItemViewModel f) VM.SelectedFacet = f;
    }

    internal void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    // ==================== 项目/标签列表快速跳转（无选中任务时按 P/T） ====================

    private bool _facetJumpActive;        // 跳转模式中（Esc 取消需要还原视图）
    private ListBox? _jumpList;           // 跳转模式所在列表（ProjectList / TagList）
    private FacetItemViewModel? _jumpPrevFacet;   // 进入前的 facet（可能为 null）
    private BlockViewModel? _jumpPrevBlock;       // 进入前的区块

    /// <summary>无选中任务时按 P/T：焦点跳到对应的侧栏列表（项目/标签），方向键/jk/Ctrl+N/P 移动选中，
    /// 选中变化经 SelectionChanged → VM.SelectedFacet 驱动右侧面板实时预览（预览期间条目呈浅色高亮，
    /// 与正式选中区分）；Enter 确认（焦点进任务列表）、Esc 取消（恢复进入前视图）、焦点离开列表 = 隐式确认。
    /// 对应列表为空时无操作。</summary>
    private void EnterFacetJumpMode(FacetKind kind)
    {
        var list = kind == FacetKind.Project ? ProjectList : TagList;
        var items = kind == FacetKind.Project ? VM.Projects : VM.Tags;
        if (items.Count == 0) return;
        if (_jumpList != null && !ReferenceEquals(_jumpList, list))
            PreviewHighlight.SetIsActive(_jumpList, false);   // 切换列表：旧列表退出预览态
        if (!_facetJumpActive)   // 跳转模式中对侧列表再按 P/T = 切换列表：保留进入前的视图快照（Esc 还原对象）
        {
            _jumpPrevFacet = VM.SelectedFacet;
            _jumpPrevBlock = VM.SelectedBlock;
        }
        _facetJumpActive = true;
        _jumpList = list;
        PreviewHighlight.SetIsActive(list, true);   // 移动预览：选中条目呈浅色高亮
        // 预选：已在同类 facet 面板时落在该 facet，否则列表第一项
        var target = VM.SelectedFacet is { } current && current.Kind == kind ? current : items[0];
        list.SelectedItem = target;
        FocusFacetItem(list, target);
    }

    /// <summary>退出跳转模式：清模式标记与预览高亮态（视图快照由调用方先行取用）。</summary>
    private void ExitFacetJumpMode()
    {
        if (_jumpList != null) PreviewHighlight.SetIsActive(_jumpList, false);
        _facetJumpActive = false;
        _jumpList = null;
        _jumpPrevFacet = null;
        _jumpPrevBlock = null;
    }

    /// <summary>确认跳转：面板已是预览状态，焦点进任务列表（j/k 随即驱动任务选择）。</summary>
    private void CommitFacetJump()
    {
        ExitFacetJumpMode();
        ParkFocusOnTaskList();
    }

    /// <summary>取消跳转：恢复进入前的视图（facet 或区块；进入期间区块引用被 facet 顶掉，需显式恢复）。</summary>
    private void CancelFacetJump()
    {
        var prevFacet = _jumpPrevFacet;
        var prevBlock = _jumpPrevBlock;
        ExitFacetJumpMode();
        VM.SelectedFacet = prevFacet;
        if (prevFacet == null)
            VM.SelectedBlock = prevBlock;
        ParkFocusOnTaskList();
    }

    /// <summary>焦点离开跳转列表 = 跳转模式隐式确认（保留当前预览的面板），仅清理模式标记。</summary>
    internal void FacetList_FocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_facetJumpActive || !ReferenceEquals(sender, _jumpList)) return;
        if (_jumpList!.IsKeyboardFocusWithin) return;
        ExitFacetJumpMode();
    }

    /// <summary>项目/标签列表按键：jk 与 Ctrl+N/P（quick-open 语义）移动选中，焦点跟随选中项，
    /// 后续方向键走 ListBox 原生导航；Enter/Esc 在窗口 OnKeyDown 按跳转模式标记处理。</summary>
    internal void FacetList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        var delta = e.Key switch
        {
            Key.J when Keyboard.Modifiers == ModifierKeys.None => 1,
            Key.K when Keyboard.Modifiers == ModifierKeys.None => -1,
            Key.N when Keyboard.Modifiers == ModifierKeys.Control => 1,
            Key.P when Keyboard.Modifiers == ModifierKeys.Control => -1,
            _ => 0,
        };
        if (delta == 0) return;
        e.Handled = true;
        var count = list.Items.Count;
        if (count == 0) return;
        var i = list.SelectedIndex;
        var next = i < 0 ? (delta > 0 ? 0 : count - 1) : Math.Clamp(i + delta, 0, count - 1);
        var item = (FacetItemViewModel)list.Items[next];
        list.SelectedItem = item;
        FocusFacetItem(list, item);
    }

    /// <summary>把键盘焦点交给侧栏列表中指定项的容器（方向键原生导航从焦点项继续）。</summary>
    private void FocusFacetItem(ListBox list, FacetItemViewModel item)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is UIElement container)
                Keyboard.Focus(container);
            else
                list.Focus();
        }));
    }

    // ==================== 横向面板导航（h/l · ←/→） ====================

    /// <summary>任务区按左：回侧栏导航源——面板视图回 facet 列表并进入跳转预览（Esc 可恢复）；
    /// 区块视图聚焦区块列表的选中项（随后方向键原生移动即切区块）。</summary>
    private void NavigateToSidebar()
    {
        if (VM.SelectedFacet is { } facet)
        {
            EnterFacetJumpMode(facet.Kind);   // 预选当前 facet，进入跳转预览
            return;
        }
        var block = VM.SelectedBlock;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var container = block != null
                ? BlockList.ItemContainerGenerator.ContainerFromItem(block) as UIElement
                : null;
            (container ?? (UIElement)BlockList).Focus();
        }));
    }

    /// <summary>侧栏按右：确认并进任务区——跳转模式中视为确认（同 Enter）；
    /// 焦点落到当前选中任务，无选中时选中首项。</summary>
    private void NavigateToTaskList()
    {
        if (_facetJumpActive) CommitFacetJump();   // 右移 = 确认预览
        FocusTaskForArrow(Key.Right);   // 有选中归位，无选中选首项
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
