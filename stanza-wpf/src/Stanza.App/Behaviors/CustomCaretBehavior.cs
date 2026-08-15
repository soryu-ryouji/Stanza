using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Stanza.App.Services;

namespace Stanza.App.Behaviors;

/// <summary>
/// 自绘文本光标。WPF 系统光标宽度固定 1px 且无公开 API 可调，高度取字体行高（含行间距，
/// 雅黑 12px 行高约 17px），顶部明显高出字形墨水，观感细弱且偏上（AvalonEdit 等编辑器
/// 因此都自绘光标）。启用后把 CaretBrush 置透明隐藏系统光标，改绘 2px 圆角竖条：
/// 垂直范围对齐字形（基线 −1em 至基线 + 下延墨水），横向对齐物理像素，按系统频率闪烁。
/// 颜色仍取 TextBox.CaretBrush 的原有效值，XAML 声明点不变。
/// </summary>
public static class CustomCaretBehavior
{
    /// <summary>光标条宽度（DIP）。</summary>
    private const double CaretWidth = 2.0;

    // 光标垂直范围的字形墨水比例（Segoe UI / 雅黑实测经验值）：
    // 基线上 0.9em 覆盖汉字与大写字母顶，基线下 0.22em 覆盖 g/j/p 下延。
    // 不用 FormattedText.Extent：复合字体回退链的最大下延（含声调/组合符）实测达 1.18em，远超常规字形
    private const double InkAscentEm = 0.9;
    private const double InkDescentEm = 0.22;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(CustomCaretBehavior),
            new FrameworkPropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    // 每个 TextBox 一份运行状态，随控件回收
    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State", typeof(CaretState), typeof(CustomCaretBehavior),
            new FrameworkPropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        if ((bool)e.NewValue)
        {
            var state = new CaretState(box);
            box.SetValue(StateProperty, state);
            state.Attach();
        }
        else
        {
            var state = (CaretState?)box.GetValue(StateProperty);
            box.ClearValue(StateProperty);
            state?.Dispose();
        }
    }

    private sealed class CaretState
    {
        private readonly TextBox _box;
        private readonly DispatcherTimer _blink;
        private CaretAdorner? _adorner;
        private Brush _brush = Brushes.Black;
        private object? _originalCaretBrush; // 还原用：启用前的本地值（含绑定表达式）或 UnsetValue
        private Rect _caretRect = Rect.Empty;
        private bool _phase = true;          // 闪烁相位：true = 显示
        private Window? _window;

        public CaretState(TextBox box)
        {
            _box = box;
            _blink = new DispatcherTimer();
            _blink.Tick += OnBlinkTick;
        }

        public void Attach()
        {
            // 先取有效值接力给自绘光标，再置透明隐藏系统光标（覆盖样式 setter）
            _originalCaretBrush = _box.ReadLocalValue(TextBoxBase.CaretBrushProperty);
            _brush = _box.CaretBrush;
            _box.CaretBrush = Brushes.Transparent;

            _box.GotKeyboardFocus += OnGotFocus;
            _box.LostKeyboardFocus += OnLostFocus;
            _box.SelectionChanged += OnSelectionChanged;
            // ScrollChanged 是 ScrollViewer 的附加路由事件，TextBox 上不直接暴露，经 AddHandler 订阅
            _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            _box.SizeChanged += OnSizeChanged;
            _box.IsVisibleChanged += OnIsVisibleChanged;
            _box.Loaded += OnLoaded;
            _box.Unloaded += OnUnloaded;

            if (_box.IsKeyboardFocused) Show();
        }

        public void Dispose()
        {
            StopBlink();
            RemoveAdorner();
            UnhookWindow();
            _box.GotKeyboardFocus -= OnGotFocus;
            _box.LostKeyboardFocus -= OnLostFocus;
            _box.SelectionChanged -= OnSelectionChanged;
            _box.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            _box.SizeChanged -= OnSizeChanged;
            _box.IsVisibleChanged -= OnIsVisibleChanged;
            _box.Loaded -= OnLoaded;
            _box.Unloaded -= OnUnloaded;

            if (_originalCaretBrush is BindingBase binding)
                _box.SetBinding(TextBoxBase.CaretBrushProperty, binding);
            else if (ReferenceEquals(_originalCaretBrush, DependencyProperty.UnsetValue))
                _box.ClearValue(TextBoxBase.CaretBrushProperty);
            else
                _box.SetValue(TextBoxBase.CaretBrushProperty, _originalCaretBrush);
        }

        // ==================== 显示 / 隐藏 ====================

        /// <summary>聚焦/输入后的完整刷新：重算位置、重置为显示相位并重启闪烁（击键后光标立即可见）。</summary>
        private void Show()
        {
            if (!UpdateCaretRect()) { Hide(); return; }
            EnsureAdorner();
            if (_adorner == null) return; // 尚未上视觉树，等 Loaded 兜底
            _phase = true;
            _adorner.Update(_caretRect, _brush, true);
            StartBlink();
        }

        private void Hide()
        {
            StopBlink();
            _adorner?.Update(Rect.Empty, _brush, false);
        }

        /// <summary>滚动/布局变化时的位置跟随：不重置闪烁相位与节奏。</summary>
        private void Refresh()
        {
            if (_adorner == null) return;
            if (UpdateCaretRect()) _adorner.Update(_caretRect, _brush, _phase);
            else _adorner.Update(Rect.Empty, _brush, false);
        }

        /// <summary>重算光标矩形；返回 false 表示当前不应显示（未聚焦/有选区/只读/不可见）。</summary>
        private bool UpdateCaretRect()
        {
            if (!_box.IsKeyboardFocused || !_box.IsVisible) return false;
            if (_box.IsReadOnly && !_box.IsReadOnlyCaretVisible) return false;
            if (_box.SelectionLength > 0) return false;

            Rect r = _box.GetRectFromCharacterIndex(_box.CaretIndex);
            if ((r.IsEmpty || r.Height <= 0) && _box.Text.Length == 0)
            {
                // 空文本时部分场景返回 Empty：按对齐方式推算行首位置
                double lineHeight = _box.FontSize * _box.FontFamily.LineSpacing;
                double x = _box.TextAlignment switch
                {
                    TextAlignment.Center => _box.ActualWidth / 2,
                    TextAlignment.Right => _box.ActualWidth,
                    _ => 0,
                };
                double y = _box.VerticalContentAlignment switch
                {
                    VerticalAlignment.Center => Math.Max(0, (_box.ActualHeight - lineHeight) / 2),
                    VerticalAlignment.Bottom => Math.Max(0, _box.ActualHeight - lineHeight),
                    _ => 0,
                };
                r = new Rect(x, y, 0, lineHeight);
            }
            if (r.IsEmpty || r.Height <= 0) return false;

            // 行高 → 字形墨水区：系统光标占满行高（含行间距），顶部高出字形一截；
            // 收到 em 顶（基线 −1em）至下延墨水底（基线 + Extent），与文字视觉对齐
            double dpi = VisualTreeHelper.GetDpi(_box).PixelsPerDip;
            var probe = new FormattedText("Ag国", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight, _box.FontStretch),
                _box.FontSize, Brushes.Black, dpi);
            double caretX = r.X;
            if (_box.Text.Length == 0 && _box.TextAlignment == TextAlignment.Center && _box.Tag is string hint && hint.Length > 0)
            {
                // 水印显示中：居中对齐的空文本插入点在正中心，光标会穿过水印文字；
                // 移到水印首字左缘（留 1px 间隙），示意「从这里开始输入」
                var wm = new FormattedText(hint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight, _box.FontStretch),
                    _box.FontSize, Brushes.Black, dpi);
                caretX -= wm.WidthIncludingTrailingWhitespace / 2 + 1;
            }

            double top = r.Top + probe.Baseline - _box.FontSize * InkAscentEm;
            double height = _box.FontSize * (InkAscentEm + InkDescentEm);
            _caretRect = new Rect(caretX, top, CaretWidth, Math.Max(height, 2));
            return true;
        }

        // ==================== 闪烁 ====================

        private void StartBlink()
        {
            // WPF SystemParameters 无闪烁间隔属性，取 Win32 值；0 / 0xFFFFFFFF 表示系统设为不闪烁：常亮
            uint ms = NativeMethods.GetCaretBlinkTime();
            if (ms == 0 || ms == uint.MaxValue) return;
            _blink.Interval = TimeSpan.FromMilliseconds(ms);
            _blink.Stop();
            _blink.Start();
        }

        private void StopBlink() => _blink.Stop();

        private void OnBlinkTick(object? sender, EventArgs e)
        {
            _phase = !_phase;
            _adorner?.Update(_caretRect, _brush, _phase);
        }

        // ==================== Adorner ====================

        private void EnsureAdorner()
        {
            if (_adorner != null) return;
            var layer = AdornerLayer.GetAdornerLayer(_box);
            if (layer == null) return;
            _adorner = new CaretAdorner(_box);
            layer.Add(_adorner);
        }

        private void RemoveAdorner()
        {
            if (_adorner == null) return;
            AdornerLayer.GetAdornerLayer(_box)?.Remove(_adorner);
            _adorner = null;
        }

        // ==================== 事件 ====================

        private void OnGotFocus(object? sender, KeyboardFocusChangedEventArgs e)
        {
            HookWindow();
            Show();
        }

        private void OnLostFocus(object? sender, KeyboardFocusChangedEventArgs e)
        {
            UnhookWindow();
            Hide();
        }

        private void OnSelectionChanged(object? sender, RoutedEventArgs e)
        {
            if (_box.IsKeyboardFocused) Show();
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => Refresh();

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => Refresh();

        private void OnIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (_box.IsVisible)
            {
                if (_box.IsKeyboardFocused) Show();
            }
            else Hide();
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (_box.IsKeyboardFocused) Show();
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            StopBlink();
            RemoveAdorner();
            UnhookWindow();
        }

        // 窗口失活时系统光标会消失，自绘光标跟随同一行为
        private void HookWindow()
        {
            _window = Window.GetWindow(_box);
            if (_window == null) return;
            _window.Deactivated += OnWindowDeactivated;
            _window.Activated += OnWindowActivated;
        }

        private void UnhookWindow()
        {
            if (_window == null) return;
            _window.Deactivated -= OnWindowDeactivated;
            _window.Activated -= OnWindowActivated;
            _window = null;
        }

        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            StopBlink();
            _adorner?.Update(Rect.Empty, _brush, false);
        }

        private void OnWindowActivated(object? sender, EventArgs e)
        {
            if (_box.IsKeyboardFocused) Show();
        }
    }

    private sealed class CaretAdorner : Adorner
    {
        private Rect _rect;
        private Brush _brush = Brushes.Transparent;
        private bool _visible;

        public CaretAdorner(TextBox box) : base(box) => IsHitTestVisible = false;

        public void Update(Rect rect, Brush brush, bool visible)
        {
            _rect = rect;
            _brush = brush;
            _visible = visible;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!_visible || _rect.IsEmpty || _rect.Height <= 0) return;
            // 裁剪到控件边界内，避免光标贴边时画出 TextBox 外
            dc.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize)));
            // 对齐物理像素：2px 竖条落在亚像素位置会被抗锯齿发散，反而显虚
            double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double x = Math.Round(_rect.X * scale) / scale;
            double y = Math.Round(_rect.Y * scale) / scale;
            double h = Math.Round(_rect.Height * scale) / scale;
            dc.DrawGeometry(_brush, null, new RectangleGeometry(new Rect(x, y, CaretWidth, h), CaretWidth / 2, CaretWidth / 2));
            dc.Pop();
        }
    }
}
