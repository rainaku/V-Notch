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
    private bool _isThumbnailSwitchActive = false;

    private static readonly string[] _genericTitles = { "Spotify", "Spotify Premium", "Spotify Free", "YouTube", "SoundCloud", "Browser" };

    #region Media Changed Handler

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        if (!info.IsThumbnailOnlyUpdate)
            _currentMediaInfo = info;

        Dispatcher.BeginInvoke(() =>
        {
            if (info.IsThumbnailOnlyUpdate && _currentMediaInfo != null)
            {
                string incomingTrack = info.CurrentTrack ?? "";
                string currentTrack = _currentMediaInfo.CurrentTrack ?? "";
                if (!string.IsNullOrEmpty(currentTrack) && !string.IsNullOrEmpty(incomingTrack) &&
                    !string.Equals(incomingTrack, currentTrack, StringComparison.OrdinalIgnoreCase))
                {
                    VNotch.Services.RuntimeLog.Log("MEDIA-THUMB", $"Rejected stale thumbnail: incoming='{incomingTrack}' current='{currentTrack}'");
                    return;
                }
            }

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
                else if (result.HasRealTrack && renderedSource == "YouTube")
                {
                    FetchSubtitlesForTrack(info);
                }
                else
                {
                    ClearLyrics();
                }
            }
            else
            {
                TrackTitle.Text = result.DisplayText.Title;
                TrackArtist.Text = result.DisplayText.Artist;
                CompactTitleMarquee.Text = result.DisplayText.Title;

                if (result.HasRealTrack && renderedSource == "YouTube"
                    && !string.IsNullOrEmpty(info.YouTubeVideoId))
                {
                    FetchSubtitlesForTrack(info);
                }
            }

            // ─── Shimmer effect for idle state ───
            if (!result.HasRealTrack && !info.IsAnyMediaPlaying)
            {
                StartTitleShimmer();
            }
            else
            {
                StopTitleShimmer();
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
                    }
                    else
                    {
                        var transitionBlur = new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(300))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        ThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, transitionBlur);
                        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty,
                            new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(300))
                            {
                                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                            });

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
                    }
                }
                else
                {
                    _thumbnailSwitchGeneration = _mediaDisplayController.ThumbnailSwitchGeneration;
                    CancelThumbnailSwitchAnimations();
                    ThumbnailImage.Visibility = Visibility.Collapsed;
                    ThumbnailFallback.Visibility = Visibility.Visible;
                    HideMediaBackground();
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
        if (IsCountdownCompletionVisualActive)
        {
            ThumbnailImage.Source = newThumb;
            CompactThumbnail.Source = newThumb;
            SuppressCompactMediaChromeForCountdownCompletion();
            VNotch.Services.RuntimeLog.Log("THUMB-ANIM", "skipped (countdown completion overlay active)");
            return;
        }

        if (_isAnimating)
        {
            // Queue the transition to run after the expand/collapse animation finishes
            VNotch.Services.RuntimeLog.Log("THUMB-ANIM", $"queued-pending (isAnimating=true) force={force}");
            _pendingFlipThumbnail = newThumb;
            return;
        }
        if (_isThumbnailSwitchActive && !force)
        {
            // A blur morph is already in progress for the same track (e.g. async thumbnail update) —
            // just update the target thumbnail on the overlay layer instead of restarting.
            VNotch.Services.RuntimeLog.Log("THUMB-ANIM",
                $"coalesced (blur morph already active, updating target) force={force}");
            ThumbnailImageNext.Source = newThumb;
            CompactThumbnailNext.Source = newThumb;
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

        _suppressOutsideClickUntilUtc = DateTime.UtcNow.AddMilliseconds(700);

        CancelThumbnailSwitchAnimations();

        var generation = ++_thumbnailSwitchGeneration;
        _isThumbnailSwitchActive = true;

       
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
        ThumbnailImageNext.Visibility = Visibility.Visible;
        CompactThumbnailNext.Visibility = Visibility.Visible;

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
        CompactThumbnailOutBlur.Radius = 0.0;

        bool compactWasHidden = CompactThumbnailBorder.Visibility != Visibility.Visible ||
                                CompactThumbnailBorder.Opacity < 0.01;
        CompactThumbnail.Opacity = compactWasHidden ? 0.0 : 1.0;

        var outBlurExpanded = new DoubleAnimation(0.0, expandedPeakBlur, outDur) { EasingFunction = cubicOut };
        var outBlurCompact  = new DoubleAnimation(0.0, compactPeakBlur,  outDur) { EasingFunction = cubicOut };
        var outFade         = new DoubleAnimation(1.0, 0.0, outDur)              { EasingFunction = cubicIn };
        var outScale        = new DoubleAnimation(1.0, 0.96, totalDur)           { EasingFunction = sineInOut };
        Timeline.SetDesiredFrameRate(outBlurExpanded, 60);
        Timeline.SetDesiredFrameRate(outBlurCompact, 60);
        Timeline.SetDesiredFrameRate(outFade, 144);
        Timeline.SetDesiredFrameRate(outScale, 144);

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

        inScale.Completed += (s, e) =>
        {
            if (_thumbnailSwitchGeneration != generation) return;
            _isThumbnailSwitchActive = false;

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

            // Promote overlay → base (use actual overlay source in case it was coalesced).
            var finalThumb = ThumbnailImageNext.Source ?? newThumb;
            ThumbnailImage.Source = finalThumb;
            CompactThumbnail.Source = finalThumb;
            if (_isLyricsActive && LyricsBlurImage != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
            }
            ThumbnailImageNext.Source = null;
            CompactThumbnailNext.Source = null;
            ThumbnailImageNext.Visibility = Visibility.Collapsed;
            CompactThumbnailNext.Visibility = Visibility.Collapsed;

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

            if (_isMusicCompactMode && _currentMediaInfo?.IsPlaying == true
                && !_isClipboardPeekActive && !_isVolumeIndicatorActive)
            {
                ShowMusicVisualizer(animate: false);
            }
        };
    }

    private void CancelThumbnailSwitchAnimations(ImageSource? targetThumb = null)
    {
        _isThumbnailSwitchActive = false;

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

        var overlayTarget = ThumbnailImageNext.Source ?? CompactThumbnailNext.Source;
        var resolvedThumb = targetThumb ?? overlayTarget
                            ?? ThumbnailImage.Source ?? CompactThumbnail.Source;
        if (resolvedThumb != null)
        {
            ThumbnailImage.Source = resolvedThumb;
            CompactThumbnail.Source = resolvedThumb;
        }

        // Clear overlay so it doesn't bleed through on the next frame.
        ThumbnailImageNext.Source = null;
        CompactThumbnailNext.Source = null;
        ThumbnailImageNext.Visibility = Visibility.Collapsed;
        CompactThumbnailNext.Visibility = Visibility.Collapsed;
        ThumbnailImageNext.Opacity = 0.0;
        CompactThumbnailNext.Opacity = 0.0;
        ThumbnailImage.Opacity = 1.0;
        CompactThumbnail.Opacity = 1.0;
    }

    private void CancelThumbnailSwitchForExpand()
    {
        _isThumbnailSwitchActive = false;

        // Stop animations on expanded layers (these will be used by expand).
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

        CompactThumbnail.BeginAnimation(Image.SourceProperty, null);

        // Capture current animated values before cancelling to prevent snap-back.
        double compactOutScaleX = CompactThumbnailOutScale.ScaleX;
        double compactOutScaleY = CompactThumbnailOutScale.ScaleY;
        double compactOutBlur = CompactThumbnailOutBlur.Radius;
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailOutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailOutBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CompactThumbnailOutScale.ScaleX = compactOutScaleX;
        CompactThumbnailOutScale.ScaleY = compactOutScaleY;
        CompactThumbnailOutBlur.Radius = compactOutBlur;

        CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
        CompactThumbnailNext.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        double compactThumbOpacity = CompactThumbnail.Opacity;
        CompactThumbnail.BeginAnimation(OpacityProperty, null);
        CompactThumbnail.Opacity = compactThumbOpacity;

        // Reset expanded layer transforms (these are needed clean for expand).
        ThumbnailOutScale.ScaleX = 1.0;
        ThumbnailOutScale.ScaleY = 1.0;
        ThumbnailNextScale.ScaleX = 1.0;
        ThumbnailNextScale.ScaleY = 1.0;
        ThumbnailOutBlur.Radius = 0.0;
        ThumbnailNextBlur.Radius = 0.0;

        var overlayTarget = ThumbnailImageNext.Source ?? CompactThumbnailNext.Source;
        var resolvedThumb = overlayTarget
                            ?? ThumbnailImage.Source ?? CompactThumbnail.Source;
        if (resolvedThumb != null)
        {
            ThumbnailImage.Source = resolvedThumb;
            CompactThumbnail.Source = resolvedThumb;
        }

        // Clear overlay layers.
        ThumbnailImageNext.Source = null;
        CompactThumbnailNext.Source = null;
        ThumbnailImageNext.Visibility = Visibility.Collapsed;
        CompactThumbnailNext.Visibility = Visibility.Collapsed;
        ThumbnailImageNext.Opacity = 0.0;
        CompactThumbnailNext.Opacity = 0.0;
        ThumbnailImage.Opacity = 1.0;
    }

    private void ResetCompactThumbnailNextLayer()
    {
        CompactThumbnailNext.BeginAnimation(OpacityProperty, null);
        CompactThumbnailNext.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailNextScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailNextBlur.BeginAnimation(BlurEffect.RadiusProperty, null);

        CompactThumbnailNext.Source = null;
        CompactThumbnailNext.Visibility = Visibility.Collapsed;
        CompactThumbnailNext.Opacity = 0.0;
        CompactThumbnailNextScale.ScaleX = 1.0;
        CompactThumbnailNextScale.ScaleY = 1.0;
        CompactThumbnailNextBlur.Radius = 0.0;
    }

    #endregion

    #region Music Compact Mode

    private void ShowMusicVisualizer(bool animate = true, Duration? duration = null)
    {
        double currentOpacity = Math.Clamp(MusicViz.Opacity, 0.0, 1.0);

        // Promote the current animated value before clearing the animation. Otherwise WPF
        // falls back to the old base opacity, which can make the visualizer blink on polling updates.
        MusicViz.Opacity = currentOpacity;
        MusicViz.BeginAnimation(OpacityProperty, null);
        MusicViz.Visibility = Visibility.Visible;

        if (!animate || currentOpacity >= 0.5)
        {
            MusicViz.Opacity = 1.0;
            return;
        }

        var vizFadeIn = MakeAnim(currentOpacity, 1.0, duration ?? _dur200, _easeQuadOut);
        vizFadeIn.Completed += (s, e) =>
        {
            MusicViz.Opacity = 1.0;
            MusicViz.BeginAnimation(OpacityProperty, null);
        };
        MusicViz.BeginAnimation(OpacityProperty, vizFadeIn);
    }

    private void UpdateMusicCompactMode(MediaInfo info)
    {
        bool shouldBeCompact = _mediaDisplayController.ShouldBeCompactMode(info);

        _collapsedWidth = GetCollapsedWidth();

        if (IsCountdownCompletionVisualActive)
        {
            _isMusicCompactMode = shouldBeCompact;

            if (info?.Thumbnail != null)
            {
                ThumbnailImage.Source = info.Thumbnail;
                CompactThumbnail.Source = info.Thumbnail;
            }

            SuppressCompactMediaChromeForCountdownCompletion();
            return;
        }

        if (shouldBeCompact == _isMusicCompactMode)
        {
            if (shouldBeCompact)
            {
                if (info?.Thumbnail != null)
                {
                    if (_mediaDisplayController.ShouldAnimateCompactThumbnail(info))
                    {
                        _lastAnimatedTrackSignature = _mediaDisplayController.LastAnimatedTrackSignature;
                        // Don't animate thumbnail while clipboard notification is showing
                        if (!_isClipboardPeekActive)
                        {
                            AnimateThumbnailSwitchOnly(info.Thumbnail);
                            PlayTrackChangeBounce();
                        }
                    }
                }
                
                if (info != null)
                {
                    // Don't restore MusicViz while clipboard notification is showing
                    if (!_isClipboardPeekActive)
                    {
                        MusicViz.IsPlaying = info.IsPlaying;
                        MusicViz.TrackId = info.GetSignature();

                        if (info.IsPlaying && !_isVolumeIndicatorActive)
                        {
                            ShowMusicVisualizer(duration: _dur200);
                        }
                    }
                }
            }
            return;
        }

        _isMusicCompactMode = shouldBeCompact;
        
        if (!_isExpanded)
        {
            if (_isBluetoothNotificationVisible || _isChargingNotificationVisible || _isGreetingActive || _isAnimating)
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
                if (info?.Thumbnail != null && !_isClipboardPeekActive)
                {
                    string compactTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
                    if (compactTrackId != _lastAnimatedTrackSignature && !_thumbnailShownForCurrentTrack)
                    {
                        bool wasHidden = CompactThumbnailBorder.Visibility != Visibility.Visible ||
                                         CompactThumbnailBorder.Opacity < 0.01;

                        if (wasHidden)
                        {
                            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
                            CompactThumbnailBorder.Visibility = Visibility.Visible;
                            CompactThumbnail.Source = info.Thumbnail;
                            ThumbnailImage.Source = info.Thumbnail;
                            PlayThumbnailRevealAnimation();
                        }
                        else
                        {
                            AnimateThumbnailSwitchOnly(info.Thumbnail);
                        }
                        _thumbnailShownForCurrentTrack = true;
                    }
                    else
                    {
                        ResetCompactThumbnailRestingState();
                    }
                }
                else
                {
                    ResetCompactThumbnailRestingState();
                }
                // Don't switch to MusicCompactContent while clipboard notification is active
                if (!_isClipboardPeekActive)
                {
                    FadeSwitch(CollapsedContent, MusicCompactContent);
                }
            }
            else
            {
                PlayCompactThumbnailExitAnimation(() =>
                {
                    FadeSwitch(MusicCompactContent, CollapsedContent);
                });
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

    private void PlayThumbnailRevealAnimation()
    {
        if (IsCountdownCompletionVisualActive)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
            return;
        }

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

    private void PlayCompactThumbnailExitAnimation(Action? onCompleted = null)
    {
        var originalOrigin = CompactThumbnailBorder.RenderTransformOrigin;
        CompactThumbnailBorder.RenderTransformOrigin = new Point(0.5, 0.5);

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;
        CompactThumbnailBorder.Opacity = 1.0;

        var dur = new Duration(TimeSpan.FromMilliseconds(260));

        // Scale 1 → 0 with a slight anticipation (BackEase) for an organic shrink
        var backIn = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.3 };
        var scaleAnimX = MakeAnim(1.0, 0.0, dur, backIn);
        var scaleAnimY = MakeAnim(1.0, 0.0, dur, backIn);
        Timeline.SetDesiredFrameRate(scaleAnimX, 120);
        Timeline.SetDesiredFrameRate(scaleAnimY, 120);

        // Opacity 1 → 0, a touch faster so it's gone by the time scale finishes
        var opacityAnim = MakeAnim(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(200)), _easeQuadIn);
        Timeline.SetDesiredFrameRate(opacityAnim, 60);

        // ─── MusicViz: fade out simultaneously with thumbnail ───
        MusicViz.BeginAnimation(OpacityProperty, null);
        var vizFadeOut = MakeAnim(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(200)), _easeQuadIn);
        vizFadeOut.Completed += (s, e) =>
        {
            MusicViz.Visibility = Visibility.Collapsed;
            MusicViz.IsPlaying = false;
        };
        MusicViz.BeginAnimation(OpacityProperty, vizFadeOut);

        // ─── Notch: subtle squeeze animation (shrink slightly then back) ───
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var squeezePeak = TimeSpan.FromMilliseconds(130);
        var squeezeEnd = TimeSpan.FromMilliseconds(400);

        var squeezeX = new DoubleAnimationUsingKeyFrames();
        squeezeX.KeyFrames.Add(new EasingDoubleKeyFrame(0.97,
            KeyTime.FromTimeSpan(squeezePeak), _easeQuadOut));
        squeezeX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(squeezeEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(squeezeX, 120);

        var squeezeY = new DoubleAnimationUsingKeyFrames();
        squeezeY.KeyFrames.Add(new EasingDoubleKeyFrame(1.02,
            KeyTime.FromTimeSpan(squeezePeak), _easeQuadOut));
        squeezeY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(squeezeEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(squeezeY, 120);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, squeezeX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, squeezeY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, squeezeX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, squeezeY);

        scaleAnimX.Completed += (s, e) =>
        {
            CompactThumbnailBorder.Visibility = Visibility.Collapsed;
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
            CompactThumbnailScale.ScaleX = 1.0;
            CompactThumbnailScale.ScaleY = 1.0;
            CompactThumbnailBorder.Opacity = 1.0;
            CompactThumbnailBorder.RenderTransformOrigin = originalOrigin;
            onCompleted?.Invoke();
        };

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimX);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimY);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void ResetCompactThumbnailRestingState()
    {
        if (IsCountdownCompletionVisualActive)
        {
            SuppressCompactMediaChromeForCountdownCompletion();
            return;
        }

        if (!_isThumbnailSwitchActive)
        {
            ResetCompactThumbnailNextLayer();
        }

        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;
        CompactThumbnailBorder.Opacity = 1.0;
        CompactThumbnailBorder.Visibility = Visibility.Visible;
    }

    #endregion

    #region Title Shimmer Effect

    private Storyboard? _titleShimmerStoryboard;
    private bool _isShimmerActive;

    private void StartTitleShimmer()
    {
        if (_isShimmerActive) return;
        _isShimmerActive = true;

        // Create a shimmer gradient brush: base color → white highlight → base color
        var shimmerBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        // All stops start off-screen to the left so the loop is seamless
        var stop0 = new GradientStop(Color.FromArgb(180, 255, 255, 255), -0.5);
        var stop1 = new GradientStop(Color.FromArgb(255, 255, 255, 255), -0.3);
        var stop2 = new GradientStop(Color.FromArgb(180, 255, 255, 255), -0.1);

        shimmerBrush.GradientStops.Add(stop0);
        shimmerBrush.GradientStops.Add(stop1);
        shimmerBrush.GradientStops.Add(stop2);

        // Apply the brush to the TrackTitle
        TrackTitle.Foreground = shimmerBrush;

        var duration = TimeSpan.FromMilliseconds(2500);
        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        // Stop 0: leading edge of highlight
        var anim0 = new DoubleAnimation
        {
            From = -0.5,
            To = 1.1,
            Duration = duration
        };
        Storyboard.SetTarget(anim0, TrackTitle);
        Storyboard.SetTargetProperty(anim0,
            new PropertyPath("(TextBlock.Foreground).(GradientBrush.GradientStops)[0].(GradientStop.Offset)"));

        // Stop 1: center of highlight (bright peak)
        var anim1 = new DoubleAnimation
        {
            From = -0.3,
            To = 1.3,
            Duration = duration
        };
        Storyboard.SetTarget(anim1, TrackTitle);
        Storyboard.SetTargetProperty(anim1,
            new PropertyPath("(TextBlock.Foreground).(GradientBrush.GradientStops)[1].(GradientStop.Offset)"));

        // Stop 2: trailing edge of highlight
        var anim2 = new DoubleAnimation
        {
            From = -0.1,
            To = 1.5,
            Duration = duration
        };
        Storyboard.SetTarget(anim2, TrackTitle);
        Storyboard.SetTargetProperty(anim2,
            new PropertyPath("(TextBlock.Foreground).(GradientBrush.GradientStops)[2].(GradientStop.Offset)"));

        storyboard.Children.Add(anim0);
        storyboard.Children.Add(anim1);
        storyboard.Children.Add(anim2);

        _titleShimmerStoryboard = storyboard;
        storyboard.Begin(this, true);
    }

    private void StopTitleShimmer()
    {
        if (!_isShimmerActive) return;
        _isShimmerActive = false;

        _titleShimmerStoryboard?.Stop(this);
        _titleShimmerStoryboard = null;

        // Restore the normal gradient brush from resources
        if (TryFindResource("TrackTitleGradient") is Brush normalBrush)
        {
            TrackTitle.Foreground = normalBrush;
        }
        else
        {
            TrackTitle.Foreground = Brushes.White;
        }
    }

    #endregion
}
