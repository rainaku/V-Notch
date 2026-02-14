using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// Partial class for Media detection handling, background color extraction, and visualizer
/// </summary>
public partial class MainWindow
{
    private string _lastAnimatedTrackSignature = "";
    private DateTime _lastAnimationStartTime = DateTime.MinValue;

    private static readonly string[] _genericTitles = { "Spotify", "Spotify Premium", "Spotify Free", "YouTube", "SoundCloud", "Browser" };

    #region Media Changed Handler

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        _currentMediaInfo = info;
        _lastMediaUpdate = DateTime.Now;
        
        Dispatcher.BeginInvoke(() =>
        {
            // Update Platform Icons
            SpotifyIcon.Visibility = Visibility.Collapsed;
            YouTubeIcon.Visibility = Visibility.Collapsed;
            SoundCloudIcon.Visibility = Visibility.Collapsed;
            FacebookIcon.Visibility = Visibility.Collapsed;
            TikTokIcon.Visibility = Visibility.Collapsed;
            InstagramIcon.Visibility = Visibility.Collapsed;
            TwitterIcon.Visibility = Visibility.Collapsed;
            AppleMusicIcon.Visibility = Visibility.Collapsed;
            BrowserIcon.Visibility = Visibility.Collapsed;
            
            bool hasRealTrack = !string.IsNullOrEmpty(info.CurrentTrack);

            // Only show icon if we have a source
            if (hasRealTrack && !string.IsNullOrEmpty(info.MediaSource))
            {
                switch (info.MediaSource)
                {
                    case "Spotify": SpotifyIcon.Visibility = Visibility.Visible; break;
                    case "YouTube": YouTubeIcon.Visibility = Visibility.Visible; break;
                    case "SoundCloud": SoundCloudIcon.Visibility = Visibility.Visible; break;
                    case "Facebook": FacebookIcon.Visibility = Visibility.Visible; break;
                    case "TikTok": TikTokIcon.Visibility = Visibility.Visible; break;
                    case "Instagram": InstagramIcon.Visibility = Visibility.Visible; break;
                    case "Twitter": case "X": TwitterIcon.Visibility = Visibility.Visible; break;
                    case "Apple Music": AppleMusicIcon.Visibility = Visibility.Visible; break;
                    default: BrowserIcon.Visibility = Visibility.Visible; break;
                }
            }

            string currentSig = info.GetSignature();
            bool isNewTrack = currentSig != _lastAnimatedTrackSignature;

            // Prepare text
            string titleText, artistText;
            if (hasRealTrack)
            {
                MediaAppName.Text = info.MediaSource;
                titleText = info.CurrentTrack;
                if (!string.IsNullOrEmpty(info.CurrentArtist) && info.CurrentArtist != "YouTube" && info.CurrentArtist != "Browser" && info.CurrentArtist != "Spotify")
                {
                    artistText = info.CurrentArtist;
                }
                else if (!string.IsNullOrEmpty(info.MediaSource))
                {
                    artistText = info.MediaSource;
                }
                else
                {
                    artistText = "Unknown Artist";
                }
                
                // Final sync: if it's YouTube, make sure we show the source correctly alongside the artist
                MediaAppName.Text = info.MediaSource;
            }
            else
            {
                titleText = "No media playing";
                artistText = "Open Spotify or YouTube";
                MediaAppName.Text = "Now Playing";
            }

            // 1. Update text immediately for responsiveness
            if (isNewTrack)
            {
                UpdateTitleText(titleText);
                UpdateArtistText(artistText);
            }
            else
            {
                // If same track, just update text properties directly to avoid re-triggering Slide animations
                TrackTitle.Text = titleText;
                TrackArtist.Text = artistText;
            }

            // 2. Update thumbnail logic with persistence (avoids black flicker during skip)
            if (hasRealTrack)
            {
                if (info.HasThumbnail && info.Thumbnail != null)
                {
                    // Check if we already animated THIS track
                    if (isNewTrack)
                    {
                        // TRIGGER FLIP ONLY NOW
                        _lastAnimatedTrackSignature = currentSig;
                        AnimateThumbnailSwitchOnly(info.Thumbnail);
                    }
                    else
                    {
                        // Direct update for resolution changes or same track re-sync
                        if (ThumbnailImage.Source != info.Thumbnail)
                        {
                            ThumbnailImage.Source = info.Thumbnail;
                            CompactThumbnail.Source = info.Thumbnail;
                        }
                    }
                    
                    ThumbnailImage.Visibility = Visibility.Visible;
                    ThumbnailFallback.Visibility = Visibility.Collapsed;
                    UpdateMediaBackground(info);
                }
                else if (isNewTrack)
                {
                    // Track changed but no thumb yet - DO NOT update _lastAnimatedTrackSignature yet.
                    // This ensures we will trigger the flip as soon as the thumbnail arrives.
                    
                    // Keep old thumb visible or show fallback if absolutely no thumb
                    if (ThumbnailImage.Source == null)
                    {
                        ThumbnailImage.Visibility = Visibility.Collapsed;
                        ThumbnailFallback.Visibility = Visibility.Visible;
                        ThumbnailFallback.Text = "🎵";
                    }
                    else
                    {
                        ThumbnailImage.Visibility = Visibility.Visible;
                        ThumbnailFallback.Visibility = Visibility.Collapsed;
                    }
                }
            }
            else
            {
                // No media playing - reset state
                _lastAnimatedTrackSignature = "";
                ThumbnailImage.Visibility = Visibility.Collapsed;
                ThumbnailFallback.Visibility = Visibility.Visible;
                HideMediaBackground();
                ThumbnailFallback.Text = "🎵";
                
                // Clear sources to ensure clean state
                ThumbnailImage.Source = null;
                CompactThumbnail.Source = null;
            }

            // Sync Play/Pause state
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds > 500 && _isPlaying != info.IsPlaying)
            {
                _isPlaying = info.IsPlaying;
                UpdatePlayPauseIcon();
            }
            
            if (info.MediaSource == "YouTube" && hasRealTrack)
            {
                YouTubeVideoModeButton.Visibility = Visibility.Visible;
                if (isNewTrack && !string.IsNullOrEmpty(info.YouTubeVideoId))
                {
                    YouTubePlayer.LoadVideo(info.YouTubeVideoId);
                }
            }
            else
            {
                YouTubeVideoModeButton.Visibility = Visibility.Collapsed;
                // If we're not on YouTube, hide player
                if (_isYouTubeVideoMode)
                {
                    _isYouTubeVideoMode = false;
                    YouTubePlayerContainer.Visibility = Visibility.Collapsed;
                    MediaWidgetContainer.Visibility = Visibility.Visible;
                }
            }

            UpdateProgressTracking(info);
            UpdateMusicCompactMode(info);
        });
    }

    private void AnimateThumbnailSwitchOnly(ImageSource newThumb)
    {
        // Snappy but smooth flip: 180ms per half (360ms total)
        var halfDur = TimeSpan.FromMilliseconds(180);
        var totalDur = TimeSpan.FromMilliseconds(360);

        // 1. ScaleX Animation (1.0 -> 0.0 -> 1.0)
        // This creates the 'flip' effect by shrinking to zero then expanding back.
        var flipAnim = new DoubleAnimationUsingKeyFrames();
        flipAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flipAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(halfDur), _easeQuadIn));
        flipAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(totalDur), _easeQuadOut));
        Timeline.SetDesiredFrameRate(flipAnim, 120);

        // 2. Pulse Animation (1.0 -> 1.08 -> 1.0)
        // Adds a subtle 'pop' effect during the flip for better visual feedback.
        var pulseAnim = new DoubleAnimationUsingKeyFrames();
        pulseAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulseAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(halfDur), _easeQuadOut));
        pulseAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(totalDur), _easeQuadIn));
        Timeline.SetDesiredFrameRate(pulseAnim, 120);

        // 3. Source Swap exactly at the 0-scale mark
        // DiscreteObjectKeyFrame ensures the image changes perfectly when it's invisible (at width 0).
        var sourceAnim = new ObjectAnimationUsingKeyFrames();
        sourceAnim.KeyFrames.Add(new DiscreteObjectKeyFrame(newThumb, KeyTime.FromTimeSpan(halfDur)));
        Timeline.SetDesiredFrameRate(sourceAnim, 120);

        // Apply animations to all relevant transforms and images simultaneously for perfect sync
        
        // Expanded Mode Targets
        ThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, flipAnim);
        ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnim);
        ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnim);
        ThumbnailImage.BeginAnimation(Image.SourceProperty, sourceAnim);

        // Compact Mode Targets
        CompactThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, flipAnim);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnim); 
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnim);
        CompactThumbnail.BeginAnimation(Image.SourceProperty, sourceAnim);

        // Setup completion to clear animations and set base values
        flipAnim.Completed += (s, e) =>
        {
            ThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailFlip.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailFlip.ScaleX = 1.0;
            CompactThumbnailFlip.ScaleX = 1.0;
        };

        pulseAnim.Completed += (s, e) =>
        {
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ThumbnailScale.ScaleX = 1.0;
            ThumbnailScale.ScaleY = 1.0;
            CompactThumbnailScale.ScaleX = 1.0;
            CompactThumbnailScale.ScaleY = 1.0;
        };

        sourceAnim.Completed += (s, e) =>
        {
            ThumbnailImage.BeginAnimation(Image.SourceProperty, null);
            CompactThumbnail.BeginAnimation(Image.SourceProperty, null);
            ThumbnailImage.Source = newThumb;
            CompactThumbnail.Source = newThumb;
        };
    }

    #endregion

    #region Music Compact Mode

    private void UpdateMusicCompactMode(MediaInfo info)
    {
        bool shouldBeCompact = info != null && info.IsAnyMediaPlaying && !string.IsNullOrEmpty(info.CurrentTrack);
        
        // Ensure we don't calculate layout if it's generic browser info with no real track
        if (info?.MediaSource == "Browser" && string.IsNullOrEmpty(info.CurrentTrack)) shouldBeCompact = false;

        _collapsedWidth = shouldBeCompact ? 180 : _settings.Width;
        
        if (shouldBeCompact == _isMusicCompactMode) 
        {
            if (shouldBeCompact && info?.Thumbnail != null)
            {
                string currentSig = info.GetSignature();
                if (currentSig != _lastAnimatedTrackSignature)
                {
                    _lastAnimatedTrackSignature = currentSig;
                    AnimateThumbnailSwitchOnly(info.Thumbnail);
                }
            }
            return;
        }

        _isMusicCompactMode = shouldBeCompact;

        if (!_isExpanded)
        {
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
                StartVisualizerAnimation();
            }
            else
            {
                FadeSwitch(MusicCompactContent, CollapsedContent);
                StopVisualizerAnimation();
            }
        }
        else
        {
            if (_isMusicCompactMode)
            {
                if (info?.Thumbnail != null) 
                {
                    AnimateThumbnailSwitchOnly(info.Thumbnail);
                }
                StartVisualizerAnimation();
            }
            else
            {
                StopVisualizerAnimation();
            }
            
            // Always ensure compact layers are hidden when expanded
            MusicCompactContent.Visibility = Visibility.Collapsed;
            MusicCompactContent.Opacity = 0;
            CollapsedContent.Visibility = Visibility.Collapsed;
            CollapsedContent.Opacity = 0;
        }
    }

    #endregion

    #region Visualizer

    private void StartVisualizerAnimation()
    {
        AnimateVizBar(VizBar1, 0.4, 1.3, 0.45);
        AnimateVizBar(VizBar2, 0.3, 1.6, 0.55);
        AnimateVizBar(VizBar3, 0.5, 1.2, 0.35);
        AnimateVizBar(VizBar4, 0.2, 1.5, 0.65);
    }

    private void AnimateVizBar(ScaleTransform bar, double from, double to, double durationSec)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(durationSec))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = _easeSineInOut
        };
        bar.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void StopVisualizerAnimation()
    {
        VizBar1.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        VizBar2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        VizBar3.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        VizBar4.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    #endregion

    #region Thumbnail Transition


    #endregion
}
