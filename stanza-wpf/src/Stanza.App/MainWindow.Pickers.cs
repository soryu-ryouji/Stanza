using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Stanza.App;

/// <summary>
/// 选择器骨架：FacetPicker（标签/项目，MainWindow.FacetPicker.cs）与 ChoicePicker（状态/优先级，
/// MainWindow.ChoicePicker.cs）共用的行描述符、行构建、高亮状态机与浮层开闭/落位。
/// 两个选择器与主窗口同一视觉树（非独立 Popup），互斥打开，共用 PickerLayer 承载；
/// 语义差异（连续 toggle / 单选 / 关闭时机 / 加速键）收进行 Apply 回调与各自的 KeyDown。
/// </summary>
public partial class MainWindow
{
    /// <summary>选择器的一行：文本 + 可选高亮跟随键 + 面板内加速键（命中即应用）+ 右侧键提示 +
    /// 当前值/已应用 ✓ 标记 + 行首徽章工厂（可空）+ 应用回调。
    /// 行在打开/刷新时即时构建：本地化与 ✓ 标记始终新鲜，无需 Loc.Changed 的刷新挂钩。
    /// 普通类（非记录）：行查找按引用相等（PickerItemIndexOf）。</summary>
    private sealed class PickerItem
    {
        public required string Label { get; init; }
        public string? Key { get; init; }                    // 高亮跟随键：行集重建后按它恢复高亮（FacetPicker 连续 toggle）
        public IReadOnlyList<Key> Keys { get; init; } = [];  // 面板内加速键（ChoicePicker）
        public string KeyHint { get; init; } = "";           // 右侧键提示（ChoicePicker）
        public bool IsCurrent { get; init; }                 // 右侧 ✓：ChoicePicker=当前值；FacetPicker=已应用
        public bool AccentCheck { get; init; }               // ✓ 非高亮时用强调蓝（FacetPicker）；默认跟随按钮前景
        public Func<FrameworkElement>? Badge { get; init; }  // 行首徽章（状态色点 / 象限小旗）
        public required Action Apply { get; init; }          // 应用动作（关闭/保持由回调内决定）
    }

    private IReadOnlyList<PickerItem> _pickerItems = [];
    private int? _pickerHighlight;          // 当前高亮索引；null = 无高亮（FacetPicker 输入态）
    private bool _pickerHighlightNullable;  // 高亮可否回落至 null（FacetPicker 输入态；ChoicePicker 不可）
    private Button? _pickerTailButton;      // 高亮循环的尾部目标（FacetPicker 的「清除」按钮），索引 = _pickerItems.Count
    private StackPanel? _pickerRowHost;     // 当前打开选择器的行容器

    // ---- 高亮状态机 ----

    /// <summary>高亮目标总数：行 + 尾部目标（「清除」可见时）。</summary>
    private int PickerTargetCount => _pickerItems.Count + (_pickerTailButton != null ? 1 : 0);

    /// <summary>方向键移动高亮：下界按选择器取 -1（回落至无高亮/输入态）或 0；两端停住不循环。</summary>
    private void MovePickerHighlight(int delta)
    {
        var count = PickerTargetCount;
        if (count == 0) return;
        var min = _pickerHighlightNullable ? -1 : 0;
        var next = Math.Clamp((_pickerHighlight ?? -1) + delta, min, count - 1);
        SetPickerHighlight(next < 0 ? null : next);
    }

    private void SetPickerHighlight(int? index)
    {
        if (_pickerHighlight == index) return;
        _pickerHighlight = index;
        UpdatePickerHighlightVisuals();
    }

    /// <summary>高亮迁移：覆写行按钮与尾部按钮的 Tag（PickerRowButton 样式以 Tag=true 渲染高亮行）。</summary>
    private void UpdatePickerHighlightVisuals()
    {
        if (_pickerRowHost != null)
        {
            var i = 0;
            foreach (var row in _pickerRowHost.Children.OfType<Button>())
                row.Tag = i++ == _pickerHighlight;
        }
        if (_pickerTailButton != null)
            _pickerTailButton.Tag = _pickerHighlight == _pickerItems.Count;
    }

    /// <summary>行的引用相等查找。</summary>
    private int PickerItemIndexOf(PickerItem item)
    {
        for (var i = 0; i < _pickerItems.Count; i++)
            if (ReferenceEquals(_pickerItems[i], item)) return i;
        return -1;
    }

    // ---- 行构建 ----

    /// <summary>按行描述重建行按钮（打开/刷新时调用；行数少，成本可忽略）。
    /// 徽章列仅在存在有徽章的行时保留（FacetPicker 全无徽章不缩进，与拆分前视觉一致）。
    /// 行样式随面板明暗取 PickerRowButton（深色）/LightPickerRowButton（浅色）。</summary>
    private void BuildPickerRows(StackPanel host, string rowStyleKey = "PickerRowButton")
    {
        _pickerRowHost = host;
        host.Children.Clear();
        var anyBadge = _pickerItems.Any(i => i.Badge != null);
        var rowStyle = (Style)FindResource(rowStyleKey);
        foreach (var item in _pickerItems)
        {
            var row = new Button
            {
                Style = rowStyle,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                DataContext = item,
                Content = MakePickerRowContent(item, anyBadge),
            };
            row.Click += PickerRow_Click;
            host.Children.Add(row);
        }
        UpdatePickerHighlightVisuals();
    }

