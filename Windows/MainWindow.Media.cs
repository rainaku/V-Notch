using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private DateTime _lastAnimationStartTime = DateTime.MinValue;
    private int _thumbnailSwitchGeneration = 0;

    private static readonly string[] _genericTitles = { "Spotify", "Spotify Premium", "Spotify Free", "YouTube", "SoundCloud", "Browser" };

    #region Media Changed Handler

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        _currentMediaInfo = info;

        Dispatcher.BeginInvoke(() =>
        {
            // Guard: if this is a thumbnail-only update from an async fetch,
            // verify it still matches the track currently displayed. If the user
            // has already moved to a different track, discard the stale thumbnail.
            if (info.IsThumbnailOnlyUpdate)
            {
                string incomingTrackId = $"{info.CurrentTrack}|{info.CurrentArtist}";
                if (!string.IsNullOrEmpty(_lastAnimatedTrackSignature) &&
                    incomingTrackId != _lastAnimatedTrackSignature)
                {
                    return; // Stale thumbnail for a previous track — ignore
                }
            }

            // Skip expensive UI updates when notch is collapsed and no compact thumbnail visible
            bool isCollapsedWithoutCompact = !_isExpanded && !_isMusicExpanded && !_isMusicCompactMode;

            bool hasRealTrack = !string.IsNullOrEmpty(info.CurrentTrack);

            // Stable source for display purposes.
            //
            // The backend can flip-flop MediaSource between e.g. "Browser" and
            // "SoundCloud" on the same track due to window-title heuristics +
            // cached track→source map. We handle that here by:
            //  1) not changing the rendered icon when the source is unchanged;
            //  2) not "degrading" a known-specific source (Spotify/YouTube/
            //     SoundCloud/…) back to the generic "Browser" fallback while
            //     still playing the same track. Only upgrades (Browser -> X) or
            //     a real track change can flip the icon.
            string incomingSource = hasRealTrack ? (info.MediaSource ?? "") : "";
            string currentTrackKey = $"{info.CurrentTrack}|{info.CurrentArtist}";
            bool sameTrackAsBefore = hasRealTrack &&
                                     currentTrackKey == _lastAnimatedTrackSignature;

            string renderedSource = incomingSource;
            if (sameTrackAsBefore &&
                !string.IsNullOrEmpty(_lastRenderedMediaSource) &&
                _lastRenderedMediaSource != "Browser" &&
                (incomingSource == "" || incomingSource == "Browser"))
            {
                // Keep the previously-rendered specific source instead of flipping
                // back to the generic Browser icon on the same track.
                renderedSource = _lastRenderedMediaSource;
            }

            if (renderedSource != _lastRenderedMediaSource)
            {
                // Platform icons removed — no icon switching needed
            }

            string currentSig = info.GetSignature();
            // Track identity used to decide whether to re-animate the thumbnail.
            // IMPORTANT: do NOT include MediaSource here — the backend can flip-flop
            // the source between e.g. "Browser" and "SoundCloud" on the same track
            // (stale window-title heuristics + cached track->source map), and using
            // GetSignature() here would re-trigger the flip animation and icon swap
            // every time. The thumbnail should only "change" when the track itself
            // changes (title + artist).
            string trackIdentity = $"{info.CurrentTrack}|{info.CurrentArtist}";
            bool isNewTrack = trackIdentity != _lastAnimatedTrackSignature;

            string titleText, artistText;
            if (hasRealTrack)
            {
                titleText = info.CurrentTrack;
                if (!string.IsNullOrEmpty(info.CurrentArtist) && info.CurrentArtist != "YouTube" && info.CurrentArtist != "Browser" && info.CurrentArtist != "Spotify")
                {
                    artistText = info.CurrentArtist;
                }
                else if (!string.IsNullOrEmpty(renderedSource))
                {
                    artistText = renderedSource;
                }
                else
                {
                    artistText = "Unknown Artist";
                }
            }
            else
            {
                titleText = "No media playing";
                artistText = "Open Spotify or YouTube";
            }

            if (isNewTrack)
            {
                UpdateTitleText(titleText);
                UpdateArtistText(artistText);
                CompactTitleMarquee.Text = titleText;
            }
            else
            {
                TrackTitle.Text = titleText;
                TrackArtist.Text = artistText;
                CompactTitleMarquee.Text = titleText;
            }

            if (hasRealTrack)
            {
                if (info.HasThumbnail && info.Thumbnail != null)
                {

                    if (isNewTrack)
                    {
                        bool isFirstEverTrack = string.IsNullOrEmpty(_lastAnimatedTrackSignature);
                        _lastAnimatedTrackSignature = trackIdentity;

                        if (isFirstEverTrack)
                        {
                            // Boot: set thumbnail then play a subtle reveal animation
                            // so the compact pill feels alive instead of popping in instantly.
                            ThumbnailImage.Source = info.Thumbnail;
                            CompactThumbnail.Source = info.Thumbnail;
                            PlayThumbnailRevealAnimation();
                        }
                        else
                        {
                            AnimateThumbnailSwitchOnly(info.Thumbnail);
                            PlayTrackChangeBounce();
                        }
                    }
                    else
                    {

                        if (ThumbnailImage.Source != info.Thumbnail)
                        {
                            // When YouTube fetch completes with the final thumbnail,
                            // play flip + bounce animation so the pill feels alive.
                            if (info.IsThumbnailOnlyUpdate && !ReferenceEquals(ThumbnailImage.Source, info.Thumbnail))
                            {
                                AnimateThumbnailSwitchOnly(info.Thumbnail);
                                PlayTrackChangeBounce();
                            }
                            else
                            {
                                ThumbnailImage.Source = info.Thumbnail;
                                CompactThumbnail.Source = info.Thumbnail;
                            }
                        }
                    }

                    ThumbnailImage.Visibility = Visibility.Visible;
                    ThumbnailFallback.Visibility = Visibility.Collapsed;

                    
                    if (isNewTrack || _lastColorTrackSignature != trackIdentity)
                    {
                        _lastColorTrackSignature = trackIdentity;
                        UpdateMediaBackground(info);
                    }
                }
                else if (isNewTrack)
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
                    _lastAnimatedTrackSignature = "";
                    _lastColorTrackSignature = "";
                    ThumbnailImage.Visibility = Visibility.Collapsed;
                    ThumbnailFallback.Visibility = Visibility.Visible;
                    HideMediaBackground();
                    ThumbnailFallback.Text = "🎵";

                    ThumbnailImage.Source = null;
                    CompactThumbnail.Source = null;
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
            // Remember the *rendered* source (after the sticky-override) so the
            // anti-flip-flop check at the top of the next update can still see
            // the "strong" source we chose last time.
            _lastRenderedMediaSource = renderedSource;
        });
    }

    private void AnimateThumbnailSwitchOnly(ImageSource newThumb)
    {
        if (_isAnimating)
        {
            // Queue the flip to run after the expand/collapse animation finishes
            _pendingFlipThumbnail = newThumb;
            return;
        }

        // Skip animation if the thumbnail is already the same object —
        // avoids a visible "snap zoom" pulse with no actual image change.
        if (newThumb != null && ReferenceEquals(ThumbnailImage.Source, newThumb))
        {
            return;
        }

        // Cancel any in-progress thumbnail switch animation to prevent stale
        // Completed handlers from resetting scale mid-animation when spamming
        // next/prev buttons rapidly.
        CancelThumbnailSwitchAnimations();

        var generation = ++_thumbnailSwitchGeneration;
        bool isHovered = _isCompactThumbnailHovered;

        var halfDur = TimeSpan.FromMilliseconds(180);
        var totalDur = TimeSpan.FromMilliseconds(360);

        var flipAnim = new DoubleAnimationUsingKeyFrames();
        flipAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flipAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(halfDur), _easeQuadIn));
        flipAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(totalDur), _easeQuadOut));
        Timeline.SetDesiredFrameRate(flipAnim, 120);

        var sourceAnim = new ObjectAnimationUsingKeyFrames();
        sourceAnim.KeyFrames.Add(new DiscreteObjectKeyFrame(newThumb, KeyTime.FromTimeSpan(halfDur)));
        Timeline.SetDesiredFrameRate(sourceAnim, 120);

        ThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, flipAnim);
        ThumbnailImage.BeginAnimation(Image.SourceProperty, sourceAnim);

        CompactThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, flipAnim);
        CompactThumbnail.BeginAnimation(Image.SourceProperty, sourceAnim);

        flipAnim.Completed += (s, e) =>
        {
            if (_thumbnailSwitchGeneration != generation) return;
            ThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailFlip.ScaleX = 1.0;
            CompactThumbnailFlip.ScaleX = 1.0;
        };

        sourceAnim.Completed += (s, e) =>
        {
            if (_thumbnailSwitchGeneration != generation) return;
            ThumbnailImage.BeginAnimation(Image.SourceProperty, null);
            CompactThumbnail.BeginAnimation(Image.SourceProperty, null);
            ThumbnailImage.Source = newThumb;
            CompactThumbnail.Source = newThumb;
        };
    }

    private void CancelThumbnailSwitchAnimations(ImageSource? targetThumb = null)
    {
        ThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, null);

        ThumbnailImage.BeginAnimation(Image.SourceProperty, null);
        CompactThumbnail.BeginAnimation(Image.SourceProperty, null);

        ThumbnailFlip.ScaleX = 1.0;
        CompactThumbnailFlip.ScaleX = 1.0;

        var resolvedThumb = targetThumb ?? _currentMediaInfo?.Thumbnail ?? ThumbnailImage.Source ?? CompactThumbnail.Source;
        if (resolvedThumb != null)
        {
            ThumbnailImage.Source = resolvedThumb;
            CompactThumbnail.Source = resolvedThumb;
        }
    }

    /// <summary>
    /// Smoothly crossfades to a new thumbnail without flip animation.
    /// Used for thumbnail-only updates (e.g., YouTube fetch completing on boot)
    /// to avoid a jarring "snap zoom" when the crop position changes.
    /// </summary>
    private void CrossfadeThumbnail(ImageSource newThumb)
    {
        if (newThumb == null) return;
        if (ReferenceEquals(ThumbnailImage.Source, newThumb)) return;

        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(100));
        Timeline.SetDesiredFrameRate(fadeOut, 60);

        fadeOut.Completed += (s, e) =>
        {
            ThumbnailImage.Source = newThumb;
            CompactThumbnail.Source = newThumb;

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150));
            Timeline.SetDesiredFrameRate(fadeIn, 60);
            CompactThumbnailBorder.BeginAnimation(OpacityProperty, fadeIn);
            ThumbnailImage.BeginAnimation(OpacityProperty, fadeIn);
        };

        CompactThumbnailBorder.BeginAnimation(OpacityProperty, fadeOut);
        ThumbnailImage.BeginAnimation(OpacityProperty, fadeOut);
    }

    #endregion

    #region Music Compact Mode

    private void UpdateMusicCompactMode(MediaInfo info)
    {
        bool shouldBeCompact = info != null && info.IsAnyMediaPlaying && !string.IsNullOrEmpty(info.CurrentTrack);

        if (info?.MediaSource == "Browser" && string.IsNullOrEmpty(info.CurrentTrack)) shouldBeCompact = false;

        _collapsedWidth = _settings.Width;

        if (shouldBeCompact == _isMusicCompactMode)
        {
            if (shouldBeCompact)
            {
                if (info?.Thumbnail != null)
                {
                    // Use track-only identity (not GetSignature) so a MediaSource
                    // flip-flop on the same track does not re-trigger the flip anim.
                    string compactTrackIdentity = $"{info.CurrentTrack}|{info.CurrentArtist}";
                    if (compactTrackIdentity != _lastAnimatedTrackSignature)
                    {
                        _lastAnimatedTrackSignature = compactTrackIdentity;
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

            var widthAnim = new DoubleAnimation(_collapsedWidth, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = _easeExpOut6
            };
            NotchBorder.BeginAnimation(WidthProperty, widthAnim);

            if (_isMusicCompactMode)
            {
                if (info?.Thumbnail != null)
                {
                    AnimateThumbnailSwitchOnly(info.Thumbnail);
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
