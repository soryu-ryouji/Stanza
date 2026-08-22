using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Stanza.App.Services;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App;

/// <summary>
/// 日期选择器（DatePickerPanel）：右键菜单「截止…」、工具栏按钮或 D 打开。
/// 输入框手输日期（yyyy-MM-dd）+ 快捷预设行（今天/明天/一周后/清除截止）。
/// 焦点始终锁输入框（同标签选择器的 quick-open 语义）；方向键/Ctrl+N/P 移动高亮，
/// Enter 应用高亮行或提交输入，Esc 关闭。行构建/高亮状态机/开闭落位骨架见 MainWindow.Pickers.cs。
/// </summary>
public partial class MainWindow
{
    /// <summary>工具栏「截止」按钮入口：锚点为选中任务右上角（同 D 键路径）。</summary>
    internal void DueDateButton_Click(object sender, RoutedEventArgs e)
        => OpenDuePicker(SelectedTaskAnchor());

    /// <summary>由右键菜单（鼠标位置）、工具栏按钮或快捷键（D，锚点为选中任务右上角）打开。
    /// 选择器是与主窗口同一视觉树的应用内浮层（不用 WPF DatePicker 的 Popup 日历——决策：浮层不进独立 HWND）。</summary>
    internal void OpenDuePicker(Point? anchor = null)
    {
        if (!VM.HasActiveSelection) return;
        CloseAllPickers();   // 浮层互斥：同一时刻只开一个选择器
        _pickerHighlight = null;            // 初始为输入态
        _pickerHighlightNullable = true;
        _pickerTailButton = null;
        DatePickerError.Visibility = Visibility.Collapsed;
        // 预填当前截止日（选中一致时；混合选中留空）
        var dues = VM.SelectedTasks.Where(t => t.IsActive).Select(t => t.Due).Distinct().ToList();
        var uniform = dues.Count == 1 ? dues[0] : null;
        DatePickerInput.Text = uniform is { } d ? d.ToString("yyyy-MM-dd") : "";
        RefreshDuePickerRows();

        // 月历定位并选中当前截止（无则停今天所在月；初始化不触发 DatePicked）
        DueCalendar.SelectedDate = uniform;

        OpenPickerPanel(DatePickerPanel, anchor, 420);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Keyboard.Focus(DatePickerInput)));
    }

    private void CloseDatePicker() => ClosePickerPanel(DatePickerPanel);

    /// <summary>月历点选日期：应用并关闭（构造时在 MainWindow.xaml.cs 挂接）。</summary>
    private void OnDueDatePicked(DateOnly date)
    {
        VM.SetDueForSelection(date);
        CloseDatePicker();
    }

    /// <summary>快捷预设：今天 / 明天 / 一周后（纯文本不带日期值——右侧周历已提供日期参照）；
    /// 选中任务的当前截止与预设一致时标 ✓（混合不标）。「清除截止」是面板底部的尾部目标，不在行列表。</summary>
    private void RefreshDuePickerRows()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dues = VM.SelectedTasks.Where(t => t.IsActive).Select(t => t.Due).Distinct().ToList();
        var uniform = dues.Count == 1 ? dues[0] : null;

        PickerItem Row(string labelKey, DateOnly date)
        {
            var d = date;
            return new PickerItem
            {
                Label = Loc.Get(labelKey),
                IsCurrent = uniform == d,
                Apply = () => { CloseDatePicker(); VM.SetDueForSelection(d); },
            };
        }

        _pickerItems = new List<PickerItem>
        {
            Row("DuePicker_Today", today),
            Row("DuePicker_Tomorrow", today.AddDays(1)),
            Row("DuePicker_NextWeek", today.AddDays(7)),
        };
        // 「清除截止」固定在面板最底部（周历下方），仅存在当前截止时可见；参与高亮循环（尾部目标）
        DueClearButton.Visibility = uniform != null ? Visibility.Visible : Visibility.Collapsed;
        _pickerTailButton = uniform != null ? DueClearButton : null;
        BuildPickerRows(DatePickerRows);
    }

    private void DueClear_Click(object sender, RoutedEventArgs e)
    {
        VM.SetDueForSelection(null);
        CloseDatePicker();
    }

    private void DatePickerInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        DatePickerError.Visibility = Visibility.Collapsed;
        _pickerHighlight = null;   // 输入时清空高亮：Enter 保持文本提交语义
    }

    // 挂在弹层面板上（隧道）：焦点锁定在输入框，按键统一在此处理
    private void DatePicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseDatePicker();
            return;
        }
        // 方向键与 Ctrl+N/P（选项导航的统一键位，两种键盘模式一致）移动高亮行
        var navDelta = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.P when Keyboard.Modifiers == ModifierKeys.Control => -1,
            Key.N when Keyboard.Modifiers == ModifierKeys.Control => 1,
            _ => 0,
        };
        if (navDelta != 0)
        {
            e.Handled = true;   // 先于输入框的光标移动拦截
            MovePickerHighlight(navDelta);
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_pickerHighlight is { } h && h < _pickerItems.Count) _pickerItems[h].Apply();
            else CommitDueInput();   // 无高亮：提交输入文本
            return;
        }
        // 焦点窗口期拦截 Delete/Backspace，避免穿透到主列表删除任务（同标签选择器先例）
        if ((e.Key is Key.Back or Key.Delete) && e.OriginalSource is not TextBoxBase)
            e.Handled = true;
    }

    /// <summary>回车提交输入：经 DueDateInput 宽松解析（完整日期或月日缩写），失败显示错误提示。</summary>
    private void CommitDueInput()
    {
        var text = DatePickerInput.Text.Trim();
        if (text.Length == 0) return;
        if (!DueDateInput.TryParse(text, out var date))
        {
            DatePickerError.Text = Loc.Get("DuePicker_Error");
            DatePickerError.Visibility = Visibility.Visible;
            return;
        }
        VM.SetDueForSelection(date);
        CloseDatePicker();
    }
}
