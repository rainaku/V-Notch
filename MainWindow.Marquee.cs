using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace VNotch;

/// <summary>
/// Partial class for Marquee text scrolling logic
/// </summary>
public partial class MainWindow
{
    #region Marquee Fields

    private DispatcherTimer? _marqueeTimer;
    private double _titleScrollOffset = 0;
    private double _artistScrollOffset = 0;
    private double _titleScrollDistance = 0;
    private double _artistScrollDistance = 0;
    private bool _titleScrollForward = true;
    private bool _artistScrollForward = true;
    private DateTime _titlePauseUntil = DateTime.MinValue;
    private DateTime _artistPauseUntil = DateTime.MinValue;
    private string _lastTitleText = "";
    private string _lastArtistText = "";
    private bool _isTitleActiveA = true; // Tracks which Title TextBlock is visible
    private bool _isArtistActiveA = true; // Tracks which Artist TextBlock is visible
    private DateTime _lastTitleMorphTime = DateTime.MinValue;
    private DateTime _lastArtistMorphTime = DateTime.MinValue;

    #endregion

    #region Marquee Timer

    private void StartMarqueeAnimation(TranslateTransform transform, double distance, double durationPerPixel = 40)
    {
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        
        var totalDuration = TimeSpan.FromMilliseconds(distance * durationPerPixel);
        var pauseDuration = TimeSpan.FromSeconds(2);
        
        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };

        var anim = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = new Duration(totalDuration),
            BeginTime = pauseDuration,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        
        // Add a slight pause at the ends by making the animation longer than the duration but with stagnant start
        // Actually, better to use a DoubleAnimationUsingKeyFrames for precise pause control
        var keyAnim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };
        
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.FromSeconds(0)));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.FromSeconds(2))); // Pause at start
        keyAnim.KeyFrames.Add(new SplineDoubleKeyFrame(-distance, TimeSpan.FromSeconds(2) + totalDuration, new KeySpline(0.4, 0, 0.6, 1)));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-distance, TimeSpan.FromSeconds(4) + totalDuration)); // Pause at end
        
        transform.BeginAnimation(TranslateTransform.XProperty, keyAnim);
    }

    #endregion

    #region Text Update with Marquee

    private void UpdateTitleText(string newText)
    {
        if (newText == _lastTitleText) return;
        
        // DEBOUNCE: Don't morph again if we just did one recently (< 400ms)
        if ((DateTime.Now - _lastTitleMorphTime).TotalMilliseconds < 400) return;
        
        _lastTitleText = newText;
        _lastTitleMorphTime = DateTime.Now;

        // Perform Morph Animation
        if (_isTitleActiveA)
        {
            AnimateTextMorph(TrackTitle, TrackTitleNext, TitleBlur, TitleMorphTranslate, TitleMorphTranslateNext, newText);
            _isTitleActiveA = false;
        }
        else
        {
            AnimateTextMorph(TrackTitleNext, TrackTitle, TitleBlur, TitleMorphTranslateNext, TitleMorphTranslate, newText);
            _isTitleActiveA = true;
        }
        
        // Reset marquee state for the NEWLY active text
        _titleScrollOffset = 0;
        _titleScrollForward = true;
        _titlePauseUntil = DateTime.Now.AddSeconds(2.2); // Slightly longer pause for morph to settle
        
        TitleMarqueeTranslate.X = 0;
        TitleMarqueeTranslateNext.X = 0;
        
        // Calculate marquee for the new active text
        var activeText = _isTitleActiveA ? TrackTitle : TrackTitleNext;
        var activeTranslate = _isTitleActiveA ? TitleMarqueeTranslate : TitleMarqueeTranslateNext;
        
        activeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = activeText.DesiredSize.Width;
        double containerWidth = TitleScrollContainer.Width > 0 ? TitleScrollContainer.Width : 250;
        
        if (textWidth > containerWidth)
        {
            _titleScrollDistance = textWidth - containerWidth + 15;
            StartMarqueeAnimation(activeTranslate, _titleScrollDistance);
        }
        else
        {
            _titleScrollDistance = 0;
            activeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            activeTranslate.X = 0;
        }
    }
    
    private void UpdateArtistText(string newText)
    {
        if (newText == _lastArtistText) return;

        // DEBOUNCE: Don't morph again if we just did one recently (< 400ms)
        if ((DateTime.Now - _lastArtistMorphTime).TotalMilliseconds < 400) return;

        _lastArtistText = newText;
        _lastArtistMorphTime = DateTime.Now;
        
        // Perform Morph Animation
        if (_isArtistActiveA)
        {
            AnimateTextMorph(TrackArtist, TrackArtistNext, ArtistBlur, ArtistMorphTranslate, ArtistMorphTranslateNext, newText);
            _isArtistActiveA = false;
        }
        else
        {
            AnimateTextMorph(TrackArtistNext, TrackArtist, ArtistBlur, ArtistMorphTranslateNext, ArtistMorphTranslate, newText);
            _isArtistActiveA = true;
        }
        
        // Reset marquee state for the NEWLY active text
        _artistScrollOffset = 0;
        _artistScrollForward = true;
        _artistPauseUntil = DateTime.Now.AddSeconds(2.5);
        
        ArtistMarqueeTranslate.X = 0;
        ArtistMarqueeTranslateNext.X = 0;
        
        // Calculate marquee for the new active text
        var activeText = _isArtistActiveA ? TrackArtist : TrackArtistNext;
        var activeTranslate = _isArtistActiveA ? ArtistMarqueeTranslate : ArtistMarqueeTranslateNext;
        
        activeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = activeText.DesiredSize.Width;
        double containerWidth = ArtistScrollContainer.Width > 0 ? ArtistScrollContainer.Width : 250;
        
        if (textWidth > containerWidth)
        {
            _artistScrollDistance = textWidth - containerWidth + 15;
            StartMarqueeAnimation(activeTranslate, _artistScrollDistance);
        }
        else
        {
            _artistScrollDistance = 0;
            activeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            activeTranslate.X = 0;
        }
    }

    private void AnimateTextMorph(TextBlock current, TextBlock next, System.Windows.Media.Effects.BlurEffect blur, TranslateTransform currentMorph, TranslateTransform nextMorph, string newText)
    {
        next.Text = newText;
        
        // Stationary Blur-Dissolve: Slow, calm, premium.
        var dur = TimeSpan.FromMilliseconds(450); 
        var easeInOut = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        // Ensure no stray movement from previous animations/initializations
        currentMorph.BeginAnimation(TranslateTransform.YProperty, null);
        nextMorph.BeginAnimation(TranslateTransform.YProperty, null);
        currentMorph.Y = 0;
        nextMorph.Y = 0;

        // 1. Pronounced Blur - Slopes with the fade
        var blurAnim = new DoubleAnimation(0, 10, dur.Divide(2)) { EasingFunction = easeInOut, AutoReverse = true };
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnim);

        // 2. Slow Cross-Fade Current (Out)
        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = easeInOut };
        current.BeginAnimation(OpacityProperty, fadeOut);

        // 3. Slow Cross-Fade Next (In)
        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easeInOut };
        next.BeginAnimation(OpacityProperty, fadeIn);
    }

    #endregion
}
