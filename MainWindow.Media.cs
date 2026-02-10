using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// Partial class for Media detection handling, background color extraction, and visualizer
/// </summary>
public partial class MainWindow
{
    #region Media Changed Handler

    private void OnMediaChanged(object? sender, MediaInfo info)
    {
        _currentMediaInfo = info;
        
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
            BrowserIcon.Visibility = Visibility.Collapsed;

            switch (info.MediaSource)
            {
                case "Spotify": SpotifyIcon.Visibility = Visibility.Visible; break;
                case "YouTube": YouTubeIcon.Visibility = Visibility.Visible; break;
                case "SoundCloud": SoundCloudIcon.Visibility = Visibility.Visible; break;
                case "Facebook": FacebookIcon.Visibility = Visibility.Visible; break;
                case "TikTok": TikTokIcon.Visibility = Visibility.Visible; break;
                case "Instagram": InstagramIcon.Visibility = Visibility.Visible; break;
                case "Twitter": case "X": TwitterIcon.Visibility = Visibility.Visible; break;
                default: BrowserIcon.Visibility = Visibility.Visible; break;
            }

            // Update thumbnail
            if (info.HasThumbnail && info.Thumbnail != null)
            {
                ThumbnailImage.Source = info.Thumbnail;
                ThumbnailImage.Visibility = Visibility.Visible;
                ThumbnailFallback.Visibility = Visibility.Collapsed;
                UpdateMediaBackground(info);
            }
            else
            {
                ThumbnailImage.Visibility = Visibility.Collapsed;
                ThumbnailFallback.Visibility = Visibility.Visible;
                HideMediaBackground();
                ThumbnailFallback.Text = info.MediaSource switch
                {
                    "Spotify" => "🎵", "YouTube" => "▶", "SoundCloud" => "☁",
                    "TikTok" => "♪", "Facebook" => "📺", "Instagram" => "📷",
                    "Twitter" => "🐦", "Browser" => "🌐", _ => "🎵"
                };
            }

            // Sync Play/Pause state
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds > 500 && _isPlaying != info.IsPlaying)
            {
                _isPlaying = info.IsPlaying;
                UpdatePlayPauseIcon();
            }

            // Update text
            string titleText;
            string artistText;
            
            if (info.IsAnyMediaPlaying && !string.IsNullOrEmpty(info.CurrentTrack))
            {
                MediaAppName.Text = info.MediaSource;
                titleText = string.IsNullOrEmpty(info.CurrentTrack) ? "Playing..." : info.CurrentTrack;
                if (info.MediaSource == "Browser" && string.IsNullOrEmpty(info.CurrentArtist))
                    artistText = "Playing in browser";
                else
                    artistText = string.IsNullOrEmpty(info.CurrentArtist) ? info.MediaSource : info.CurrentArtist;
            }
            else if (info.IsSpotifyPlaying)
            {
                MediaAppName.Text = "Spotify";
                titleText = info.CurrentTrack;
                artistText = info.CurrentArtist;
            }
            else if (info.IsYouTubeRunning)
            {
                MediaAppName.Text = "YouTube";
                titleText = info.YouTubeTitle;
                artistText = "Playing in browser";
            }
            else if (info.IsSoundCloudRunning)
            {
                MediaAppName.Text = "SoundCloud";
                titleText = info.CurrentTrack;
                artistText = "Playing";
            }
            else if (info.IsTikTokRunning)
            {
                MediaAppName.Text = "TikTok";
                titleText = info.CurrentTrack;
                artistText = "Playing";
            }
            else if (info.IsFacebookRunning)
            {
                MediaAppName.Text = "Facebook";
                titleText = info.CurrentTrack;
                artistText = "Video";
            }
            else if (info.MediaSource == "Browser")
            {
                MediaAppName.Text = "Browser";
                titleText = !string.IsNullOrEmpty(info.CurrentTrack) ? info.CurrentTrack : "Playing...";
                artistText = !string.IsNullOrEmpty(info.CurrentArtist) ? info.CurrentArtist : "Playing in browser";
            }
            else
            {
                MediaAppName.Text = "Now Playing";
                titleText = "No media playing";
                artistText = "Open Spotify or YouTube";
            }
            
            UpdateTitleText(titleText);
            UpdateArtistText(artistText);
            UpdateProgressTracking(info);
            UpdateMusicCompactMode(info);
        });
    }

    #endregion

    #region Music Compact Mode

    private void UpdateMusicCompactMode(MediaInfo info)
    {
        bool shouldBeCompact = info != null && info.IsPlaying;
        _collapsedWidth = shouldBeCompact ? 180 : _settings.Width;
        
        if (shouldBeCompact == _isMusicCompactMode) 
        {
            if (shouldBeCompact && info?.Thumbnail != null) CompactThumbnail.Source = info.Thumbnail;
            return;
        }

        _isMusicCompactMode = shouldBeCompact;

        if (!_isExpanded)
        {
            var widthAnim = new DoubleAnimation(_collapsedWidth, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
            };
            NotchBorder.BeginAnimation(WidthProperty, widthAnim);
            
            if (_isMusicCompactMode)
            {
                if (info?.Thumbnail != null) CompactThumbnail.Source = info.Thumbnail;
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
                if (info?.Thumbnail != null) CompactThumbnail.Source = info.Thumbnail;
                StartVisualizerAnimation();
                MusicCompactContent.Opacity = 0; 
                CollapsedContent.Opacity = 0;
            }
            else
            {
                StopVisualizerAnimation();
            }
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
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
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
}
