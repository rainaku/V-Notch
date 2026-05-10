using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Controls;

/// <summary>
/// Encapsulates the track title/artist marquee (pause-travel-pause loop) and the
/// two-TextBlock morph transition played when the underlying text changes. Extracted
/// from <c>MainWindow.Marquee.cs</c> so the notch window no longer owns this state
/// directly.
/// </summary>
internal sealed class MarqueeController
{
    #region Dependencies

    private readonly MarqueeTargets _title;
    private readonly MarqueeTargets _artist;
    private readonly TextBlock _compactTitleText;
    private readonly TranslateTransform _compactTitleTranslate;
    private readonly Func<double, double> _getVisibleMediaTextWidth;

    #endregion

    #region State

    private double _titleScrollDistance;
    private double _artistScrollDistance;
    private string _lastTitleText = string.Empty;
    private string _lastArtistText = string.Empty;
    private bool _isTitleActiveA = true;
    private bool _isArtistActiveA = true;
    private DateTime _lastTitleMorphTime = DateTime.MinValue;
    private DateTime _lastArtistMorphTime = DateTime.MinValue;

    #endregion

    public MarqueeController(
        TextBlock titleA, TranslateTransform titleMarqueeA, TranslateTransform titleMorphA,
        TextBlock titleB, TranslateTransform titleMarqueeB, TranslateTransform titleMorphB,
        TextBlock artistA, TranslateTransform artistMarqueeA, TranslateTransform artistMorphA,
        TextBlock artistB, TranslateTransform artistMarqueeB, TranslateTransform artistMorphB,
        TextBlock compactTitleText, TranslateTransform compactTitleTranslate,
        Func<double, double> getVisibleMediaTextWidth)
    {
        _title = new MarqueeTargets(titleA, titleMarqueeA, titleMorphA, titleB, titleMarqueeB, titleMorphB);
        _artist = new MarqueeTargets(artistA, artistMarqueeA, artistMorphA, artistB, artistMarqueeB, artistMorphB);
        _compactTitleText = compactTitleText;
        _compactTitleTranslate = compactTitleTranslate;
        _getVisibleMediaTextWidth = getVisibleMediaTextWidth;
    }

    #region Public API

    /// <summary>Rebuilds the scrolling loop for the currently displayed title and artist.</summary>
    public void RefreshMediaMarquee()
    {
        RestartMarqueeFor(_title, _isTitleActiveA, isTitle: true);
        RestartMarqueeFor(_artist, _isArtistActiveA, isTitle: false);
    }

    /// <summary>Animates a title text change (crossfade + slide) and restarts the marquee.</summary>
    public void UpdateTitleText(string newText)
    {
        if (newText == _lastTitleText) return;
        if ((DateTime.Now - _lastTitleMorphTime).TotalMilliseconds < 400) return;

        _lastTitleText = newText;
        _lastTitleMorphTime = DateTime.Now;

        MorphAndRestart(_title, ref _isTitleActiveA, newText,
            setDistance: d => _titleScrollDistance = d);
    }

    /// <summary>Animates an artist text change (crossfade + slide) and restarts the marquee.</summary>
    public void UpdateArtistText(string newText)
    {
        if (newText == _lastArtistText) return;
        if ((DateTime.Now - _lastArtistMorphTime).TotalMilliseconds < 400) return;

        _lastArtistText = newText;
        _lastArtistMorphTime = DateTime.Now;

        MorphAndRestart(_artist, ref _isArtistActiveA, newText,
            setDistance: d => _artistScrollDistance = d);
    }

