using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;
public partial class MainWindow
{
    #region Expanded Music Player Animations

    private bool _isMusicExpanded => _notchState.IsMusicExpanded;
    private bool _isMusicAnimating = false;
    private double _musicWidgetSmallWidth = 0;

    private void ExpandMusicWidget()
    {
        if (_isMusicAnimating) return;
        _isMusicAnimating = true;
        _notchState.TryTransitionTo(NotchState.MusicExpanding);
        UpdateProgressSectionLayout();

        UpdateZOrderTimerInterval();

        _musicWidgetSmallWidth = MediaWidgetContainer.ActualWidth;
        ResetCalendarHoverFocusVisualState();

        var expandDuration = new Duration(TimeSpan.FromMilliseconds(500));
        var contentDelay = TimeSpan.FromMilliseconds(150);

        var fadeOutCalendar = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutCalendar.Completed += (s, e) => CalendarWidget.Visibility = Visibility.Collapsed;
        CalendarWidget.BeginAnimation(OpacityProperty, fadeOutCalendar);

        // Also hide lyrics widget during music expand
        if (_isLyricsActive && LyricsWidget.Visibility == Visibility.Visible)
        {
            var fadeOutLyrics = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
            fadeOutLyrics.Completed += (s, e) => LyricsWidget.Visibility = Visibility.Collapsed;
            LyricsWidget.BeginAnimation(OpacityProperty, fadeOutLyrics);
        }

        var fadeOutControls = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutControls.Completed += (s, e) => MediaControls.Visibility = Visibility.Collapsed;
        MediaControls.BeginAnimation(OpacityProperty, fadeOutControls);

        var fadeOutBattery = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutBattery.Completed += (s, e) => BatterySection.Visibility = Visibility.Collapsed;
        BatterySection.BeginAnimation(OpacityProperty, fadeOutBattery);

        var fadeOutNavPanel = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutNavPanel.Completed += (s, e) => NavIconsPanel.Visibility = Visibility.Collapsed;
        NavIconsPanel.BeginAnimation(OpacityProperty, fadeOutNavPanel);

        var fadeOutNavBg = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutNavBg.Completed += (s, e) => NavIconsBackground.Visibility = Visibility.Collapsed;
        NavIconsBackground.BeginAnimation(OpacityProperty, fadeOutNavBg);

        // Fade out update notification if visible
        if (UpdateNotificationButton != null && UpdateNotificationButton.Visibility == Visibility.Visible)
        {
            StopUpdatePulseAnimation();
            var fadeOutUpdate = MakeAnim(UpdateNotificationButton.Opacity, 0d, _dur150, _easePowerIn2, null);
            fadeOutUpdate.Completed += (s, e) =>
            {
                UpdateNotificationButton.Visibility = Visibility.Collapsed;
                UpdateNotificationButton.IsHitTestVisible = false;
            };
            UpdateNotificationButton.BeginAnimation(OpacityProperty, fadeOutUpdate);
        }

        var fadeOutSettings = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutSettings.Completed += (s, e) => SettingsButton.Visibility = Visibility.Collapsed;
        SettingsButton.BeginAnimation(OpacityProperty, fadeOutSettings);

        var fadeOutGreeting = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutGreeting.Completed += (s, e) => GreetingSection.Visibility = Visibility.Collapsed;
        GreetingSection.BeginAnimation(OpacityProperty, fadeOutGreeting);

        double startWidth = MediaWidgetContainer.ActualWidth;
        double finalWidth = ExpandedContent.ActualWidth;

        MediaWidgetContainer.Width = startWidth;
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Left;
        Panel.SetZIndex(MediaWidgetContainer, 10);
        Grid.SetColumnSpan(MediaWidgetContainer, 3);

        var widthAnim = new DoubleAnimation(startWidth, finalWidth, expandDuration)
        {
            EasingFunction = _easeExpOut7
        };

        var marginAnim = new ThicknessAnimation(new Thickness(-8, 0, 0, 0), new Thickness(0), expandDuration)
        {
            EasingFunction = _easeExpOut7
        };

        widthAnim.Completed += (s, e) =>
        {
            MediaWidgetContainer.Width = double.NaN;
            MediaWidgetContainer.Margin = new Thickness(-8, 0, 0, 0);
            MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            UpdateProgressSectionLayout();
            UpdateProgressTimerState();
            MediaWidgetContainer.BeginAnimation(WidthProperty, null);
            MediaWidgetContainer.BeginAnimation(MarginProperty, null);
            _isMusicAnimating = false;
            _notchState.TryTransitionTo(NotchState.MusicExpanded);
        };

        MediaWidgetContainer.BeginAnimation(WidthProperty, widthAnim);
        MediaWidgetContainer.BeginAnimation(MarginProperty, marginAnim);

        InlineControls.Visibility = Visibility.Visible;

        var fadeInInline = MakeAnim(0d, 1d, _dur350, _easeExpOut7, contentDelay);
        InlineControls.BeginAnimation(OpacityProperty, fadeInInline);

        var slideUpAnim = MakeAnim(10, 0, _dur450, _easeSpring, contentDelay);
        var slideTransform = InlineControls.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 10);
        InlineControls.RenderTransform = slideTransform;
        slideTransform.BeginAnimation(TranslateTransform.YProperty, slideUpAnim);

