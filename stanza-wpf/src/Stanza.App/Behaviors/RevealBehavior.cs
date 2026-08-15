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

        // 容器尚未完成初始布局（新建容器、切换区块后重建等）时直接呈现终态，不播放动画
        if (!el.IsLoaded)
        {
            ApplyFinalState(el, (bool)e.NewValue);
            return;
        }

        if ((bool)e.NewValue) Expand(el);
        else Collapse(el);
    }

    private static void ApplyFinalState(FrameworkElement el, bool revealed)
    {
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);
        var target = GetSpacingTarget(el);
        if (target != null) SetIsChromeActive(target, revealed);
        if (revealed)
        {
            el.MaxHeight = double.PositiveInfinity;
            el.Opacity = 1;
            el.Visibility = Visibility.Visible;
            if (target != null)
                target.Margin = Add(target.Margin, ExpandSpacing);
            var content = GetContentTarget(el);
            if (content != null)
                content.Margin = Add(content.Margin, ExpandContentInset);
        }
        else
        {
            el.MaxHeight = 0;
            el.Opacity = 0;
            el.Visibility = Visibility.Collapsed;
        }
    }

    private static void Expand(FrameworkElement el)
    {
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);

        // 展开开始即点上卡片外观
        var card = GetSpacingTarget(el);
        if (card != null) SetIsChromeActive(card, true);

        // 先以无高度限制量出目标高度
        el.MaxHeight = double.PositiveInfinity;
        el.Visibility = Visibility.Visible;
        var width = (el.Parent as FrameworkElement)?.ActualWidth ?? 0;
        el.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        var target = el.DesiredSize.Height;

        el.MaxHeight = 0;
        el.Opacity = 0;

        // 间距展开：卡片上下让位、左右向外延伸，与详情展开动画同步（Things 3 风格）；
        // 内容层同步反向补偿，标题/备注保持原位
        AnimateMargin(GetSpacingTarget(el), ExpandSpacing, ExpandDuration, new CubicEase { EasingMode = EasingMode.EaseOut });
        AnimateMargin(GetContentTarget(el), ExpandContentInset, ExpandDuration, new CubicEase { EasingMode = EasingMode.EaseOut });

        var heightAnim = new DoubleAnimation(0, target, ExpandDuration)
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

        // 间距同步收回（内容层反向补偿同步撤销）
        AnimateMargin(GetSpacingTarget(el), Negate(ExpandSpacing), CollapseDuration, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        AnimateMargin(GetContentTarget(el), Negate(ExpandContentInset), CollapseDuration, new QuadraticEase { EasingMode = EasingMode.EaseIn });

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
            var card = GetSpacingTarget(el);
            if (card != null) SetIsChromeActive(card, false);
        };
        el.BeginAnimation(FrameworkElement.MaxHeightProperty, heightAnim);

        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(el.Opacity, 0, CollapseFadeDuration));
    }

    /// <summary>边距动画：按 delta 分量增减目标元素的四向边距（展开传正，收起传反）。</summary>
    private static void AnimateMargin(FrameworkElement? target, Thickness delta, Duration duration, IEasingFunction easing)
    {
        if (target == null) return;
        target.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation
        {
            From = target.Margin,
            To = Add(target.Margin, delta),
            Duration = duration,
            EasingFunction = easing,
        });
    }

    private static Thickness Add(Thickness a, Thickness b)
        => new(a.Left + b.Left, a.Top + b.Top, a.Right + b.Right, a.Bottom + b.Bottom);

    private static Thickness Negate(Thickness t) => new(-t.Left, -t.Top, -t.Right, -t.Bottom);
}
