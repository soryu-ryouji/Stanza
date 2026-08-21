using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Stanza.App.Services;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App;

/// <summary>
/// 两个选择器浮层（与主窗口同一视觉树，非独立 Popup）：标签/项目选择器（FacetPickerPanel，
/// 右键菜单或 T/P 打开，输入过滤 + 键盘高亮 + 创建新名称）与通用选择面板（ChoicePickerPanel，
/// 状态 M / 优先级 Shift+P，加速键直达 + 高亮循环）。二者互斥打开，共用 PickerLayer 承载。
/// </summary>
public partial class MainWindow
{
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
        CloseChoicePicker();   // 浮层互斥：同一时刻只开一个选择器
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

    /// <summary>浮层承载两个选择器面板（标签/项目、通用选择），任一可见即需要浮层拦截点击。</summary>
    private void UpdatePickerLayerVisibility()
        => PickerLayer.Visibility =
            FacetPickerPanel.Visibility == Visibility.Visible
            || ChoicePickerPanel.Visibility == Visibility.Visible
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
        if (!VisualTreeEx.IsWithin(source, ChoicePickerPanel))
            CloseChoicePicker();
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

    private void FacetPickerRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PickerRow row) SetHighlight(row.Name);
    }

    private void FacetPickerClear_MouseEnter(object sender, MouseEventArgs e)
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

    // ==================== 通用选择面板（状态 M / 优先级 Shift+P） ====================

    private IReadOnlyList<ChoiceItem> _choiceItems = [];
    private int _choiceHighlight;
    private (ModifierKeys Modifiers, Key Key) _choiceToggleKey;   // 再按激活键 = 关闭（开关语义）

    /// <summary>选择面板的一行：文本 + 面板内加速键（命中即应用）+ 右侧键提示 + 当前值 ✓ 标记 +
    /// 行首徽章工厂（可空：无徽章行的文本仍与有徽章行对齐，徽章列固定宽）+ 应用回调。
    /// 行在打开时即时构建：本地化与当前值标记始终新鲜，也无需 Loc.Changed 的刷新挂钩。
    /// 普通类（非记录）：行查找按引用相等（ChoiceIndexOf）。</summary>
    private sealed class ChoiceItem
    {
        public required string Label { get; init; }
        public required IReadOnlyList<Key> Keys { get; init; }
        public required string KeyHint { get; init; }
        public required bool IsCurrent { get; init; }
        public Func<FrameworkElement>? Badge { get; init; }
        public required Action Apply { get; init; }
    }

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

        var items = new List<ChoiceItem>();
        for (var i = 0; i < TaskStateNames.CanonicalOrder.Length; i++)
        {
            var state = TaskStateNames.CanonicalOrder[i];   // 循环体内局部变量：闭包按迭代捕获
            items.Add(new ChoiceItem
            {
                Label = Loc.StateName(state),
                Keys = new[] { Key.D1 + i, Key.NumPad1 + i },
                KeyHint = (i + 1).ToString(),
                IsCurrent = uniform && current == state,
                Badge = () => new Ellipse { Width = 7, Height = 7, Fill = StateToBrushConverter.Of(state) },
                Apply = () => ApplyMoveTo(state),
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

        var items = new List<ChoiceItem>();
        foreach (var option in VM.PriorityOptions)   // 循环体内闭包：option 按迭代捕获
        {
            if (option.Value is { } q)   // 象限行 A-D
            {
                var digit = items.Count + 1;
                items.Add(new ChoiceItem
                {
                    Label = Loc.Get($"Priority_Desc_{q}"),
                    Keys = new[] { Key.D0 + digit, Key.NumPad0 + digit },
                    KeyHint = digit.ToString(),
                    IsCurrent = uniform && current == q,
                    Badge = () => new TextBlock
                    {
                        Text = "\uE7C1",   // 小旗（象限着色，与右键菜单优先级同一旗形）
                        FontFamily = (FontFamily)FindResource("IconFont"),
                        FontSize = 11,
                        Foreground = QuadrantToBrushConverter.Of(q),
                    },
                    Apply = () => VM.SetPriorityForSelection(q),
                });
            }
            else   // 无优先级行（不带徽章，文本与旗子行左侧对齐）
            {
                items.Add(new ChoiceItem
                {
                    Label = option.Label,
                    Keys = new[] { Key.D0, Key.NumPad0 },
                    KeyHint = "0",
                    IsCurrent = uniform && current == null,
                    Apply = () => VM.SetPriorityForSelection(null),
                });
            }
        }
        OpenChoicePicker(items, ModifierKeys.Shift, Key.P, anchor);
    }

    // ---- 面板主体 ----

    /// <summary>打开选择面板：互斥关闭标签选择器、构建行、落位夹取（参照物用已布局的 Root，
    /// Collapsed 面板自身无尺寸）。焦点锁面板：全部按键在 ChoicePicker_KeyDown 统一处理。</summary>
    private void OpenChoicePicker(IReadOnlyList<ChoiceItem> items, ModifierKeys toggleModifiers, Key toggleKey, Point? anchor)
    {
        CloseFacetPicker();   // 浮层互斥：同一时刻只开一个选择器
        _choiceItems = items;
        _choiceToggleKey = (toggleModifiers, toggleKey);
        // 初始高亮：当前值行；无当前值（混合选中）时落在第一行
        var current = -1;
        for (var i = 0; i < items.Count; i++)
            if (items[i].IsCurrent) { current = i; break; }
        _choiceHighlight = current >= 0 ? current : 0;
        RebuildChoiceRows();

        var pos = anchor ?? Mouse.GetPosition(Root);
        Canvas.SetLeft(ChoicePickerPanel,
            Math.Clamp(pos.X, 0, Math.Max(0, Root.ActualWidth - ChoicePickerPanel.Width - 8)));
        Canvas.SetTop(ChoicePickerPanel,
            Math.Clamp(pos.Y, 0, Math.Max(0, Root.ActualHeight - 180)));

        ChoicePickerPanel.Visibility = Visibility.Visible;
        UpdatePickerLayerVisibility();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Keyboard.Focus(ChoicePickerPanel)));
    }

    private void CloseChoicePicker()
    {
        ChoicePickerPanel.Visibility = Visibility.Collapsed;
        UpdatePickerLayerVisibility();
        if (Keyboard.FocusedElement is DependencyObject focus
            && VisualTreeEx.IsWithin(focus, ChoicePickerPanel))
            ParkFocusOnTaskList();
    }

    /// <summary>按行描述重建行按钮（每次打开调用，行数少，成本可忽略）。</summary>
    private void RebuildChoiceRows()
    {
        ChoicePickerRows.Children.Clear();
        foreach (var item in _choiceItems)
        {
            var row = new Button
            {
                Style = (Style)FindResource("PickerRowButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                DataContext = item,
                Content = MakeChoiceRowContent(item),
            };
            row.Click += ChoiceRow_Click;
            row.MouseEnter += ChoiceRow_MouseEnter;
            ChoicePickerRows.Children.Add(row);
        }
        UpdateChoiceHighlightVisuals();
    }

    /// <summary>行内容：徽章（状态色点 / 象限字母）+ 文本 + 当前值 ✓ + 右侧键提示。
    /// 文字前景色绑定行按钮：高亮（悬停/键盘 Tag=true）时随按钮反白。</summary>
    private Grid MakeChoiceRowContent(ChoiceItem item)
    {
        var foreground = new Binding(nameof(Button.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        };
        var grid = new Grid();
        // 徽章列固定宽：无徽章行（无优先级）的文本与有徽章行左侧对齐
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (item.Badge is { } badgeFactory)
        {
            var badge = badgeFactory();
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(badge);
        }
        var name = new TextBlock
        {
            Text = item.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
        };
        name.SetBinding(TextBlock.ForegroundProperty, foreground);
        var check = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = item.IsCurrent ? Visibility.Visible : Visibility.Collapsed,
        };
        check.SetBinding(TextBlock.ForegroundProperty, foreground);
        var key = new TextBlock
        {
            Text = item.KeyHint,
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        key.SetBinding(TextBlock.ForegroundProperty, foreground);

        Grid.SetColumn(name, 1);
        Grid.SetColumn(check, 2);
        Grid.SetColumn(key, 3);
        grid.Children.Add(name);
        grid.Children.Add(check);
        grid.Children.Add(key);
        return grid;
    }

    // 焦点锁在面板上：加速键直达、方向键/jk/Ctrl+N/P 移动高亮、Enter/Space 确认、
    // Esc 或再按激活键关闭（开关语义）
    private void ChoicePicker_KeyDown(object sender, KeyEventArgs e)
    {
        for (var i = 0; i < _choiceItems.Count; i++)
            if (_choiceItems[i].Keys.Contains(e.Key))
            {
                e.Handled = true;
                ApplyChoice(i);
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
                CycleChoiceHighlight(-1);
                return;
            case Key.Down:
            case Key.J:
                e.Handled = true;
                CycleChoiceHighlight(1);
                return;
            // Ctrl+N/P（quick-open 语义）：Windows 模式下 Ctrl+N 的应用命令分发已在
            // OnPreProcessInput 让路（焦点在面板内）；macOS 模式该组合本来空闲
            case Key.N when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                CycleChoiceHighlight(1);
                return;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control:
                e.Handled = true;
                CycleChoiceHighlight(-1);
                return;
            case Key.Enter:
            case Key.Space:
                e.Handled = true;
                ApplyChoice(_choiceHighlight);
                return;
        }
    }

    private void CycleChoiceHighlight(int delta)
    {
        if (_choiceItems.Count == 0) return;
        _choiceHighlight = Math.Clamp(_choiceHighlight + delta, 0, _choiceItems.Count - 1);
        UpdateChoiceHighlightVisuals();
    }

    /// <summary>高亮迁移：覆写行按钮 Tag（PickerRowButton 样式以 Tag=true 渲染高亮行）。</summary>
    private void UpdateChoiceHighlightVisuals()
    {
        var i = 0;
        foreach (var row in ChoicePickerRows.Children.OfType<Button>())
            row.Tag = i++ == _choiceHighlight;
    }

    /// <summary>行的引用相等查找。</summary>
    private int ChoiceIndexOf(ChoiceItem item)
    {
        for (var i = 0; i < _choiceItems.Count; i++)
            if (ReferenceEquals(_choiceItems[i], item)) return i;
        return -1;
    }

    private void ChoiceRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Button { DataContext: ChoiceItem item }) return;
        var i = ChoiceIndexOf(item);
        if (i < 0 || i == _choiceHighlight) return;
        _choiceHighlight = i;
        UpdateChoiceHighlightVisuals();
    }

    private void ChoiceRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChoiceItem item }) return;
        var i = ChoiceIndexOf(item);
        if (i >= 0) ApplyChoice(i);
    }

    /// <summary>应用并关闭；流转后的焦点落位由各行 Apply 回调负责。</summary>
    private void ApplyChoice(int index)
    {
        CloseChoicePicker();
        _choiceItems[index].Apply();
    }
}
