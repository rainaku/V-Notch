using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace VNotch.Services;
internal static class AnimationPrimitives
{
    #region Cached Easing Functions (Frozen - Thread Safe)

    public static readonly ExponentialEase _easeExpOut7 = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 });
    public static readonly ExponentialEase _easeExpOut6 = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });
    public static readonly QuadraticEase _easeQuadOut = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    public static readonly QuadraticEase _easeQuadIn = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    public static readonly QuadraticEase _easeQuadInOut = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseInOut });
    public static readonly PowerEase _easePowerIn2 = Freeze(new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 });
    public static readonly PowerEase _easePowerOut3 = Freeze(new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 });
    public static readonly ElasticEase _easeSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 8 });
    public static readonly ElasticEase _easeSoftSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 3 });
    public static readonly ElasticEase _easeMenuSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 });
    public static readonly ElasticEase _easeThumbSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6.5 });
    public static readonly SineEase _easeSineInOut = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });
    public static readonly ElasticEase _easeHapticBounce = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 });

    private static T Freeze<T>(T easing) where T : Freezable
    {
        easing.Freeze();
        return easing;
    }

    #endregion

    #region Cached Durations

    public static readonly Duration _dur600 = new(TimeSpan.FromMilliseconds(600));
    public static readonly Duration _dur500 = new(TimeSpan.FromMilliseconds(500));
    public static readonly Duration _dur450 = new(TimeSpan.FromMilliseconds(450));
    public static readonly Duration _dur400 = new(TimeSpan.FromMilliseconds(400));
    public static readonly Duration _dur350 = new(TimeSpan.FromMilliseconds(350));
    public static readonly Duration _dur250 = new(TimeSpan.FromMilliseconds(250));
    public static readonly Duration _dur200 = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration _dur150 = new(TimeSpan.FromMilliseconds(150));
    public static readonly Duration _dur100 = new(TimeSpan.FromMilliseconds(100));
    public static readonly Duration _dur80 = new(TimeSpan.FromMilliseconds(80));

    #endregion

    #region Animation Factories

    public static DoubleAnimation MakeAnim(double? from, double to, Duration duration, IEasingFunction? easing = null, int fps = 120)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, fps);
        return anim;
    }

    public static DoubleAnimation MakeAnim(double to, Duration duration, IEasingFunction? easing = null, int fps = 120)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, fps);
        return anim;
    }

    public static DoubleAnimation MakeAnim(double from, double to, Duration duration, IEasingFunction? easing, TimeSpan? beginTime)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };

        if (beginTime.HasValue)
            anim.BeginTime = beginTime.Value;
        Timeline.SetDesiredFrameRate(anim, 120);
        return anim;
    }

    #endregion
}
