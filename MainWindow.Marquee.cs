using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace VNotch;

public partial class MainWindow
{
    #region Marquee Fields

    private double _titleScrollDistance = 0;
    private double _artistScrollDistance = 0;
    private string _lastTitleText = "";
    private string _lastArtistText = "";
    private bool _isTitleActiveA = true; 
    private bool _isArtistActiveA = true; 
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

        var keyAnim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };
        Timeline.SetDesiredFrameRate(keyAnim, 30);

        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.FromSeconds(0)));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.FromSeconds(2))); 
        keyAnim.KeyFrames.Add(new SplineDoubleKeyFrame(-distance, TimeSpan.FromSeconds(2) + totalDuration, new KeySpline(0.4, 0, 0.6, 1)));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-distance, TimeSpan.FromSeconds(4) + totalDuration)); 

        transform.BeginAnimation(TranslateTransform.XProperty, keyAnim);
    }

    #endregion

    #region Text Update with Marquee

    private void UpdateTitleText(string newText)
    {
        if (newText == _lastTitleText) return;

        if ((DateTime.Now - _lastTitleMorphTime).TotalMilliseconds < 400) return;

        _lastTitleText = newText;
        _lastTitleMorphTime = DateTime.Now;

        if (_isTitleActiveA)
        {
            AnimateTextMorph(TrackTitle, TrackTitleNext, TitleMorphTranslate, TitleMorphTranslateNext, newText);
            _isTitleActiveA = false;
        }
        else
        {
            AnimateTextMorph(TrackTitleNext, TrackTitle, TitleMorphTranslateNext, TitleMorphTranslate, newText);
            _isTitleActiveA = true;
        }

        TitleMarqueeTranslate.X = 0;
        TitleMarqueeTranslateNext.X = 0;

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

        if ((DateTime.Now - _lastArtistMorphTime).TotalMilliseconds < 400) return;

        _lastArtistText = newText;
        _lastArtistMorphTime = DateTime.Now;

        if (_isArtistActiveA)
        {
            AnimateTextMorph(TrackArtist, TrackArtistNext, ArtistMorphTranslate, ArtistMorphTranslateNext, newText);
            _isArtistActiveA = false;
        }
        else
        {
            AnimateTextMorph(TrackArtistNext, TrackArtist, ArtistMorphTranslateNext, ArtistMorphTranslate, newText);
            _isArtistActiveA = true;
        }

        ArtistMarqueeTranslate.X = 0;
        ArtistMarqueeTranslateNext.X = 0;

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

    private void AnimateTextMorph(TextBlock current, TextBlock next, TranslateTransform currentMorph, TranslateTransform nextMorph, string newText)
    {
        next.Text = newText;

        var dur = TimeSpan.FromMilliseconds(450);
        var easeInOut = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        currentMorph.BeginAnimation(TranslateTransform.YProperty, null);
        nextMorph.BeginAnimation(TranslateTransform.YProperty, null);
        currentMorph.Y = 0;
        nextMorph.Y = 0;

        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = easeInOut };
        current.BeginAnimation(OpacityProperty, fadeOut);

        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easeInOut };
        next.BeginAnimation(OpacityProperty, fadeIn);
    }

    #endregion
}