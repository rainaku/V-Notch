using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private readonly LyricsService _lyricsService = new();
    private readonly YouTubeSubtitleService _youtubeSubtitleService = new();
    private List<LyricLine>? _currentLyrics;
    private int _currentLyricIndex = -1;
    private string _lyricsTrackKey = "";
    private bool _isLyricsActive
    {
        get => _notchState.IsLyricsActive;
        set => _notchState.IsLyricsActive = value;
    }
    private string _lastKnownYouTubeVideoId = "";

    private enum SyncedTextSource { None, SpotifyLyrics, YouTubeSubtitles }
    private SyncedTextSource _syncedTextSource = SyncedTextSource.None;

    private async Task FetchLyricsForTrack(MediaInfo info)
    {
        if (!_settings.EnableSpotifyLyrics)
        {
            HideLyricsWidget();
            return;
        }

        string trackKey = $"{info.CurrentTrack}|{info.CurrentArtist}";

        if (trackKey == _lyricsTrackKey) return;
        _lyricsTrackKey = trackKey;
        _syncedTextSource = SyncedTextSource.SpotifyLyrics;

        if (info.Platform != MediaPlatform.Spotify)
        {
            HideLyricsWidget();
            return;
        }

        _currentLyrics = null;
        _currentLyricIndex = -1;

        if (_isLyricsActive)
        {
            Dispatcher.Invoke(() =>
            {
                LyricTextA.BeginAnimation(OpacityProperty, null);
                LyricTextB.BeginAnimation(OpacityProperty, null);
                LyricTextA.Opacity = 0;
                LyricTextB.Opacity = 0;
                LyricTextA.Text = "";
                LyricTextB.Text = "";
                ShowLyricsPlaceholder(info.CurrentTrack, info.CurrentArtist);
            });
        }

        int durationSec = (int)info.Duration.TotalSeconds;
        if (durationSec <= 0) durationSec = 240;

        var lyrics = await _lyricsService.FetchSyncedLyricsAsync(
            info.CurrentTrack, info.CurrentArtist, durationSec);

        if (trackKey != _lyricsTrackKey) return;

        ApplySyncedLines(lyrics, info);
    }

    private async Task FetchSubtitlesForTrack(MediaInfo info, bool force = false)
    {
        if (!_settings.EnableYouTubeSubtitles)
        {
            HideLyricsWidget();
            return;
        }

        if (string.IsNullOrEmpty(info.YouTubeVideoId))
            return;

        _lastKnownYouTubeVideoId = info.YouTubeVideoId;

        string trackKey = $"yt:{info.YouTubeVideoId}";
        if (!force && trackKey == _lyricsTrackKey) return;
        _lyricsTrackKey = trackKey;
        _syncedTextSource = SyncedTextSource.YouTubeSubtitles;

        _currentLyrics = null;
        _currentLyricIndex = -1;

        if (_isLyricsActive)
        {
            Dispatcher.Invoke(() =>
            {
                LyricTextA.BeginAnimation(OpacityProperty, null);
                LyricTextB.BeginAnimation(OpacityProperty, null);
                LyricTextA.Opacity = 0;
                LyricTextB.Opacity = 0;
                LyricTextA.Text = "";
                LyricTextB.Text = "";
                ShowLyricsPlaceholder(info.CurrentArtist, "");
            });
        }

        var subtitles = await _youtubeSubtitleService.FetchSubtitlesAsync(info.YouTubeVideoId, force: force);

        if (trackKey != _lyricsTrackKey) return;

        ApplySyncedLines(subtitles, info, isYouTube: true);
    }

    private void ApplySyncedLines(List<LyricLine>? lines, MediaInfo info, bool isYouTube = false)
    {
        if (lines == null || lines.Count == 0)
        {
            _lyricsTrackKey = "";
            if (_isLyricsActive)
                HideLyricsWidget();
            return;
        }

        _currentLyrics = lines;
        _currentLyricIndex = -1;

        if (!_isLyricsActive)
        {
            ShowLyricsWidget();
        }

        Dispatcher.Invoke(() =>
        {
            int idx = FindCurrentLyricIndex();
            if (idx >= 0)
            {
                _currentLyricIndex = idx;
                HideLyricsPlaceholder();
                AnimateLyricLine(_currentLyrics[idx].Text);
            }
            else if (LyricsPlaceholderPanel.Visibility != Visibility.Visible || LyricsPlaceholderPanel.Opacity < 0.01)
            {
                string placeholderTitle = isYouTube ? info.CurrentArtist : info.CurrentTrack;
                string placeholderArtist = isYouTube ? "" : info.CurrentArtist;
                ShowLyricsPlaceholder(placeholderTitle, placeholderArtist);
            }
        });
    }

    private int FindCurrentLyricIndex()
    {
        if (_currentLyrics == null || _currentLyrics.Count == 0) return -1;
        if (_currentMediaInfo == null) return -1;

        TimeSpan position;
        if (_currentMediaInfo.IsPlaying && _currentMediaInfo.Duration.TotalSeconds > 0)
        {
            var frame = _progressEngine.GetUiFrame();
            if (frame.Duration.TotalSeconds > 0 && frame.State == ProgressState.Playing)
            {
                position = frame.Position;
            }
            else
            {
                var elapsed = DateTimeOffset.UtcNow - _currentMediaInfo.LastUpdated.ToUniversalTime();
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed > TimeSpan.FromMinutes(10)) elapsed = TimeSpan.FromMinutes(10);
                position = _currentMediaInfo.Position + elapsed;
            }
        }
        else
        {
            position = _currentMediaInfo.Position;
        }

        return FindLyricIndex(position);
    }

    private void ShowLyricsPlaceholder(string title, string artist)
    {
        if (LyricsPlaceholderPanel == null) return;

        LyricsPlaceholderTitle.Text = title;
        LyricsPlaceholderArtist.Text = artist;
        LyricsPlaceholderPanel.Visibility = Visibility.Visible;

        LyricTextA.BeginAnimation(OpacityProperty, null);
        LyricTextB.BeginAnimation(OpacityProperty, null);
        LyricTextA.Opacity = 0;
        LyricTextB.Opacity = 0;

        var dur = new Duration(TimeSpan.FromMilliseconds(350));
        var ease = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        var slideIn = new DoubleAnimation(6, 0, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideIn, VNotch.Services.AnimationConfig.TargetFps);

        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, fadeIn);
        LyricsPlaceholderTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void HideLyricsPlaceholder()
    {
        if (LyricsPlaceholderPanel == null || LyricsPlaceholderPanel.Opacity < 0.01) return;

        var dur = new Duration(TimeSpan.FromMilliseconds(300));
        var ease = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn };

        var fadeOut = new DoubleAnimation(LyricsPlaceholderPanel.Opacity, 0, dur) { EasingFunction = ease };
        var slideOut = new DoubleAnimation(0, -6, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fadeOut, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideOut, VNotch.Services.AnimationConfig.TargetFps);

        fadeOut.Completed += (s, e) =>
        {
            LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;
            LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
            LyricsPlaceholderPanel.Opacity = 0;
        };

        LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, fadeOut);
        LyricsPlaceholderTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }

    private void UpdateLyricsDisplay()
    {
        if (!_isLyricsActive || _currentLyrics == null || _currentLyrics.Count == 0)
            return;

        TimeSpan position;
        if (_currentMediaInfo != null && _currentMediaInfo.IsPlaying && _currentMediaInfo.Duration.TotalSeconds > 0)
        {
            var frame = _progressEngine.GetUiFrame();
            if (frame.Duration.TotalSeconds > 0 && frame.State == ProgressState.Playing)
            {
                position = frame.Position;
            }
            else
            {
                var elapsed = DateTimeOffset.UtcNow - _currentMediaInfo.LastUpdated.ToUniversalTime();
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed > TimeSpan.FromMinutes(10)) elapsed = TimeSpan.FromMinutes(10);
                position = _currentMediaInfo.Position + elapsed;
            }
            if (position > _currentMediaInfo.Duration) position = _currentMediaInfo.Duration;
        }
        else if (_currentMediaInfo != null)
        {
            position = _currentMediaInfo.Position;
        }
        else
        {
            var frame = _progressEngine.GetUiFrame();
            if (frame.Duration.TotalSeconds <= 0) return;
            position = frame.Position;
        }

        int newIndex = FindLyricIndex(position);

        if (newIndex != _currentLyricIndex && newIndex >= 0)
        {
            if (_currentLyricIndex < 0)
                HideLyricsPlaceholder();

            _currentLyricIndex = newIndex;
            string lineText = _currentLyrics[newIndex].Text;
            AnimateLyricLine(lineText);
        }
        else if (newIndex < 0 && _currentLyricIndex >= 0)
        {
            _currentLyricIndex = -1;
            if (_syncedTextSource == SyncedTextSource.YouTubeSubtitles && _currentMediaInfo != null)
            {
                ShowLyricsPlaceholder(_currentMediaInfo.CurrentArtist, "");
            }
            else
            {
                string trackKey = _lyricsTrackKey;
                string[] parts = trackKey.Split('|', 2);
                if (parts.Length == 2)
                    ShowLyricsPlaceholder(parts[0], parts[1]);
            }
        }
    }

    private int FindLyricIndex(TimeSpan position)
    {
        if (_currentLyrics == null || _currentLyrics.Count == 0) return -1;

        if (position < _currentLyrics[0].Time) return -1;

        int lo = 0, hi = _currentLyrics.Count - 1;
        int result = 0;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_currentLyrics[mid].Time <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    private void AnimateLyricLine(string newText)
    {
        if (LyricTextA == null || LyricTextB == null) return;

        bool useA = LyricTextA.Opacity > 0.5;
        TextBlock outgoing = useA ? LyricTextA : LyricTextB;
        TextBlock incoming = useA ? LyricTextB : LyricTextA;
        TranslateTransform outTransform = useA ? LyricTranslateA : LyricTranslateB;
        TranslateTransform inTransform = useA ? LyricTranslateB : LyricTranslateA;

        incoming.Text = newText;

        outgoing.BeginAnimation(OpacityProperty, null);
        outTransform.BeginAnimation(TranslateTransform.YProperty, null);
        incoming.BeginAnimation(OpacityProperty, null);
        inTransform.BeginAnimation(TranslateTransform.YProperty, null);

        outgoing.Opacity = 1;
        outTransform.Y = 0;
        incoming.Opacity = 0;
        inTransform.Y = 14;

        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var outDur = new Duration(TimeSpan.FromMilliseconds(300));
        var inDur = new Duration(TimeSpan.FromMilliseconds(450));
        var inDelay = TimeSpan.FromMilliseconds(80);
        var easeOut = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fadeOut = new DoubleAnimation(1, 0, outDur) { EasingFunction = easeIn };
        var slideOut = new DoubleAnimation(0, -14, outDur) { EasingFunction = easeIn };
        Timeline.SetDesiredFrameRate(fadeOut, fps);
        Timeline.SetDesiredFrameRate(slideOut, fps);

        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        outTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);

        var fadeIn = new DoubleAnimation(0, 1, inDur) { EasingFunction = easeOut, BeginTime = inDelay };
        var slideIn = new DoubleAnimation(14, 0, inDur) { EasingFunction = easeOut, BeginTime = inDelay };
        Timeline.SetDesiredFrameRate(fadeIn, fps);
        Timeline.SetDesiredFrameRate(slideIn, fps);

        incoming.BeginAnimation(OpacityProperty, fadeIn);
        inTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void ShowLyricsWidget()
    {
        if (_isLyricsActive) return;
        _isLyricsActive = true;

        Dispatcher.Invoke(() =>
        {
            var fadeOutCalendar = new DoubleAnimation(CalendarWidget.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
            };
            fadeOutCalendar.Completed += (s, e) =>
            {
                CalendarWidget.Visibility = Visibility.Collapsed;
                CalendarWidget.BeginAnimation(OpacityProperty, null);
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutCalendar, VNotch.Services.AnimationConfig.TargetFps);
            CalendarWidget.BeginAnimation(OpacityProperty, fadeOutCalendar);

            var fadeOutGreeting = new DoubleAnimation(GreetingSection.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
            };
            fadeOutGreeting.Completed += (s, e) =>
            {
                GreetingSection.Visibility = Visibility.Collapsed;
                GreetingSection.BeginAnimation(OpacityProperty, null);
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutGreeting, VNotch.Services.AnimationConfig.TargetFps);
            GreetingSection.BeginAnimation(OpacityProperty, fadeOutGreeting);

            LyricsWidget.BeginAnimation(OpacityProperty, null);
            LyricsWidget.Visibility = Visibility.Visible;
            LyricsWidget.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(100)
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeIn, VNotch.Services.AnimationConfig.TargetFps);
            LyricsWidget.BeginAnimation(OpacityProperty, fadeIn);

            if (_settings.EnableBlurEffects && !IsLiquidGlassEnabled && LyricsBlurBackground != null)
            {
                LyricsBlurImage.BeginAnimation(OpacityProperty, null);
                LyricsBlurImage.Opacity = 1;
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                LyricsBlurBackground.Visibility = Visibility.Visible;
                LyricsBlurBackground.Opacity = 0.55;
            }
            else if (LyricsBlurBackground != null)
            {
                LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                LyricsBlurBackground.Opacity = 0;
                LyricsBlurBackground.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void HideLyricsWidget()
    {
        if (!_isLyricsActive) return;
        _isLyricsActive = false;
        _currentLyrics = null;
        _currentLyricIndex = -1;

        Dispatcher.Invoke(() =>
        {
            var fadeOutLyrics = new DoubleAnimation(LyricsWidget.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(300)))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
            };
            fadeOutLyrics.Completed += (s, e) =>
            {
                if (_isLyricsActive) return;

                LyricsWidget.Visibility = Visibility.Collapsed;
                LyricsWidget.BeginAnimation(OpacityProperty, null);
                LyricsWidget.Opacity = 0;

                if (LyricTextA != null) LyricTextA.Text = "";
                if (LyricTextB != null) LyricTextB.Text = "";
                if (LyricsPlaceholderPanel != null)
                {
                    LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;
                    LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
                    LyricsPlaceholderPanel.Opacity = 0;
                }
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutLyrics, VNotch.Services.AnimationConfig.TargetFps);
            LyricsWidget.BeginAnimation(OpacityProperty, fadeOutLyrics);

            if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
            {
                var fadeOutBlur = new DoubleAnimation(LyricsBlurBackground.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(400)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
                };
                fadeOutBlur.Completed += (s, e) =>
                {
                    if (_isLyricsActive) return;
                    LyricsBlurBackground.Visibility = Visibility.Collapsed;
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeOutBlur, VNotch.Services.AnimationConfig.TargetFps);
                LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeOutBlur);
            }

            CalendarWidget.Visibility = Visibility.Visible;
            CalendarWidget.Opacity = 0;
            var fadeInCalendar = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(150)
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInCalendar, VNotch.Services.AnimationConfig.TargetFps);
            CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);

            if (!IsNonCalendarWidgetMode && !_isLyricsActive)
            {
                GreetingSection.Visibility = Visibility.Visible;
                GreetingSection.Opacity = 0;
                var fadeInGreeting = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                    BeginTime = TimeSpan.FromMilliseconds(150)
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeInGreeting, VNotch.Services.AnimationConfig.TargetFps);
                GreetingSection.BeginAnimation(OpacityProperty, fadeInGreeting);
            }
            else
            {
                GreetingSection.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void ClearLyrics()
    {
        _lyricsTrackKey = "";
        _syncedTextSource = SyncedTextSource.None;
        _lyricsService.Reset();
        _youtubeSubtitleService.Reset();
        HideLyricsWidget();
    }
}
