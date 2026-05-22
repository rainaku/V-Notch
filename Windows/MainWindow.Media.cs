using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private string _lastAnimatedTrackSignature = "";
    private string _lastColorTrackSignature = "";
    private string _lastRenderedMediaSource = "";
    private ImageSource? _pendingFlipThumbnail;
    private ImageSource? _lastAnimatedThumbnail;
    private DateTime _lastAnimationStartTime = DateTime.MinValue;
    private int _thumbnailSwitchGeneration = 0;
    private bool _thumbnailShownForCurrentTrack = false;

    private static readonly string[] _genericTitles = { "Spotify", "Spotify Premium", "Spotify Free", "YouTube", "SoundCloud", "Browser" };

    #region Media Changed Handler

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        _currentMediaInfo = info;

        Dispatcher.BeginInvoke(() =>
        {
            var result = _mediaDisplayController.ProcessMediaUpdate(
                info, _isExpanded, _isMusicExpanded, _isMusicCompactMode, _isAnimating);

            if (result.Action == MediaDisplayAction.Ignore)
                return;

            // Sync local tracking fields from controller state
            _lastAnimatedTrackSignature = _mediaDisplayController.LastAnimatedTrackSignature;
            _lastColorTrackSignature = _mediaDisplayController.LastColorTrackSignature;
            _lastRenderedMediaSource = _mediaDisplayController.LastRenderedMediaSource;
            _lastAnimatedThumbnail = _mediaDisplayController.LastAnimatedThumbnail;
            _thumbnailShownForCurrentTrack = _mediaDisplayController.ThumbnailShownForCurrentTrack;

            string trackIdentity = result.TrackIdentity;
            string renderedSource = result.RenderedSource;

            // ─── Display Text ───
            if (result.IsNewTrack)
            {
                UpdateTitleText(result.DisplayText.Title);
                UpdateArtistText(result.DisplayText.Artist);
                CompactTitleMarquee.Text = result.DisplayText.Title;

                // Fetch synced lyrics for Spotify tracks
                if (result.HasRealTrack && renderedSource == "Spotify")
                {
                    FetchLyricsForTrack(info);
                }
                else if (!_isLyricsActive)
                {
                    ClearLyrics();
                }
            }
            else
            {
                TrackTitle.Text = result.DisplayText.Title;
                TrackArtist.Text = result.DisplayText.Artist;
                CompactTitleMarquee.Text = result.DisplayText.Title;
            }

            // ─── Thumbnail Handling ───
            if (result.HasRealTrack)
            {
                if (result.HasThumbnail && info.Thumbnail != null)
                {
                    // Update LyricsBlurImage with crossfade when expanded + lyrics active
                    if (LyricsBlurImage != null && _isExpanded && _isLyricsActive)
                    {
                        if (!ReferenceEquals(LyricsBlurImage.Source, info.Thumbnail) &&
                            !ReferenceEquals(LyricsBlurImageNext.Source, info.Thumbnail))
                        {
                            LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                            if (LyricsBlurImageNext.Opacity > 0.5 && LyricsBlurImageNext.Source != null)
                            {
                                LyricsBlurImage.Source = LyricsBlurImageNext.Source;
                            }
                            LyricsBlurImageNext.Opacity = 0;
                            LyricsBlurImageNext.Source = info.Thumbnail;
                            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
                            {
                                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut }
                            };
                            fadeIn.Completed += (s, e) =>
                            {
                                if (ReferenceEquals(LyricsBlurImageNext.Source, info.Thumbnail))
                                {
                                    LyricsBlurImage.Source = info.Thumbnail;
                                    LyricsBlurImageNext.BeginAnimation(OpacityProperty, null);
                                    LyricsBlurImageNext.Opacity = 0;
                                }
                            };
                            LyricsBlurImageNext.BeginAnimation(OpacityProperty, fadeIn);
                        }
                    }
                    else if (LyricsBlurImage != null)
                    {
                        LyricsBlurImage.Source = info.Thumbnail;
                    }

                    switch (result.ThumbnailAction)
                    {
                        case ThumbnailAction.RevealFirst:
                            ThumbnailImage.Source = info.Thumbnail;
                            CompactThumbnail.Source = info.Thumbnail;
                            PlayThumbnailRevealAnimation();
                            break;

                        case ThumbnailAction.AnimateSwitch:
                            AnimateThumbnailSwitchOnly(info.Thumbnail, force: true);
                            PlayTrackChangeBounce();
                            break;

                        case ThumbnailAction.AnimateUpdate:
                            AnimateThumbnailSwitchOnly(info.Thumbnail, force: true);
                            break;

                        case ThumbnailAction.None:
                            // No animation needed
                            break;
                    }

                    ThumbnailImage.Visibility = Visibility.Visible;
                    ThumbnailFallback.Visibility = Visibility.Collapsed;

                    if (result.NeedsBackgroundUpdate)
                    {
                        UpdateMediaBackground(info);
                    }
                }
                else if (result.IsNewTrack)
                {
                    if (ThumbnailImage.Source == null)
                    {
                        ThumbnailImage.Visibility = Visibility.Collapsed;
                        ThumbnailFallback.Visibility = Visibility.Visible;
                        ThumbnailFallback.Text = "🎵";
                    }
                    else
                    {
                        if (CompactThumbnail.Source == null)
                        {
                            CompactThumbnail.Source = ThumbnailImage.Source;
                        }
                        ThumbnailImage.Visibility = Visibility.Visible;
                        ThumbnailFallback.Visibility = Visibility.Collapsed;
                    }
                }
            }
            else
            {
                if (info.IsAnyMediaPlaying)
                {
                    if (ThumbnailImage.Source != null)
                    {
                        ThumbnailImage.Visibility = Visibility.Visible;
                        ThumbnailFallback.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ThumbnailImage.Visibility = Visibility.Collapsed;
                        ThumbnailFallback.Visibility = Visibility.Visible;
                        ThumbnailFallback.Text = "🎵";
                    }
                }
                else
                {
                    _thumbnailSwitchGeneration = _mediaDisplayController.ThumbnailSwitchGeneration;
                    CancelThumbnailSwitchAnimations();
                    ThumbnailImage.Visibility = Visibility.Collapsed;
                    ThumbnailFallback.Visibility = Visibility.Visible;
                    HideMediaBackground();
                    ThumbnailFallback.Text = "🎵";
                    ClearLyrics();
                }
            }

            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds > 500 && _isPlaying != info.IsPlaying)
            {
                _isPlaying = info.IsPlaying;
                UpdatePlayPauseIcon();
            }

            UpdateProgressTracking(info);
            UpdateMusicCompactMode(info);

            MusicViz.TrackId = info?.GetSignature() ?? "";
            MusicViz.IsPlaying = info?.IsPlaying ?? false;
        });
    }

    private void AnimateThumbnailSwitchOnly(ImageSource newThumb, bool force = false)
    {
        if (_isAnimating)
        {
            // Queue the transition to run after the expand/collapse animation finishes
            VNotch.Services.RuntimeLog.Log("THUMB-ANIM", $"queued-pending (isAnimating=true) force={force}");
            _pendingFlipThumbnail = newThumb;
            return;
        }
        if (!force && newThumb != null && ReferenceEquals(ThumbnailImage.Source, newThumb))
        {
            VNotch.Services.RuntimeLog.Log("THUMB-ANIM", "skipped (same reference, no force)");
            return;
        } 
        if (!force && _thumbnailShownForCurrentTrack && newThumb != null)
        {
            VNotch.Services.RuntimeLog.Log("THUMB-ANIM",
                "skipped (thumbnail already shown for current track, no force)");
            return;
        }

        VNotch.Services.RuntimeLog.Log("THUMB-ANIM", $"BLUR-MORPH-START force={force}");

        CancelThumbnailSwitchAnimations();

        var generation = ++_thumbnailSwitchGeneration;

       
        var outDur     = TimeSpan.FromMilliseconds(420);
        var inDur      = TimeSpan.FromMilliseconds(440);
        var overlap    = TimeSpan.FromMilliseconds(180); // when the new layer starts
        var totalDur   = overlap + inDur;

        const double expandedPeakBlur = 18.0;
        const double compactPeakBlur  = 6.0;

        var quintOut  = new QuinticEase  { EasingMode = EasingMode.EaseOut };
        var cubicOut  = new CubicEase    { EasingMode = EasingMode.EaseOut };
        var cubicIn   = new CubicEase    { EasingMode = EasingMode.EaseIn  };
        var sineInOut = new SineEase     { EasingMode = EasingMode.EaseInOut };

        // Stage incoming image on the overlay layer, pre-blurred & invisible.
        ThumbnailImageNext.Source = newThumb;
        CompactThumbnailNext.Source = newThumb;

        ThumbnailNextScale.ScaleX = 1.05;
        ThumbnailNextScale.ScaleY = 1.05;
        ThumbnailImageNext.Opacity = 0.0;
        ThumbnailNextBlur.Radius = expandedPeakBlur;

        CompactThumbnailNextScale.ScaleX = 1.05;
        CompactThumbnailNextScale.ScaleY = 1.05;
        CompactThumbnailNext.Opacity = 0.0;
        CompactThumbnailNextBlur.Radius = compactPeakBlur;

        // Reset outgoing layer to pristine starting state.
        ThumbnailOutScale.ScaleX = 1.0;
        ThumbnailOutScale.ScaleY = 1.0;
        ThumbnailImage.Opacity = 1.0;
        ThumbnailOutBlur.Radius = 0.0;

        CompactThumbnailOutScale.ScaleX = 1.0;
        CompactThumbnailOutScale.ScaleY = 1.0;
        CompactThumbnail.Opacity = 1.0;
        CompactThumbnailOutBlur.Radius = 0.0;

        // ─── Outgoing animations ───
        // Blur ramps up first (cubic out → fast at start, slow at end), then
        // opacity fades (cubic in → starts slow, accelerates) so dissolve feels
        // gradual instead of an instant fade.
        var outBlurExpanded = new DoubleAnimation(0.0, expandedPeakBlur, outDur) { EasingFunction = cubicOut };
        var outBlurCompact  = new DoubleAnimation(0.0, compactPeakBlur,  outDur) { EasingFunction = cubicOut };
        var outFade         = new DoubleAnimation(1.0, 0.0, outDur)              { EasingFunction = cubicIn };
        var outScale        = new DoubleAnimation(1.0, 0.96, totalDur)           { EasingFunction = sineInOut };
        Timeline.SetDesiredFrameRate(outBlurExpanded, 60);
        Timeline.SetDesiredFrameRate(outBlurCompact, 60);
        Timeline.SetDesiredFrameRate(outFade, 144);
        Timeline.SetDesiredFrameRate(outScale, 144);

        // ─── Incoming animations (delayed by 'overlap') ───
        // Hold blurred + invisible for 'overlap', then bloom into focus.
        DoubleAnimationUsingKeyFrames MakeInBlur(double peak)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, KeyTime.FromTimeSpan(overlap)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(totalDur), quintOut));
            return a;
        }

        var inBlurExpanded = MakeInBlur(expandedPeakBlur);
        var inBlurCompact  = MakeInBlur(compactPeakBlur);

        var inFade = new DoubleAnimationUsingKeyFrames();
        inFade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        inFade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(overlap)));
        inFade.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(totalDur), cubicOut));

        var inScale = new DoubleAnimationUsingKeyFrames();
        inScale.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        inScale.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(overlap)));
        inScale.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(totalDur), quintOut));

        Timeline.SetDesiredFrameRate(inBlurExpanded, 60);
        Timeline.SetDesiredFrameRate(inBlurCompact, 60);
        Timeline.SetDesiredFrameRate(inFade, 144);
        Timeline.SetDesiredFrameRate(inScale, 144);

        // ─── Apply to expanded view ───
        ThumbnailImage.BeginAnimation(OpacityProperty, outFade);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, outScale);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, outScale);
        ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, outBlurExpanded);
        ThumbnailImageNext.BeginAnimation(OpacityProperty, inFade);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, inScale);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, inScale);
        ThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, inBlurExpanded);

        // ─── Apply to compact view ───
        CompactThumbnail.BeginAnimation(OpacityProperty, outFade);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, outScale);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, outScale);
        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, outBlurCompact);
        CompactThumbnailNext.BeginAnimation(OpacityProperty, inFade);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, inScale);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, inScale);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, inBlurCompact);

        // Single Completed handler on the longest timeline commits the swap:
        // promote the overlay's image to the base layer, clear the overlay,
        // and reset every transient transform/opacity/blur.
        inScale.Completed += (s, e) =>
        {
            if (_thumbnailSwitchGeneration != generation) return;

            // Stop & clear all animations.
            ThumbnailImage.BeginAnimation(OpacityProperty, null);
            ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            ThumbnailImageNext.BeginAnimation(OpacityProperty, null);
            ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

            CompactThumbnail.BeginAnimation(OpacityProperty, null);
            CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
            CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

            // Promote overlay → base.
            ThumbnailImage.Source = newThumb;
            CompactThumbnail.Source = newThumb;
            if (_isLyricsActive && LyricsBlurImage != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
            }
            ThumbnailImageNext.Source = null;
            CompactThumbnailNext.Source = null;

            // Reset all transient state.
            ThumbnailImage.Opacity = 1.0;
            CompactThumbnail.Opacity = 1.0;
            ThumbnailImageNext.Opacity = 0.0;
            CompactThumbnailNext.Opacity = 0.0;
            ThumbnailOutScale.ScaleX = 1.0;
            ThumbnailOutScale.ScaleY = 1.0;
            CompactThumbnailOutScale.ScaleX = 1.0;
            CompactThumbnailOutScale.ScaleY = 1.0;
            ThumbnailNextScale.ScaleX = 1.0;
            ThumbnailNextScale.ScaleY = 1.0;
            CompactThumbnailNextScale.ScaleX = 1.0;
            CompactThumbnailNextScale.ScaleY = 1.0;
            ThumbnailOutBlur.Radius = 0.0;
            ThumbnailNextBlur.Radius = 0.0;
            CompactThumbnailOutBlur.Radius = 0.0;
            CompactThumbnailNextBlur.Radius = 0.0;
        };
    }

    private void CancelThumbnailSwitchAnimations(ImageSource? targetThumb = null)
    {
        // Stop every active animation on both layers.
        ThumbnailImage.BeginAnimation(OpacityProperty, null);
        ThumbnailImage.BeginAnimation(Image.SourceProperty, null);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        ThumbnailImageNext.BeginAnimation(OpacityProperty, null);
        ThumbnailImageNext.BeginAnimation(Image.SourceProperty, null);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        CompactThumbnail.BeginAnimation(OpacityProperty, null);
        CompactThumbnail.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
        CompactThumbnailNext.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        // Reset transforms + blur.
        ThumbnailOutScale.ScaleX = 1.0;
        ThumbnailOutScale.ScaleY = 1.0;
        CompactThumbnailOutScale.ScaleX = 1.0;
        CompactThumbnailOutScale.ScaleY = 1.0;
        ThumbnailNextScale.ScaleX = 1.0;
        ThumbnailNextScale.ScaleY = 1.0;
        CompactThumbnailNextScale.ScaleX = 1.0;
        CompactThumbnailNextScale.ScaleY = 1.0;
        ThumbnailOutBlur.Radius = 0.0;
        ThumbnailNextBlur.Radius = 0.0;
        CompactThumbnailOutBlur.Radius = 0.0;
        CompactThumbnailNextBlur.Radius = 0.0;

        // If an in-flight transition was interrupted, the overlay layer may hold
        // the most recent target frame — promote it onto the base so the user
        // doesn't see the old thumbnail snap back.
        var overlayTarget = ThumbnailImageNext.Source ?? CompactThumbnailNext.Source;
        var resolvedThumb = targetThumb ?? overlayTarget ?? _currentMediaInfo?.Thumbnail
                            ?? ThumbnailImage.Source ?? CompactThumbnail.Source;
        if (resolvedThumb != null)
        {
            ThumbnailImage.Source = resolvedThumb;
            CompactThumbnail.Source = resolvedThumb;
        }

        // Clear overlay so it doesn't bleed through on the next frame.
        ThumbnailImageNext.Source = null;
        CompactThumbnailNext.Source = null;
        ThumbnailImageNext.Opacity = 0.0;
        CompactThumbnailNext.Opacity = 0.0;
        ThumbnailImage.Opacity = 1.0;
        CompactThumbnail.Opacity = 1.0;
    }

    #endregion

    #region Music Compact Mode

    private void UpdateMusicCompactMode(MediaInfo info)
    {
        bool shouldBeCompact = _mediaDisplayController.ShouldBeCompactMode(info);

        _collapsedWidth = _settings.Width;

        if (shouldBeCompact == _isMusicCompactMode)
        {
            if (shouldBeCompact)
            {
                if (info?.Thumbnail != null)
                {
                    if (_mediaDisplayController.ShouldAnimateCompactThumbnail(info))
                    {
                        _lastAnimatedTrackSignature = _mediaDisplayController.LastAnimatedTrackSignature;
                        AnimateThumbnailSwitchOnly(info.Thumbnail);
                    }
                }
                
                if (info != null)
                {
                    MusicViz.IsPlaying = info.IsPlaying;
                    MusicViz.TrackId = info.GetSignature();
                }
            }
            return;
        }

        _isMusicCompactMode = shouldBeCompact;
        
        if (!_isExpanded)
        {
            // Don't animate content switch while bluetooth notification is showing —
            // the notification owns the collapsed area. State is updated so that
            // AnimateBluetoothNotificationOut restores the correct content.
            if (_isBluetoothNotificationVisible)
            {
                return;
            }

            VNotch.Services.RuntimeLog.Log("NOTCH-WIDTH",
                $"UpdateMusicCompactMode -> animating to _collapsedWidth={_collapsedWidth}, shouldBeCompact={shouldBeCompact}, _isExpanded={_isExpanded}");

            var widthAnim = new DoubleAnimation(_collapsedWidth, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = _easeExpOut6
            };
            NotchBorder.BeginAnimation(WidthProperty, widthAnim);

            if (_isMusicCompactMode)
            {
                if (info?.Thumbnail != null)
                {
                    // When re-entering compact mode for the same track (e.g. pause→play),
                    // don't touch the source at all. The track hasn't changed — the
                    // existing thumbnail is correct. Setting source would trigger a
                    // re-render which can cause a visible flash/shift.
                    string compactTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
                    if (compactTrackId != _lastAnimatedTrackSignature && !_thumbnailShownForCurrentTrack)
                    {
                        AnimateThumbnailSwitchOnly(info.Thumbnail);
                        _thumbnailShownForCurrentTrack = true;
                    }
                }
                FadeSwitch(CollapsedContent, MusicCompactContent);
            }
            else
            {
                FadeSwitch(MusicCompactContent, CollapsedContent);
            }
        }
        else
        {
            MusicCompactContent.Visibility = Visibility.Collapsed;
            MusicCompactContent.Opacity = 0;
            CollapsedContent.Visibility = Visibility.Collapsed;
            CollapsedContent.Opacity = 0;
        }
    }

    #endregion

    #region Thumbnail Transition

    /// <summary>
    /// Plays a subtle "pop-in" reveal animation on the compact thumbnail
    /// when it transitions from empty (null) to having an image for the first time.
    /// Scale 0→1 with a soft spring + opacity fade-in for a polished feel.
    /// </summary>
    private void PlayThumbnailRevealAnimation()
    {
        // Start from scale 0 and opacity 0
        CompactThumbnailScale.ScaleX = 0.0;
        CompactThumbnailScale.ScaleY = 0.0;
        CompactThumbnailBorder.Opacity = 0.0;

        var duration = TimeSpan.FromMilliseconds(450);
        var dur = new Duration(duration);

        // Scale animation: 0 → 1 with soft spring for a bouncy pop-in
        var scaleAnimX = MakeAnim(0.0, 1.0, dur, _easeSoftSpring);
        var scaleAnimY = MakeAnim(0.0, 1.0, dur, _easeSoftSpring);
        Timeline.SetDesiredFrameRate(scaleAnimX, 120);
        Timeline.SetDesiredFrameRate(scaleAnimY, 120);

        // Opacity animation: 0 → 1, slightly faster so it's visible early
        var opacityAnim = MakeAnim(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(250)), _easeQuadOut);
        Timeline.SetDesiredFrameRate(opacityAnim, 60);

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimX);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimY);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, opacityAnim);

        // Clean up after animation completes
        scaleAnimX.Completed += (s, e) =>
        {
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailScale.ScaleX = 1.0;
            CompactThumbnailScale.ScaleY = 1.0;
        };

        opacityAnim.Completed += (s, e) =>
        {
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailBorder.Opacity = 1.0;
        };
    }

    #endregion
}
