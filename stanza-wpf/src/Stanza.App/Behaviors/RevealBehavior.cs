using System.Windows;
using System.Windows.Media.Animation;

namespace Stanza.App.Behaviors;

/// <summary>
/// 展开/收起的揭示动画：动画 MaxHeight 产生真实的展开几何（周围内容被平滑推开），
/// 同时淡入淡出。展开完成后解除高度限制，编辑内容时高度可自由生长。
/// 卡片外观（白底/描边/投影）由 IsChromeActive 驱动：展开开始置位，收起动画结束才复位，
/// 避免外观先于动画消失导致「直接消失」的观感。
/// </summary>
public static class RevealBehavior
{
    private static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration ExpandFadeDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration CollapseFadeDuration = new(TimeSpan.FromMilliseconds(160));

    /// <summary>展开时的间距增量：上下推开邻居、左右向外延伸（负边距），Things 3 风格。</summary>
    private static readonly Thickness ExpandSpacing = new(-6, 14, -6, 14);

    /// <summary>展开时内容区的边距增量：左右 +6 抵消卡片横向外扩（内容不左右移动）；
    /// 上下各 +6，标题/勾选框/备注随卡片展开下移，并与卡片上下缘保持 15px 间距。</summary>
    private static readonly Thickness ExpandContentInset = new(6, 6, 6, 6);

    public static readonly DependencyProperty IsRevealedProperty =
        DependencyProperty.RegisterAttached(
            "IsRevealed", typeof(bool), typeof(RevealBehavior),
            new FrameworkPropertyMetadata(false, OnIsRevealedChanged));

    /// <summary>可选：展开/收起时间距动画的目标元素（通常是外层卡片）。</summary>
    public static readonly DependencyProperty SpacingTargetProperty =
        DependencyProperty.RegisterAttached(
            "SpacingTarget", typeof(FrameworkElement), typeof(RevealBehavior),
            new FrameworkPropertyMetadata(null));