        InlinePauseIcon.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
        InlinePlayIcon.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;

        SyncVolumeFromActiveSession();
    }

    private void CollapseMusicWidget()
    {
        if (_isMusicAnimating) return;
        _isMusicAnimating = true;
        _notchState.TryTransitionTo(NotchState.MusicCollapsing);
        UpdateProgressSectionLayout();

        UpdateZOrderTimerInterval();
        ResetCalendarHoverFocusVisualState();

        var collapseDuration = new Duration(TimeSpan.FromMilliseconds(400));
        var contentDelay = TimeSpan.FromMilliseconds(80);

        var fadeOutInline = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutInline.Completed += (s, e) =>
        {
            InlineControls.Visibility = Visibility.Collapsed;
        };
        InlineControls.BeginAnimation(OpacityProperty, fadeOutInline);

        double currentWidth = MediaWidgetContainer.ActualWidth;
        double targetSmallWidth = _musicWidgetSmallWidth > 0 ? _musicWidgetSmallWidth : (ExpandedContent.ActualWidth / 3.0) - 8;

        MediaWidgetContainer.Width = currentWidth;
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Left;

        var widthAnim = new DoubleAnimation(currentWidth, targetSmallWidth, collapseDuration)
        {
            EasingFunction = _easeExpOut7
        };

        var marginAnim = new ThicknessAnimation(new Thickness(0), new Thickness(-8, 0, 0, 0), collapseDuration)
        {
            EasingFunction = _easeExpOut7
        };

        widthAnim.Completed += (s, e) =>
        {
            MediaWidgetContainer.Width = double.NaN;
            MediaWidgetContainer.Margin = new Thickness(-8, 0, 0, 0);
            MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumnSpan(MediaWidgetContainer, 1);
            Panel.SetZIndex(MediaWidgetContainer, 0);
            UpdateProgressSectionLayout();
            UpdateProgressTimerState();
            MediaWidgetContainer.BeginAnimation(WidthProperty, null);
            MediaWidgetContainer.BeginAnimation(MarginProperty, null);
            _isMusicAnimating = false;
            _notchState.TryTransitionTo(NotchState.Expanded);
        };

        MediaWidgetContainer.BeginAnimation(WidthProperty, widthAnim);
        MediaWidgetContainer.BeginAnimation(MarginProperty, marginAnim);

        MediaControls.BeginAnimation(OpacityProperty, null);
        MediaControls.Opacity = 0;
        MediaControls.Visibility = Visibility.Visible;
        var fadeInControls = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, contentDelay);
        MediaControls.BeginAnimation(OpacityProperty, fadeInControls);

        CalendarWidget.BeginAnimation(OpacityProperty, null);
        CalendarWidget.Opacity = 0;
        CalendarWidget.Visibility = Visibility.Visible;
        var fadeInCalendar = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
        CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);

        // If lyrics are active, show lyrics widget instead of calendar
        if (_isLyricsActive)
        {
            CalendarWidget.Visibility = Visibility.Collapsed;
            GreetingSection.Visibility = Visibility.Collapsed;
            LyricsWidget.Visibility = Visibility.Visible;
            LyricsWidget.Opacity = 0;
            var fadeInLyrics = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
            LyricsWidget.BeginAnimation(OpacityProperty, fadeInLyrics);
        }

        BatterySection.BeginAnimation(OpacityProperty, null);
        BatterySection.Opacity = 0;
        BatterySection.Visibility = Visibility.Visible;
        var fadeInBattery = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(100));
        BatterySection.BeginAnimation(OpacityProperty, fadeInBattery);

        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Visible;
        var fadeInNavPanel = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(100));
        NavIconsPanel.BeginAnimation(OpacityProperty, fadeInNavPanel);

        if (_isSecondaryView)
        {
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 0;
            NavIconsBackground.Visibility = Visibility.Visible;
            var fadeInNavBg = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(100));
            NavIconsBackground.BeginAnimation(OpacityProperty, fadeInNavBg);
        }

        // Animate update notification if available
        if (_isUpdateAvailable && UpdateNotificationButton != null)
        {
            UpdateNotificationButton.BeginAnimation(OpacityProperty, null);
            UpdateNotificationButton.Opacity = 0;
            UpdateNotificationButton.Visibility = Visibility.Visible;
            UpdateNotificationButton.IsHitTestVisible = true;
            UpdateNotificationButton.Cursor = System.Windows.Input.Cursors.Hand;
            
            // Reset icon color
            if (UpdateIconBrush != null)
            {
                UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);
            }

            // Start pulse deterministically for every reveal.
            StartUpdatePulseAnimation();

            var fadeInUpdate = MakeAnim(0d, 1.0d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(110));
            UpdateNotificationButton.BeginAnimation(OpacityProperty, fadeInUpdate);
        }

        SettingsButton.BeginAnimation(OpacityProperty, null);
        SettingsButton.Opacity = 0;
        SettingsButton.Visibility = Visibility.Visible;
        var fadeInSettings = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
        SettingsButton.BeginAnimation(OpacityProperty, fadeInSettings);

        GreetingSection.BeginAnimation(OpacityProperty, null);
        GreetingSection.Opacity = 0;
        GreetingSection.Visibility = _isLyricsActive ? Visibility.Collapsed : Visibility.Visible;
        if (!_isLyricsActive)
        {
            var fadeInGreeting = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(140));
            GreetingSection.BeginAnimation(OpacityProperty, fadeInGreeting);
        }
    }

    private void UpdateProgressSectionLayout()
    {
        if (ProgressSection == null || ProgressBarContainer == null || MediaInfoSection == null)
            return;

        bool useCompactLayout = !_isMusicExpanded;
        double fallbackWidth = useCompactLayout ? 208 : 340;
        double visibleTextWidth = GetVisibleMediaTextWidth(fallbackWidth);

        double containerHeight = useCompactLayout ? 10 : 14;
        double barHeight = 4;
        double barRadius = 2.0;
        double timeTopMargin = useCompactLayout ? 4 : 2;
        double timeSideMargin = useCompactLayout ? 4 : 6;
        double progressRightInset = 0;
        double targetWidth = Math.Max(0, fallbackWidth - 29);

        Grid.SetColumnSpan(MediaInfoSection, useCompactLayout ? 2 : 1);

        ProgressSection.HorizontalAlignment = HorizontalAlignment.Left;
        ProgressSection.Width = targetWidth;
        ProgressSection.Margin = new Thickness(0, useCompactLayout ? 8 : 10, progressRightInset, 0);
        ProgressBarContainer.Margin = new Thickness(0);

        ProgressBarContainer.Height = containerHeight;
        ProgressBarBg.Height = barHeight;
        ProgressBar.Height = barHeight;
        IndeterminateProgress.Height = barHeight;

        var cornerRadius = new CornerRadius(barRadius);
        ProgressBarBg.CornerRadius = cornerRadius;
        ProgressBar.CornerRadius = new CornerRadius(0);
        IndeterminateProgress.CornerRadius = cornerRadius;

        if (ProgressBarClip != null)
        {
            ProgressBarClip.RadiusX = barRadius;
            ProgressBarClip.RadiusY = barRadius;
        }

        CurrentTimeText.Margin = new Thickness(0, timeTopMargin, timeSideMargin, 0);
        RemainingTimeText.Margin = new Thickness(timeSideMargin, timeTopMargin, 0, 0);
    }

    private void MediaInfoSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateProgressSectionLayout();
    }

    private void ProgressBarBg_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ProgressBarClip != null)
        {
            ProgressBarClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void MediaWidgetContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double fadeEndX = GetVisibleMediaTextWidth(340);

        if (TitleScrollContainer != null)
        {
            TitleScrollContainer.Width = fadeEndX;
        }

        if (ArtistScrollContainer != null)
        {
            ArtistScrollContainer.Width = fadeEndX;
        }

        RefreshMediaMarquee();
        
        if (TextFadeBrush != null)
        {
            TextFadeBrush.EndPoint = new Point(fadeEndX, 0);
            TextFadeBrush.GradientStops[0].Offset = 0;
            double fadeStartX = Math.Max(0, fadeEndX - 20);
            TextFadeBrush.GradientStops[1].Offset = fadeEndX > 0 ? fadeStartX / fadeEndX : 0.8;
            TextFadeBrush.GradientStops[2].Offset = 1;
        }

        UpdateProgressSectionLayout();
    }

    private double GetVisibleMediaTextWidth(double fallbackWidth)
    {
        double widgetWidth = MediaWidgetContainer?.ActualWidth > 0 ? MediaWidgetContainer.ActualWidth : 0;
        if (widgetWidth > 0)
        {
            double thumbnailWidth = ThumbnailBorder?.ActualWidth > 0 ? ThumbnailBorder.ActualWidth : 102;
            double thumbnailGap = ThumbnailBorder?.Margin.Right ?? 8;
            double availableWidth = widgetWidth - thumbnailWidth - thumbnailGap - 4;
            return Math.Max(0, Math.Min(340, availableWidth));
        }

        double infoWidth = MediaInfoSection?.ActualWidth > 0 ? MediaInfoSection.ActualWidth : 0;
        if (infoWidth > 0)
        {
            return Math.Max(0, Math.Min(340, infoWidth));
        }

        return fallbackWidth;
    }

    #endregion
}

