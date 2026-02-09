using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// Apple-style animation service for smooth, native-feeling animations
/// Implements macOS-like spring animations and easing curves
/// </summary>
public static class AnimationService
{
    // Apple's default animation duration (0.25s - 0.35s for most interactions)
    public static readonly Duration FastDuration = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration NormalDuration = new(TimeSpan.FromMilliseconds(300));
    public static readonly Duration SlowDuration = new(TimeSpan.FromMilliseconds(450));
    public static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(350));

    // Apple's signature easing functions
    // macOS uses custom bezier curves similar to iOS's UIViewPropertyAnimator
    public static readonly IEasingFunction AppleEaseOut = new CubicEase
    {
        EasingMode = EasingMode.EaseOut
    };

    public static readonly IEasingFunction AppleEaseInOut = new CubicEase
    {
        EasingMode = EasingMode.EaseInOut
    };

    // Spring-like bounce effect (similar to Apple's spring animations)
    public static readonly IEasingFunction AppleSpring = new ElasticEase
    {
        EasingMode = EasingMode.EaseOut,
        Oscillations = 1,
        Springiness = 8
    };

    // Smooth deceleration for expand animations
    public static readonly IEasingFunction AppleDecelerate = new QuinticEase
    {
        EasingMode = EasingMode.EaseOut
    };

    // Power ease for subtle micro-interactions
    public static readonly IEasingFunction AppleMicro = new PowerEase
    {
        EasingMode = EasingMode.EaseOut,
        Power = 3
    };

    /// <summary>
    /// Animate width change with Apple-style easing
    /// </summary>
    public static void AnimateWidth(FrameworkElement element, double toWidth, 
        Duration? duration = null, IEasingFunction? easing = null, Action? onComplete = null)
    {
        var animation = new DoubleAnimation
        {
            To = toWidth,
            Duration = duration ?? NormalDuration,
            EasingFunction = easing ?? AppleEaseOut
        };

        if (onComplete != null)
        {
            animation.Completed += (s, e) => onComplete();
        }

        element.BeginAnimation(FrameworkElement.WidthProperty, animation);
    }

    /// <summary>
    /// Animate height change with Apple-style easing
    /// </summary>
    public static void AnimateHeight(FrameworkElement element, double toHeight,
        Duration? duration = null, IEasingFunction? easing = null, Action? onComplete = null)
    {
        var animation = new DoubleAnimation
        {
            To = toHeight,
            Duration = duration ?? NormalDuration,
            EasingFunction = easing ?? AppleEaseOut
        };

        if (onComplete != null)
        {
            animation.Completed += (s, e) => onComplete();
        }

        element.BeginAnimation(FrameworkElement.HeightProperty, animation);
    }

    /// <summary>
    /// Animate size change (both width and height) simultaneously
    /// </summary>
    public static void AnimateSize(FrameworkElement element, double toWidth, double toHeight,
        Duration? duration = null, IEasingFunction? easing = null, Action? onComplete = null)
    {
        var dur = duration ?? NormalDuration;
        var ease = easing ?? AppleEaseOut;

        var widthAnimation = new DoubleAnimation
        {
            To = toWidth,
            Duration = dur,
            EasingFunction = ease
        };

        var heightAnimation = new DoubleAnimation
        {
            To = toHeight,
            Duration = dur,
            EasingFunction = ease
        };

        if (onComplete != null)
        {
            heightAnimation.Completed += (s, e) => onComplete();
        }

        element.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);
        element.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
    }

    /// <summary>
    /// Animate opacity (fade in/out)
    /// </summary>
    public static void AnimateOpacity(UIElement element, double toOpacity,
        Duration? duration = null, IEasingFunction? easing = null, Action? onComplete = null)
    {
        var animation = new DoubleAnimation
        {
            To = toOpacity,
            Duration = duration ?? FastDuration,
            EasingFunction = easing ?? AppleEaseOut
        };

        if (onComplete != null)
        {
            animation.Completed += (s, e) => onComplete();
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>
    /// Animate corner radius change (for notch expansion/collapse)
    /// </summary>
    public static void AnimateCornerRadius(System.Windows.Controls.Border border, 
        CornerRadius toRadius, Duration? duration = null, Action? onComplete = null)
    {
        // WPF doesn't directly animate CornerRadius, so we use a workaround
        var animation = new DoubleAnimation
        {
            To = toRadius.BottomLeft, // Assuming uniform bottom corners
            Duration = duration ?? NormalDuration,
            EasingFunction = AppleEaseOut
        };

        animation.CurrentTimeInvalidated += (s, e) =>
        {
            if (s is AnimationClock clock && clock.CurrentProgress.HasValue)
            {
                var currentRadius = border.CornerRadius.BottomLeft +
                    (toRadius.BottomLeft - border.CornerRadius.BottomLeft) * clock.CurrentProgress.Value;
                border.CornerRadius = new CornerRadius(0, 0, currentRadius, currentRadius);
            }
        };

        if (onComplete != null)
        {
            animation.Completed += (s, e) => onComplete();
        }

        // Create a storyboard to run the animation
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, border);
        Storyboard.SetTargetProperty(animation, new PropertyPath("Tag")); // Dummy property
        storyboard.Begin();
    }

    /// <summary>
    /// Scale transform animation (for micro-interactions)
    /// </summary>
    public static void AnimateScale(FrameworkElement element, double toScale,
        Duration? duration = null, IEasingFunction? easing = null, Action? onComplete = null)
    {
        var transform = element.RenderTransform as ScaleTransform;
        if (transform == null)
        {
            transform = new ScaleTransform(1, 1);
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var scaleXAnimation = new DoubleAnimation
        {
            To = toScale,
            Duration = duration ?? FastDuration,
            EasingFunction = easing ?? AppleMicro
        };

        var scaleYAnimation = new DoubleAnimation
        {
            To = toScale,
            Duration = duration ?? FastDuration,
            EasingFunction = easing ?? AppleMicro
        };

        if (onComplete != null)
        {
            scaleYAnimation.Completed += (s, e) => onComplete();
        }

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
    }

    /// <summary>
    /// Translate animation (for sliding effects)
    /// </summary>
    public static void AnimateTranslate(FrameworkElement element, double toX, double toY,
        Duration? duration = null, IEasingFunction? easing = null, Action? onComplete = null)
    {
        var transform = element.RenderTransform as TranslateTransform;
        if (transform == null)
        {
            transform = new TranslateTransform(0, 0);
            element.RenderTransform = transform;
        }

        var translateXAnimation = new DoubleAnimation
        {
            To = toX,
            Duration = duration ?? NormalDuration,
            EasingFunction = easing ?? AppleDecelerate
        };

        var translateYAnimation = new DoubleAnimation
        {
            To = toY,
            Duration = duration ?? NormalDuration,
            EasingFunction = easing ?? AppleDecelerate
        };

        if (onComplete != null)
        {
            translateYAnimation.Completed += (s, e) => onComplete();
        }

        transform.BeginAnimation(TranslateTransform.XProperty, translateXAnimation);
        transform.BeginAnimation(TranslateTransform.YProperty, translateYAnimation);
    }

    /// <summary>
    /// Pulse animation for attention-grabbing (like notification indicator)
    /// </summary>
    public static void AnimatePulse(FrameworkElement element, int repeatCount = 2)
    {
        var transform = element.RenderTransform as ScaleTransform;
        if (transform == null)
        {
            transform = new ScaleTransform(1, 1);
            element.RenderTransform = transform;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = new RepeatBehavior(repeatCount)
        };

        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromPercent(0.5), AppleSpring));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1), AppleEaseOut));

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation.Clone());
    }

    /// <summary>
    /// Smooth expand animation for Dynamic Island-style expansion
    /// </summary>
    public static void AnimateExpand(FrameworkElement element, 
        double fromWidth, double fromHeight, 
        double toWidth, double toHeight,
        Duration? duration = null, Action? onComplete = null)
    {
        var dur = duration ?? ExpandDuration;

        // Use spring-like easing for that bouncy Apple feel
        var widthAnimation = new DoubleAnimation
        {
            From = fromWidth,
            To = toWidth,
            Duration = dur,
            EasingFunction = AppleDecelerate
        };

        var heightAnimation = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = dur,
            EasingFunction = AppleDecelerate
        };

        if (onComplete != null)
        {
            heightAnimation.Completed += (s, e) => onComplete();
        }

        element.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);
        element.BeginAnimation(FrameworkElement.HeightProperty, heightAnimation);
    }

    /// <summary>
    /// Collapse animation with smooth deceleration
    /// </summary>
    public static void AnimateCollapse(FrameworkElement element,
        double toWidth, double toHeight,
        Duration? duration = null, Action? onComplete = null)
    {
        AnimateExpand(element, element.ActualWidth, element.ActualHeight, 
            toWidth, toHeight, duration ?? NormalDuration, onComplete);
    }
}