    /// <summary>Starts a marquee animation on an arbitrary transform (used for the compact widget title).</summary>
    public static void StartMarqueeAnimation(TranslateTransform transform, double distance, double durationPerPixel = 40)
    {
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;

        if (distance <= 1) return;

        const double pauseMs = 900;
        const double minTravelMs = 2200;
        const double maxTravelMs = 14000;
        var travelMs = Math.Clamp(distance * durationPerPixel, minTravelMs, maxTravelMs);

        var t0 = TimeSpan.Zero;
        var t1 = t0 + TimeSpan.FromMilliseconds(pauseMs);
        var t2 = t1 + TimeSpan.FromMilliseconds(travelMs);
        var t3 = t2 + TimeSpan.FromMilliseconds(pauseMs);
        var t4 = t3 + TimeSpan.FromMilliseconds(travelMs);
        var t5 = t4 + TimeSpan.FromMilliseconds(pauseMs);

        var keyAnim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(keyAnim, 120);

        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, t0));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, t1));
        keyAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, t2));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-distance, t3));
        keyAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, t4));
        keyAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, t5));

        transform.BeginAnimation(TranslateTransform.XProperty, keyAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Implementation

    private void RestartMarqueeFor(MarqueeTargets t, bool activeA, bool isTitle)
    {
        var activeText = activeA ? t.TextA : t.TextB;
        var activeTranslate = activeA ? t.MarqueeA : t.MarqueeB;

        activeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = activeText.DesiredSize.Width;
        double containerWidth = _getVisibleMediaTextWidth(340);
        double distance = textWidth - containerWidth + 15;

        if (distance > 1)
        {
            if (isTitle) _titleScrollDistance = distance;
            else _artistScrollDistance = distance;
            StartMarqueeAnimation(activeTranslate, distance);
        }
        else
        {
            if (isTitle) _titleScrollDistance = 0;
            else _artistScrollDistance = 0;
            activeTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            activeTranslate.X = 0;
        }
    }

    private void MorphAndRestart(MarqueeTargets t, ref bool activeA, string newText, Action<double> setDistance)
    {
        if (activeA)
        {
            AnimateTextMorph(t.TextA, t.TextB, t.MorphA, t.MorphB, newText);
            activeA = false;
        }
        else
        {
            AnimateTextMorph(t.TextB, t.TextA, t.MorphB, t.MorphA, newText);
            activeA = true;
        }

        t.MarqueeA.X = 0;
        t.MarqueeB.X = 0;

        var newActiveText = activeA ? t.TextA : t.TextB;
        var newActiveTranslate = activeA ? t.MarqueeA : t.MarqueeB;

        newActiveText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = newActiveText.DesiredSize.Width;
        double containerWidth = _getVisibleMediaTextWidth(340);

        if (textWidth > containerWidth)
        {
            double distance = textWidth - containerWidth + 15;
            setDistance(distance);
            StartMarqueeAnimation(newActiveTranslate, distance);
        }
        else
        {
            setDistance(0);
            newActiveTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            newActiveTranslate.X = 0;
        }
    }

    private static void AnimateTextMorph(TextBlock current, TextBlock next, TranslateTransform currentMorph, TranslateTransform nextMorph, string newText)
    {
        next.Text = newText;

        var dur = _dur600;
        const int animFps = 144;
        var easeOut = _easeExpOut6;

        currentMorph.BeginAnimation(TranslateTransform.XProperty, null);
        nextMorph.BeginAnimation(TranslateTransform.XProperty, null);
        currentMorph.BeginAnimation(TranslateTransform.YProperty, null);
        nextMorph.BeginAnimation(TranslateTransform.YProperty, null);
        currentMorph.Y = 0;
        nextMorph.Y = 0;

        var slideOut = MakeAnim(0, -10, dur, easeOut, animFps);
        currentMorph.BeginAnimation(TranslateTransform.XProperty, slideOut);

        var slideIn = MakeAnim(12, 0, dur, easeOut, animFps);
        nextMorph.BeginAnimation(TranslateTransform.XProperty, slideIn);

        var fadeOut = MakeAnim(1, 0, dur, easeOut, animFps);
        current.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        var fadeIn = MakeAnim(0, 1, dur, easeOut, animFps);
        next.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    #endregion

    /// <summary>Holds the two TextBlocks, their marquee translates, and their morph translates for a single crossfading line (title or artist).</summary>
    private sealed record MarqueeTargets(
        TextBlock TextA, TranslateTransform MarqueeA, TranslateTransform MorphA,
        TextBlock TextB, TranslateTransform MarqueeB, TranslateTransform MorphB);
}
