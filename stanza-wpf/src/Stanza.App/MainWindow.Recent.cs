using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Stanza.App.ViewModels;

namespace Stanza.App;

/// <summary>
/// 最近打开文件弹层（左下角）：打开/循环切换（Ctrl+R，VS Code quick-open 语义）、
/// 键盘高亮行、条目移除、点击外部关闭。数据与命令在 RecentFilesViewModel。
/// </summary>
public partial class MainWindow
{
    private int _recentCycleIndex = -1;   // 弹层内当前高亮行（-1 = 无键盘循环高亮）

    private void InitializeRecentPopup()
    {
        // 弹层打开时：从底部向上滑入 + 淡入
        RecentPopup.Opened += (_, _) =>
        {
            RecentPanel.RenderTransform.BeginAnimation(
                TranslateTransform.YProperty,
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
}
