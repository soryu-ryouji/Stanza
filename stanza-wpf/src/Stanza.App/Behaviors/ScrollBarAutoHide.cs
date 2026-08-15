using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Stanza.App.Behaviors;

/// <summary>
/// 滚动条自动隐藏（macOS 风格）：滚动偏移变化时淡入，停止滚动约 1.2 秒后淡出；
/// 内容不可滚动或闲置时不显示。滚动条列由定制 ScrollViewer 模板常驻保留，不影响内容布局。
/// </summary>
public static class ScrollBarAutoHide
{
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(400));

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollBarAutoHide),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox || e.NewValue is not true) return;
        if (listBox.IsLoaded) Attach(listBox);
        else listBox.Loaded += (_, _) => Attach(listBox);
    }

    private static void Attach(ListBox listBox)
    {
        var scrollViewer = VisualTreeEx.FindVisualChildren<ScrollViewer>(listBox).FirstOrDefault();
        var bar = VisualTreeEx.FindVisualChildren<ScrollBar>(listBox)
            .FirstOrDefault(b => b.Orientation == Orientation.Vertical);
        if (scrollViewer == null || bar == null) return;

        // 定制模板的 Value 绑定是 OneWay，拖拽滑块时显式回写滚动位置
        bar.Scroll += (_, se) => scrollViewer.ScrollToVerticalOffset(se.NewValue);

        var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        hideTimer.Tick += (_, _) =>
        {
            if (bar.IsMouseOver) return;   // 悬停在滚动条上时保持显示（到点后再检查）
            hideTimer.Stop();
            Fade(bar, 0);
        };

        scrollViewer.ScrollChanged += (_, se) =>
        {
            // 内容不再可滚动：立即隐藏
            if (se.ExtentHeight <= se.ViewportHeight + 0.5)
            {
                hideTimer.Stop();
                Fade(bar, 0);
                return;
            }
            // 只有滚动偏移真正变化（滚轮/拖拽/定位）才显示，布局变化不显示
            if (se.VerticalChange == 0) return;
            Fade(bar, 1);
            hideTimer.Stop();
            hideTimer.Start();
        };
    }

    private static void Fade(ScrollBar bar, double to)
    {
        bar.IsHitTestVisible = to > 0;   // 隐形时不拦截鼠标
        bar.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, to > 0 ? FadeInDuration : FadeOutDuration));
    }
}
