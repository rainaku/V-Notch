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
    private List<LyricLine>? _currentLyrics;
    private int _currentLyricIndex = -1;
    private string _lyricsTrackKey = "";
    private bool _isLyricsActive = false;

    private async void FetchLyricsForTrack(MediaInfo info)
    {
        string trackKey = $"{info.CurrentTrack}|{info.CurrentArtist}";

        // Don't re-fetch for the same track
        if (trackKey == _lyricsTrackKey) return;
        _lyricsTrackKey = trackKey;

        // Only support Spotify
        if (info.MediaSource != "Spotify")
        {
            HideLyricsWidget();
            return;
        }

        // Immediately clear old lyrics and show new track placeholder
        _currentLyrics = null;
        _currentLyricIndex = -1;
        if (_isLyricsActive)
        {
            Dispatcher.Invoke(() =>
            {
                // Clear old lyric text immediately
                LyricTextA.BeginAnimation(OpacityProperty, null);
                LyricTextB.BeginAnimation(OpacityProperty, null);
                LyricTextA.Opacity = 0;
                LyricTextB.Opacity = 0;
                LyricTextA.Text = "";
                LyricTextB.Text = "";

                // Update placeholder text directly (no animation) to avoid flicker on metadata refinement
                if (LyricsPlaceholderPanel != null && LyricsPlaceholderPanel.Opacity > 0.5)
                {
                    LyricsPlaceholderTitle.Text = info.CurrentTrack;
                    LyricsPlaceholderArtist.Text = info.CurrentArtist;
                }
                else
                {
                    ShowLyricsPlaceholder(info.CurrentTrack, info.CurrentArtist);
                }
            });
        }

        int durationSec = (int)info.Duration.TotalSeconds;
        // If duration is 0 (not yet known), use a reasonable default
        if (durationSec <= 0) durationSec = 240;

        var lyrics = await _lyricsService.FetchSyncedLyricsAsync(
            info.CurrentTrack, info.CurrentArtist, durationSec);

        // Verify we're still on the same track after async
        if (trackKey != _lyricsTrackKey) return;

        if (lyrics == null || lyrics.Count == 0)
        {
            HideLyricsWidget();
            return;
        }

        _currentLyrics = lyrics;
        _currentLyricIndex = -1;

        if (!_isLyricsActive)
        {
            ShowLyricsWidget();
            // Show track info placeholder until first lyric line
            Dispatcher.Invoke(() => ShowLyricsPlaceholder(info.CurrentTrack, info.CurrentArtist));
        }
    }

    private void ShowLyricsPlaceholder(string title, string artist)
    {
        if (LyricsPlaceholderPanel == null) return;

        LyricsPlaceholderTitle.Text = title;
        LyricsPlaceholderArtist.Text = artist;
        LyricsPlaceholderPanel.Visibility = Visibility.Visible;

        // Hide lyric text lines
        LyricTextA.Opacity = 0;
        LyricTextB.Opacity = 0;

        var dur = new Duration(TimeSpan.FromMilliseconds(350));
        var ease = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        var slideIn = new DoubleAnimation(6, 0, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fadeIn, 120);
        Timeline.SetDesiredFrameRate(slideIn, 120);

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
        Timeline.SetDesiredFrameRate(fadeOut, 120);
        Timeline.SetDesiredFrameRate(slideOut, 120);

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

        var frame = _progressEngine.GetUiFrame();
        if (frame.Duration.TotalSeconds <= 0) return;

        var position = frame.Position;
        int newIndex = FindLyricIndex(position);

        if (newIndex != _currentLyricIndex && newIndex >= 0)
        {
            // Hide placeholder when first real lyric appears
            if (_currentLyricIndex < 0)
                HideLyricsPlaceholder();

            _currentLyricIndex = newIndex;
            string lineText = _currentLyrics[newIndex].Text;
            AnimateLyricLine(lineText);
        }
        else if (newIndex < 0 && _currentLyricIndex >= 0)
        {
            // Seeked back before first lyric — show placeholder again
            _currentLyricIndex = -1;
            string trackKey = _lyricsTrackKey;
            string[] parts = trackKey.Split('|', 2);
            if (parts.Length == 2)
                ShowLyricsPlaceholder(parts[0], parts[1]);
        }
    }

    private int FindLyricIndex(TimeSpan position)
    {
        if (_currentLyrics == null || _currentLyrics.Count == 0) return -1;

        // If before the first line
        if (position < _currentLyrics[0].Time) return -1;

        // Binary search for the last line whose time <= position
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

        // Determine which TextBlock is currently active
        bool useA = LyricTextA.Opacity > 0.5;
        TextBlock outgoing = useA ? LyricTextA : LyricTextB;
        TextBlock incoming = useA ? LyricTextB : LyricTextA;
        TranslateTransform outTransform = useA ? LyricTranslateA : LyricTranslateB;
        TranslateTransform inTransform = useA ? LyricTranslateB : LyricTranslateA;

        incoming.Text = newText;

        var dur = new Duration(TimeSpan.FromMilliseconds(400));
        var easeOut = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };

        // Fade out + slide up outgoing
        var fadeOut = new DoubleAnimation(1, 0, dur) { EasingFunction = easeOut };
        var slideOut = new DoubleAnimation(0, -6, dur) { EasingFunction = easeOut };
        Timeline.SetDesiredFrameRate(fadeOut, 120);
        Timeline.SetDesiredFrameRate(slideOut, 120);

        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        outTransform.BeginAnimation(TranslateTransform.YProperty, slideOut);

        // Fade in + slide up incoming
        var fadeIn = new DoubleAnimation(0, 1, dur) { EasingFunction = easeOut };
        var slideIn = new DoubleAnimation(6, 0, dur) { EasingFunction = easeOut };
        Timeline.SetDesiredFrameRate(fadeIn, 120);
        Timeline.SetDesiredFrameRate(slideIn, 120);

        incoming.BeginAnimation(OpacityProperty, fadeIn);
        inTransform.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    private void ShowLyricsWidget()
    {
        if (_isLyricsActive) return;
        _isLyricsActive = true;

        // Hide calendar with fade, show lyrics
        Dispatcher.Invoke(() =>
        {
            // Fade out calendar
            var fadeOutCalendar = new DoubleAnimation(CalendarWidget.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
            };
            fadeOutCalendar.Completed += (s, e) =>
            {
                CalendarWidget.Visibility = Visibility.Collapsed;
                CalendarWidget.BeginAnimation(OpacityProperty, null);
            };
            CalendarWidget.BeginAnimation(OpacityProperty, fadeOutCalendar);

            // Fade out greeting
            var fadeOutGreeting = new DoubleAnimation(GreetingSection.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
            };
            fadeOutGreeting.Completed += (s, e) =>
            {
                GreetingSection.Visibility = Visibility.Collapsed;
                GreetingSection.BeginAnimation(OpacityProperty, null);
            };
            GreetingSection.BeginAnimation(OpacityProperty, fadeOutGreeting);

            // Fade in lyrics (delayed slightly so calendar fades first)
            LyricsWidget.Visibility = Visibility.Visible;
            LyricsWidget.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(100)
            };
            LyricsWidget.BeginAnimation(OpacityProperty, fadeIn);

            // Show lyrics blur background (blurred thumbnail glow)
            if (LyricsBlurBackground != null)
            {
                LyricsBlurImage.Source = ThumbnailImage.Source;
                LyricsBlurBackground.Visibility = Visibility.Visible;
                var fadeInBlur = new DoubleAnimation(0, 0.55, new Duration(TimeSpan.FromMilliseconds(500)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut },
                    BeginTime = TimeSpan.FromMilliseconds(150)
                };
                LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeInBlur);
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
            // Fade out lyrics
            var fadeOutLyrics = new DoubleAnimation(LyricsWidget.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(300)))
            {
                EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
            };
            fadeOutLyrics.Completed += (s, e) =>
            {
                LyricsWidget.Visibility = Visibility.Collapsed;
                LyricsWidget.BeginAnimation(OpacityProperty, null);
                LyricsWidget.Opacity = 0;

                // Reset lyric text and placeholder
                if (LyricTextA != null) LyricTextA.Text = "";
                if (LyricTextB != null) LyricTextB.Text = "";
                if (LyricsPlaceholderPanel != null)
                {
                    LyricsPlaceholderPanel.Visibility = Visibility.Collapsed;
                    LyricsPlaceholderPanel.BeginAnimation(OpacityProperty, null);
                    LyricsPlaceholderPanel.Opacity = 0;
                }
            };
            LyricsWidget.BeginAnimation(OpacityProperty, fadeOutLyrics);

            // Fade out lyrics blur background
            if (LyricsBlurBackground != null && LyricsBlurBackground.Visibility == Visibility.Visible)
            {
                var fadeOutBlur = new DoubleAnimation(LyricsBlurBackground.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(400)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseIn }
                };
                fadeOutBlur.Completed += (s, e) =>
                {
                    LyricsBlurBackground.Visibility = Visibility.Collapsed;
                    LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
                };
                LyricsBlurBackground.BeginAnimation(OpacityProperty, fadeOutBlur);
            }

            // Fade in calendar (delayed slightly so lyrics fades first)
            CalendarWidget.Visibility = Visibility.Visible;
            CalendarWidget.Opacity = 0;
            var fadeInCalendar = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(150)
            };
            CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);

            GreetingSection.Visibility = Visibility.Visible;
            GreetingSection.Opacity = 0;
            var fadeInGreeting = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(350)))
            {
                EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(150)
            };
            GreetingSection.BeginAnimation(OpacityProperty, fadeInGreeting);
        });
    }

    private void ClearLyrics()
    {
        _lyricsTrackKey = "";
        _lyricsService.Reset();
        HideLyricsWidget();
    }
}
