using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Stanza.App.Services;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App;

/// <summary>
/// 通用选择面板（ChoicePickerPanel）：状态（M / 右键「状态…」）与优先级（Shift+P）两个场景共用。
/// 行在打开时按描述符即时构建（本地化与当前值标记始终新鲜）；加速键直达、方向键/jk + Enter 确认、
/// Esc 或再按激活键关闭（开关语义）。行构建/高亮状态机/开闭落位骨架见 MainWindow.Pickers.cs。
/// </summary>
public partial class MainWindow
{
    private (ModifierKeys Modifiers, Key Key) _choiceToggleKey;   // 再按激活键 = 关闭（开关语义）

    // ---- 入口：状态（移到…） ----

    /// <summary>由快捷键（M，锚点为选中任务右上角）或右键菜单「状态…」（鼠标位置）打开。
    /// 四个目标按规范序：数字 1-4 直达（与 Alt+1~4 的区块序号同义）。
    /// 选中任务状态一致时当前状态行标 ✓，多选混合不标。</summary>
    internal void OpenMovePicker(Point? anchor = null)
    {
        if (!VM.HasSelection) return;
        var states = VM.SelectedTasks.Select(t => t.State).Distinct().ToList();
        var uniform = states.Count == 1;
        var current = uniform ? states[0] : (TaskState?)null;

        var items = new List<PickerItem>();
        for (var i = 0; i < TaskStateNames.CanonicalOrder.Length; i++)
        {
            var state = TaskStateNames.CanonicalOrder[i];   // 循环体内局部变量：闭包按迭代捕获
            items.Add(new PickerItem
            {
                Label = Loc.StateName(state),
                Keys = new[] { Key.D1 + i, Key.NumPad1 + i },
                KeyHint = (i + 1).ToString(),
                IsCurrent = uniform && current == state,
                Badge = () => new Ellipse { Width = 7, Height = 7, Fill = StateToBrushConverter.Of(state) },
                Apply = () => { CloseChoicePicker(); ApplyMoveTo(state); },
            });
        }
        OpenChoicePicker(items, ModifierKeys.None, Key.M, anchor);
    }

    /// <summary>状态行的应用：流转并把焦点落位到空缺处（同 Delete/Backspace 路径，支持连续操作）。</summary>
    private void ApplyMoveTo(TaskState state)
    {
        var index = FirstSelectedIndex();
        VM.MoveSelectionTo(state);
        FocusTaskAtIndex(index);
    }

    // ---- 入口：优先级 ----

    /// <summary>由快捷键（Shift+P，锚点为选中任务右上角）打开。数字 1-4 对应象限 A-D
    /// （Todoist/Linear 的数字选级惯例），0 = 无优先级（Linear 的键位惯例）。
    /// 行首徽章为象限着色的小旗（与右键菜单优先级同一旗形）；无优先级行不带徽章。
    /// 优先级只属于活跃任务：全归档选中不响应（分发处按 HasActiveSelection 拦截，这里再兜底）。
    /// 选中活跃任务优先级一致时标 ✓（均无优先级标在无优先级行），混合不标。
    /// 行描述取自 PriorityOptions（与右键子菜单同源）。</summary>
    internal void OpenPriorityPicker(Point? anchor = null)
    {
        if (!VM.HasActiveSelection) return;
        var priorities = VM.SelectedTasks.Where(t => t.IsActive).Select(t => t.Priority).Distinct().ToList();
        var uniform = priorities.Count == 1;
        var current = uniform ? priorities[0] : (char?)null;

        var items = new List<PickerItem>();
        foreach (var option in VM.PriorityOptions)   // 循环体内闭包：option 按迭代捕获
        {
            if (option.Value is { } q)   // 象限行 A-D
            {
                var digit = items.Count + 1;
                items.Add(new PickerItem
                {
                    Label = Loc.Get($"Priority_Desc_{q}"),
                    Keys = new[] { Key.D0 + digit, Key.NumPad0 + digit },
                    KeyHint = digit.ToString(),
                    IsCurrent = uniform && current == q,
                    Badge = () => new TextBlock
                    {
                        Text = "",   // 小旗（象限着色，与右键菜单优先级同一旗形）
                        FontFamily = (FontFamily)FindResource("IconFont"),
                        FontSize = 11,
                        Foreground = QuadrantToBrushConverter.Of(q),
                    },
                    Apply = () => { CloseChoicePicker(); VM.SetPriorityForSelection(q); },
                });
            }
            else   // 无优先级行（不带徽章，文本与旗子行左侧对齐）
            {
                items.Add(new PickerItem
                {
                    Label = option.Label,
                    Keys = new[] { Key.D0, Key.NumPad0 },
                    KeyHint = "0",
                    IsCurrent = uniform && current == null,
                    Apply = () => { CloseChoicePicker(); VM.SetPriorityForSelection(null); },
                });
            }
        }
        OpenChoicePicker(items, ModifierKeys.Shift, Key.P, anchor);
    }

    // ---- 面板主体 ----

    /// <summary>打开选择面板：互斥关闭标签选择器、构建行、落位夹取。焦点锁面板：
    /// 全部按键在 ChoicePicker_KeyDown 统一处理。</summary>
    private void OpenChoicePicker(IReadOnlyList<PickerItem> items, ModifierKeys toggleModifiers, Key toggleKey, Point? anchor)
    {
        CloseFacetPicker();   // 浮层互斥：同一时刻只开一个选择器
        _pickerItems = items;
        _pickerTailButton = null;
        _pickerHighlightNullable = false;
        _choiceToggleKey = (toggleModifiers, toggleKey);
        // 初始高亮：当前值行；无当前值（混合选中）时落在第一行
        var current = -1;
        for (var i = 0; i < items.Count; i++)
            if (items[i].IsCurrent) { current = i; break; }
        _pickerHighlight = current >= 0 ? current : 0;
        BuildPickerRows(ChoicePickerRows);

        OpenPickerPanel(ChoicePickerPanel, anchor, 180);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Keyboard.Focus(ChoicePickerPanel)));
    }

    private void CloseChoicePicker() => ClosePickerPanel(ChoicePickerPanel);

    // 焦点锁在面板上：加速键直达、方向键/jk/Ctrl+N/P 移动高亮、Enter/Space 确认、
    // Esc 或再按激活键关闭（开关语义）
    private void ChoicePicker_KeyDown(object sender, KeyEventArgs e)
    {
        for (var i = 0; i < _pickerItems.Count; i++)
            if (_pickerItems[i].Keys.Contains(e.Key))
            {
                e.Handled = true;
                _pickerItems[i].Apply();   // 行 Apply 内含关闭
                return;
            }
        if (e.Key == _choiceToggleKey.Key && Keyboard.Modifiers == _choiceToggleKey.Modifiers)
        {
            e.Handled = true;
            CloseChoicePicker();
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseChoicePicker();
                return;
            case Key.Up:
            case Key.K:
                e.Handled = true;
                MovePickerHighlight(-1);
                return;
            case Key.Down:
            case Key.J:
                e.Handled = true;
                MovePickerHighlight(1);
                return;
            // Ctrl+N/P（quick-open 语义）：Windows 模式下 Ctrl+N 的应用命令分发已在
            // OnPreProcessInput 让路（焦点在面板内）；macOS 模式该组合本来空闲
            case Key.N when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                MovePickerHighlight(1);
                return;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                MovePickerHighlight(-1);
                return;
            case Key.Enter:
            case Key.Space:
                e.Handled = true;
                if (_pickerHighlight is { } h) _pickerItems[h].Apply();   // 行 Apply 内含关闭
                return;
        }
    }
}