    /// <summary>行内容：[徽章（可选列）] + 文本 + ✓（可选）+ 右侧键提示（可选）。
    /// 文字前景色绑定行按钮：高亮（悬停/键盘 Tag=true）时随按钮反白。</summary>
    private static Grid MakePickerRowContent(PickerItem item, bool badgeColumn)
    {
        var foreground = new Binding(nameof(Button.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        };
        var grid = new Grid();
        var column = 0;
        if (badgeColumn)
        {
            // 徽章列固定宽：无徽章行（无优先级）的文本仍与有徽章行左侧对齐
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            if (item.Badge is { } badgeFactory)
            {
                var badge = badgeFactory();
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.HorizontalAlignment = HorizontalAlignment.Left;
                grid.Children.Add(badge);
            }
            column++;
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock
        {
            Text = item.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(badgeColumn ? 9 : 0, 0, 0, 0),
        };
        name.SetBinding(TextBlock.ForegroundProperty, foreground);
        Grid.SetColumn(name, column);
        grid.Children.Add(name);

        if (item.IsCurrent)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            if (item.AccentCheck)
            {
                // 非高亮时强调蓝；高亮（Tag=true）/悬停时随按钮反白
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                    (Brush)Application.Current.FindResource("AccentBrush")));
                style.Triggers.Add(MakeInverseCheckTrigger(nameof(UIElement.IsMouseOver)));
                style.Triggers.Add(MakeInverseCheckTrigger(nameof(Button.Tag)));
                check.Style = style;
            }
            else
            {
                check.SetBinding(TextBlock.ForegroundProperty, foreground);
            }
            Grid.SetColumn(check, ++column);
            grid.Children.Add(check);
        }
        if (item.KeyHint.Length > 0)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var key = new TextBlock
            {
                Text = item.KeyHint,
                FontSize = 11,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
            };
            key.SetBinding(TextBlock.ForegroundProperty, foreground);
            Grid.SetColumn(key, ++column);
            grid.Children.Add(key);
        }
        return grid;
    }

    /// <summary>AccentCheck 勾的反白触发器：行按钮的指定属性（IsMouseOver / Tag）为 true 时勾转白。</summary>
    private static DataTrigger MakeInverseCheckTrigger(string property)
    {
        var trigger = new DataTrigger
        {
            Binding = new Binding(property)
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
            },
            Value = true,
        };
        trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
        return trigger;
    }

    // ---- 行交互（两个选择器共用）：点击 = 高亮 + 应用；悬停是纯视觉（行样式 IsMouseOver 弱档），
    // 不迁移键盘高亮——鼠标移走后键盘高亮保持在原行（VS Code quick-open 同款双轨语义） ----

    private void PickerRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PickerItem item }) return;
        var i = PickerItemIndexOf(item);
        if (i < 0) return;
        SetPickerHighlight(i);
        _pickerItems[i].Apply();
    }

    // ---- 浮层开闭与落位 ----

    /// <summary>打开选择器面板：在锚点（鼠标/选中任务）附近落位并夹取到窗口内
    /// （参照物用始终已布局的 Root：Collapsed 面板自身 ActualWidth/Height 为 0，不能作为参照）。
    /// 互斥关闭另一选择器与聚焦由调用方负责。</summary>
    private void OpenPickerPanel(FrameworkElement panel, Point? anchor, double estimatedHeight)
    {
        var pos = anchor ?? Mouse.GetPosition(Root);
        Canvas.SetLeft(panel,
            Math.Clamp(pos.X, 0, Math.Max(0, Root.ActualWidth - panel.Width - 8)));
        Canvas.SetTop(panel,
            Math.Clamp(pos.Y, 0, Math.Max(0, Root.ActualHeight - estimatedHeight)));
        panel.Visibility = Visibility.Visible;
        UpdatePickerLayerVisibility();
    }

    /// <summary>关闭选择器面板。焦点残留在已隐藏的面板内时停回任务列表（WPF 不自动迁移焦点，
    /// 否则焦点留在不可见元素上，Esc 关闭后 T/P 等裸键分发全部失效）。</summary>
    private void ClosePickerPanel(FrameworkElement panel)
    {
        panel.Visibility = Visibility.Collapsed;
        UpdatePickerLayerVisibility();
        if (Keyboard.FocusedElement is DependencyObject focus
            && VisualTreeEx.IsWithin(focus, panel))
            ParkFocusOnTaskList();
    }

    /// <summary>浮层互斥：打开任一选择器前关闭全部（同一时刻只开一个）。</summary>
    private void CloseAllPickers()
    {
        CloseFacetPicker();
        CloseChoicePicker();
        CloseDatePicker();
    }

    /// <summary>浮层承载三个选择器面板（标签/项目、通用选择、日期），任一可见即需要浮层拦截点击。</summary>
    private void UpdatePickerLayerVisibility()
        => PickerLayer.Visibility =
            FacetPickerPanel.Visibility == Visibility.Visible
            || ChoicePickerPanel.Visibility == Visibility.Visible
            || DatePickerPanel.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;

    /// <summary>点选择器卡片以外的区域关闭（点卡片内部不处理，由行/按钮自身响应）。</summary>
    private void PickerLayer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (!VisualTreeEx.IsWithin(source, FacetPickerPanel))
            CloseFacetPicker();
        if (!VisualTreeEx.IsWithin(source, ChoicePickerPanel))
            CloseChoicePicker();
        if (!VisualTreeEx.IsWithin(source, DatePickerPanel))
            CloseDatePicker();
    }
}