    /// <summary>展开卡片外观状态：展开期间为 true，收起动画结束后才复位（只读语义，由行为内部维护）。</summary>
    public static readonly DependencyProperty IsChromeActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsChromeActive", typeof(bool), typeof(RevealBehavior),
            new FrameworkPropertyMetadata(false));

    public static bool GetIsChromeActive(DependencyObject obj) => (bool)obj.GetValue(IsChromeActiveProperty);

    public static void SetIsChromeActive(DependencyObject obj, bool value) => obj.SetValue(IsChromeActiveProperty, value);

    /// <summary>可选：展开/收起时做边距补偿的内容元素（卡片内层容器）：
    /// 水平方向锚定不动，竖直方向随卡片膨胀移动并保持与边缘的间距。</summary>
    public static readonly DependencyProperty ContentTargetProperty =
        DependencyProperty.RegisterAttached(
            "ContentTarget", typeof(FrameworkElement), typeof(RevealBehavior),
            new FrameworkPropertyMetadata(null));

    public static FrameworkElement? GetContentTarget(DependencyObject obj)
        => (FrameworkElement?)obj.GetValue(ContentTargetProperty);

    public static void SetContentTarget(DependencyObject obj, FrameworkElement? value)
        => obj.SetValue(ContentTargetProperty, value);

    public static FrameworkElement? GetSpacingTarget(DependencyObject obj)
        => (FrameworkElement?)obj.GetValue(SpacingTargetProperty);

    public static void SetSpacingTarget(DependencyObject obj, FrameworkElement? value)
        => obj.SetValue(SpacingTargetProperty, value);

    public static bool GetIsRevealed(DependencyObject obj) => (bool)obj.GetValue(IsRevealedProperty);

    public static void SetIsRevealed(DependencyObject obj, bool value) => obj.SetValue(IsRevealedProperty, value);

    private static void OnIsRevealedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        if (!el.IsLoaded)
        {
            // 模板实例化期间，SpacingTarget/ContentTarget 的 ElementName 绑定可能尚未求值（属性按声明顺序应用，
            // IsRevealed 先触发）。此时直接读会拿到 null，导致展开态的边距增量与卡片外观缺失，
            // 之后收起时再按增量反向补偿就会把边距算成负值（卡片内容上溢、容器被压扁）。
            // 延迟到 Loaded（全部绑定已解析、首帧渲染前）应用终态，不播放动画。
            // ApplyFinalState 的边距由基准值绝对推算，重复应用冪等，多次订阅 Loaded 无害。
            el.Loaded += ApplyOnce;
            void ApplyOnce(object? s, RoutedEventArgs args)
            {
                el.Loaded -= ApplyOnce;
                ApplyFinalState(el, GetIsRevealed(el));
            }
            return;
        }

        if ((bool)e.NewValue) Expand(el);
        else Collapse(el);
    }

    // 基准边距（未展开态）：首次访问时捕获并缓存。之后展开/收起的终点一律由基准绝对推算，
    // 不用「当前值 + 增量」的相对计算，动画被打断时也不会累积误差
    private static readonly DependencyProperty BaseMarginProperty =
        DependencyProperty.RegisterAttached(
            "BaseMargin", typeof(Thickness?), typeof(RevealBehavior),
            new FrameworkPropertyMetadata(null));

    private static Thickness GetBaseMargin(FrameworkElement target)
    {
        if (target.GetValue(BaseMarginProperty) is Thickness stored) return stored;
        var current = target.Margin;
        target.SetValue(BaseMarginProperty, current);
        return current;
    }

    private static void ApplyFinalState(FrameworkElement el, bool revealed)
    {
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);
        var target = GetSpacingTarget(el);
        var content = GetContentTarget(el);
        if (revealed)
        {
            el.MaxHeight = double.PositiveInfinity;
            el.Opacity = 1;
            el.Visibility = Visibility.Visible;
            if (target != null)
            {
                SetIsChromeActive(target, true);
                target.Margin = Add(GetBaseMargin(target), ExpandSpacing);
            }
            if (content != null)
                content.Margin = Add(GetBaseMargin(content), ExpandContentInset);
        }
        else
        {
            el.MaxHeight = 0;
            el.Opacity = 0;
            el.Visibility = Visibility.Collapsed;
            if (target != null)
            {
                SetIsChromeActive(target, false);
                target.Margin = GetBaseMargin(target);
            }
            if (content != null)
                content.Margin = GetBaseMargin(content);
        }
    }

    private static void Expand(FrameworkElement el)
    {
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);

        var card = GetSpacingTarget(el);
        var content = GetContentTarget(el);

        // 展开开始即点上卡片外观
        if (card != null) SetIsChromeActive(card, true);

        // 先以无高度限制量出目标高度
        el.MaxHeight = double.PositiveInfinity;
        el.Visibility = Visibility.Visible;
        var width = (el.Parent as FrameworkElement)?.ActualWidth ?? 0;
        el.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        var targetHeight = el.DesiredSize.Height;

        el.MaxHeight = 0;
        el.Opacity = 0;

        // 间距展开：卡片上下让位、左右向外延伸，与详情展开动画同步（Things 3 风格）；
        // 内容层同步反向补偿，标题/备注保持原位。终点由基准边距绝对推算（打断不漂移）
        if (card != null) AnimateMargin(card, Add(GetBaseMargin(card), ExpandSpacing), ExpandDuration, new CubicEase { EasingMode = EasingMode.EaseOut });
        if (content != null) AnimateMargin(content, Add(GetBaseMargin(content), ExpandContentInset), ExpandDuration, new CubicEase { EasingMode = EasingMode.EaseOut });

        var heightAnim = new DoubleAnimation(0, targetHeight, ExpandDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        heightAnim.Completed += (_, _) =>
        {
            // 解除高度限制，编辑备注时高度自由增长
            el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            el.MaxHeight = double.PositiveInfinity;
        };
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, heightAnim);

        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, ExpandFadeDuration));
    }

    private static void Collapse(FrameworkElement el)
    {
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);

        // 从当前真实高度收起（支持展开动画进行到一半时反向收起）
        var from = el.ActualHeight;
        el.MaxHeight = from;

        // 间距同步收回至基准（内容层反向补偿同步撤销）
        var card = GetSpacingTarget(el);
        var content = GetContentTarget(el);
        if (card != null) AnimateMargin(card, GetBaseMargin(card), CollapseDuration, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        if (content != null) AnimateMargin(content, GetBaseMargin(content), CollapseDuration, new QuadraticEase { EasingMode = EasingMode.EaseIn });

        var heightAnim = new DoubleAnimation(from, 0, CollapseDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        heightAnim.Completed += (_, _) =>
        {
            el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            el.MaxHeight = 0;
            el.Visibility = Visibility.Collapsed;
            // 收缩完成后才卸下卡片外观，折叠全程保持白卡/描边/投影
            if (card != null) SetIsChromeActive(card, false);
        };
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, heightAnim);

        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(el.Opacity, 0, CollapseFadeDuration));
    }

    /// <summary>边距动画：从当前值过渡到绝对终点（由基准边距推算），打断后以当前值为起点重播。</summary>
    private static void AnimateMargin(FrameworkElement? target, Thickness to, Duration duration, IEasingFunction easing)
    {
        if (target == null) return;
        target.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation
        {
            From = target.Margin,
            To = to,
            Duration = duration,
            EasingFunction = easing,
        });
    }

    private static Thickness Add(Thickness a, Thickness b)
        => new(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);
}
