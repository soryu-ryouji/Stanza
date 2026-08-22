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
/// 标签/项目选择器（FacetPickerPanel）：右键菜单（标签…/项目…）或 T/P 打开，
/// 输入过滤 + 键盘高亮（方向键/Ctrl+N/P）+ 创建新名称。焦点始终锁输入框（VS Code quick-open 语义）。
/// 行构建/高亮状态机/开闭落位骨架见 MainWindow.Pickers.cs。
/// </summary>
public partial class MainWindow
{
    private FacetKind _pickerKind;

    private const string ClearSentinel = "\0";   // 高亮跟随键的哨兵值：尾部「清除」按钮

    /// <summary>由右键菜单（标签…/项目…）在鼠标位置打开选择器（Themes/TaskTemplates 转发调用），
    /// 或由快捷键（T/P）在选中任务旁打开（anchor 为锚点，null 时取鼠标位置）。
    /// 选择器是与主窗口同一视觉树的应用内浮层，不受 ContextMenu 关闭时的焦点回收影响。</summary>
    internal void OpenFacetPicker(FacetKind kind, Point? anchor = null)
    {
        if (!VM.HasSelection) return;
        CloseChoicePicker();   // 浮层互斥：同一时刻只开一个选择器
        _pickerKind = kind;
        _pickerHighlight = null;            // 初始为输入态
        _pickerHighlightNullable = true;
        FacetPickerInput.Tag = Loc.Get(kind == FacetKind.Tag ? "Picker_Tag" : "Picker_Project");
        FacetPickerInput.Text = "";
        FacetPickerError.Visibility = Visibility.Collapsed;
        RefreshFacetPicker();

        OpenPickerPanel(FacetPickerPanel, anchor, 320);
        // 与 ExitOverlay 同款：同一视觉树内直接聚焦输入框
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Keyboard.Focus(FacetPickerInput)));
    }

    private bool FacetPickerOpen => FacetPickerPanel.Visibility == Visibility.Visible;

    private void CloseFacetPicker() => ClosePickerPanel(FacetPickerPanel);

    /// <summary>当前高亮的跟随键（行名 / ClearSentinel / null），供行集重建后恢复。</summary>
    private string? FacetHighlightKey => _pickerHighlight switch
    {
        null => null,
        { } i when i >= _pickerItems.Count => ClearSentinel,
        { } i => _pickerItems[i].Key,
    };

    private void RefreshFacetPicker()
    {
        var filter = FacetPickerInput.Text.Trim();
        var prefix = _pickerKind == FacetKind.Tag ? "#" : "+";
        var highlightKey = FacetHighlightKey;   // 基于旧行集取出，重建后按键恢复

        _pickerItems = VM.FacetNames(_pickerKind)
            .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                // 标签：toggle 并保持开启（连续切换），高亮按键保留，焦点锁回输入框；
                // 项目：每条任务至多一个，应用即关闭
                Action apply = _pickerKind == FacetKind.Tag
                    ? () => { VM.ToggleTag(n); RefreshFacetPicker(); Keyboard.Focus(FacetPickerInput); }
                    : () => { VM.SetProjectForSelection(n); CloseFacetPicker(); };
                return new PickerItem
                {
                    Label = prefix + n,
                    Key = n,
                    IsCurrent = VM.SelectionHasFacet(_pickerKind, n),
                    AccentCheck = true,   // 已应用勾：非高亮强调蓝、高亮反白
                    Apply = apply,
                };
            })
            .ToList();

        // 「清除」是行列表之后的高亮尾部目标（固定在面板底部，不随滚动区）
        var clearVisible = VM.SelectionHasAnyFacet(_pickerKind);
        FacetPickerClear.Visibility = clearVisible ? Visibility.Visible : Visibility.Collapsed;
        _pickerTailButton = clearVisible ? FacetPickerClear : null;
        BuildPickerRows(FacetPickerRows);

        // 恢复高亮；跟随键已消失（被过滤/清除转不可见）时回落输入态
        SetPickerHighlight(highlightKey switch
        {
            null => null,
            ClearSentinel => _pickerTailButton != null ? _pickerItems.Count : null,
            var key => PickerItemIndexByKey(key),
        });
    }

    private int? PickerItemIndexByKey(string key)
    {
        for (var i = 0; i < _pickerItems.Count; i++)
            if (_pickerItems[i].Key == key) return i;
        return null;
    }

    private void FacetPickerInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        FacetPickerError.Visibility = Visibility.Collapsed;
        _pickerHighlight = null;   // 输入过滤时清空高亮：Enter 保持文本提交语义（创建/精确匹配）
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
        // 方向键与 Ctrl+N/P（选项导航的统一键位，两种键盘模式一致）移动高亮行。
        // Windows 模式 Alt 组是文本编辑手势、macOS 模式 Alt 扮演 Command（Alt+N=新建任务），均不用于选项导航；
        // 输入框内 Ctrl+N/P 的应用命令（新建任务）已在 OnPreProcessInput 让路（焦点在本面板内）
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
            e.Handled = true;   // 先于输入框的光标移动（单行框的 Home/End 语义）拦截
            MovePickerHighlight(navDelta);
            return;
        }
        // Space 选择高亮项（标签行 Apply=toggle 保持开启；项目行=应用并关闭）；
        // 无高亮时放行，作为输入框文本（标签/项目名不允许空格，提交时校验）
        if (e.Key == Key.Space && _pickerHighlight is { } picked)
        {
            e.Handled = true;
            ApplyFacetHighlight(picked, toggle: true);
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_pickerHighlight is { } h) ApplyFacetHighlight(h, toggle: false);   // 确认
            else CommitFacetPickerInput();   // 无高亮：提交输入文本（精确匹配或新建）
            return;
        }
        // 焦点不在输入框时（点击行到回焦之间的窗口期）拦截 Delete/Backspace，避免穿透到主列表删除任务；
        // 在输入框时放行，由编辑框自身消化（过滤输入的删字）
        if ((e.Key is Key.Back or Key.Delete) && e.OriginalSource is not TextBoxBase)
            e.Handled = true;
    }

    /// <summary>应用当前高亮目标（行或尾部「清除」）。toggle=true（Space/点击，选择）：
    /// 标签切换并保持打开（连续选择）、项目应用并关闭；toggle=false（Enter，确认）：
    /// 「清除」执行清空、项目应用高亮项并关闭、标签仅关闭——选择已由 Space 完成，
    /// Enter 不再切换（否则会撤销刚勾选的标签）。</summary>
    private void ApplyFacetHighlight(int index, bool toggle)
    {
        if (index >= _pickerItems.Count) { ApplyPickerClear(); return; }
        if (!toggle && _pickerKind == FacetKind.Tag) { CloseFacetPicker(); return; }
        _pickerItems[index].Apply();
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

    private void FacetPickerClear_MouseEnter(object sender, MouseEventArgs e)
        => SetPickerHighlight(_pickerItems.Count);   // 尾部目标索引

    /// <summary>「清除」：清空选中任务的该类 facet（标签全清 / 项目置空）并关闭浮层。</summary>
    private void ApplyPickerClear()
    {
        if (_pickerKind == FacetKind.Tag) VM.ClearTagsForSelection();
        else VM.SetProjectForSelection(null);
        CloseFacetPicker();
    }
}
