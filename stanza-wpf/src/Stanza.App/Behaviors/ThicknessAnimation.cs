using System.Windows;
using System.Windows.Media.Animation;

namespace Stanza.App.Behaviors;

/// <summary>WPF 没有内置的 Thickness 动画，这里补一个（用于展开卡片的间距过渡）。</summary>
public sealed class ThicknessAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(Thickness), typeof(ThicknessAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(Thickness), typeof(ThicknessAnimation));

    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(ThicknessAnimation));

    public Thickness From
    {
        get => (Thickness)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public Thickness To
    {
        get => (Thickness)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(Thickness);

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
    {
        var progress = clock.CurrentProgress ?? 0;
        if (EasingFunction != null) progress = EasingFunction.Ease(progress);

        var from = From;
        var to = To;
        return new Thickness(
            from.Left + (to.Left - from.Left) * progress,
            from.Top + (to.Top - from.Top) * progress,
            from.Right + (to.Right - from.Right) * progress,
            from.Bottom + (to.Bottom - from.Bottom) * progress);
    }

    protected override Freezable CreateInstanceCore() => new ThicknessAnimation();
}
