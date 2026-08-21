using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Stanza.App.Services;

namespace Stanza.App;

/// <summary>
/// 底部工具栏：清空（DONE/DELETE 区块）的二次确认——首次点击进入待确认状态
/// （图标变红 + 提示变化），3 秒无操作自动恢复。
/// </summary>
public partial class MainWindow
{
    private bool _clearArmed;
    private DispatcherTimer? _clearTimer;

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            // 第一次点击：进入待确认状态（图标变红 + 提示变化），3 秒无操作自动恢复
            _clearArmed = true;
            ClearButton.Foreground = (Brush)FindResource("DangerBrush");
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
}
