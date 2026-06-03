using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controllers;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Gesture Controls

    private GestureController _gestureController = null!;
    private bool _isGestureActive = false;
    private TranslateTransform? _gestureTranslate;

    private void InitializeGestureController()
    {
        _gestureController = new GestureController();
        _gestureController.SwipeLeft += OnGestureSwipeLeft;
        _gestureController.SwipeRight += OnGestureSwipeRight;
        _gestureController.SwipeDown += OnGestureSwipeDown;
        _gestureController.DoubleTap += OnGestureDoubleTap;
    }

    // ─── XAML-bound event handlers ───

    private void NotchWrapper_MouseMove(object sender, MouseEventArgs e)
    {
        NotchBorder_GestureMouseMove(sender, e);
    }

    private void NotchWrapper_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        NotchBorder_GestureMouseUp(sender, e);
    }

    private bool TryBeginGesture(MouseButtonEventArgs e)
    {
        if (!_settings.EnableGestureControls)
        {
            RuntimeLog.Log("GESTURE", "blocked: EnableGestureControls=false");
            return false;
        }
        if (_isAnimating)
        {
            RuntimeLog.Log("GESTURE", "blocked: _isAnimating=true");
            return false;
        }

        // Only handle gestures when collapsed (compact mode) with media playing
        if (_isExpanded || _isMusicExpanded)
        {
            RuntimeLog.Log("GESTURE", $"blocked: expanded={_isExpanded} musicExpanded={_isMusicExpanded}");
            return false;
        }
        if (_currentMediaInfo == null || !_currentMediaInfo.IsAnyMediaPlaying)
        {
            RuntimeLog.Log("GESTURE", $"blocked: mediaInfo={(_currentMediaInfo != null)} isPlaying={_currentMediaInfo?.IsAnyMediaPlaying}");
            return false;
        }

        var pos = e.GetPosition(NotchBorder);
        _gestureController.BeginTracking(pos);
        _isGestureActive = true;

        NotchWrapper.CaptureMouse();

        return true;
    }

    private void NotchBorder_GestureMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isGestureActive || !_gestureController.IsTracking) return;

        var pos = e.GetPosition(NotchBorder);
        bool triggered = _gestureController.UpdateTracking(pos);

        if (!triggered && !_gestureController.GestureTriggered)
        {
            // Provide real-time visual feedback: slight horizontal shift following the finger
            ApplyGestureDragFeedback(_gestureController.AccumulatedX);
        }
    }

    private void NotchBorder_GestureMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isGestureActive) return;

        NotchWrapper.ReleaseMouseCapture();
        _isGestureActive = false;

        var pos = e.GetPosition(NotchBorder);
        bool wasTap = _gestureController.EndTracking(pos);

        // If no gesture was triggered and it wasn't a double-tap, snap back and let normal click through
        if (!_gestureController.GestureTriggered && !wasTap)
        {
            AnimateGestureSnapBack();
            // Allow normal click behavior — re-dispatch
            ToggleNotchFromClick(e.ClickCount);
        }
        else if (!_gestureController.GestureTriggered && wasTap)
        {
            // Double-tap was handled by the controller
            AnimateGestureSnapBack();
        }
        else
        {
            // Gesture was triggered during move — snap back animation already played
            AnimateGestureSnapBack();
        }

        // After gesture ends, restore correct hover state based on whether cursor is still over the notch
        if (!NotchWrapper.IsMouseOver)
        {
            AnimateNotchHover(false);
            if (_isMusicCompactMode && _isCompactThumbnailHovered)
            {
                SetCompactThumbnailHover(false);
            }
        }
    }

    // ─── Gesture Handlers ───

    private async void OnGestureSwipeLeft()
    {
        // Swipe left = Next track (always skip, regardless of source)
        Dispatcher.Invoke(() =>
        {
            PlayGestureSwipeFeedback(isLeft: true);
            PlayNextSkipAnimation();
            OptimisticPrepareForNextTrack();
        });

        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            await _mediaService.NextTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("GESTURE", ex, "SwipeLeft/Next failed");
        }
    }

    private async void OnGestureSwipeRight()
    {
        // Swipe right = Previous track (always skip, regardless of source)
        Dispatcher.Invoke(() =>
        {
            PlayGestureSwipeFeedback(isLeft: false);
            PlayPrevSkipAnimation();
        });

        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            OptimisticPrepareForPreviousTrack();
            await _mediaService.PreviousTrackAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("GESTURE", ex, "SwipeRight/Prev failed");
        }
    }

    private void OnGestureSwipeDown()
    {
        // Swipe down = Open File Shelf (expand + switch to secondary)
        Dispatcher.Invoke(() =>
        {
            PlayGestureSwipeDownFeedback();

            if (!_isExpanded && !_isAnimating)
            {
                ExpandNotch();

                // Wait for expand to finish, then switch to secondary view
                var waitTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(40)
                };
                waitTimer.Tick += (s, args) =>
                {
                    if (!_isAnimating)
                    {
                        waitTimer.Stop();
                        if (!_isSecondaryView)
                        {
                            SwitchToSecondaryView();
                        }
                    }
                };
                waitTimer.Start();
            }
        });
    }

    private async void OnGestureDoubleTap()
    {
        // Double-tap = Play/Pause
        Dispatcher.Invoke(() =>
        {
            PlayGestureDoubleTapFeedback();
        });

        try
        {
            if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
            _lastMediaActionTime = DateTime.Now;

            _isPlaying = !_isPlaying;
            Dispatcher.Invoke(() => UpdatePlayPauseIcon());

            _mediaProgressController.NotifyUserPlayPause(_isPlaying);
            await _mediaService.PlayPauseAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("GESTURE", ex, "DoubleTap/PlayPause failed");
        }
    }

    // ─── Visual Feedback Animations ───

    private void ApplyGestureDragFeedback(double deltaX)
    {
        // Dampen the movement (rubber-band feel)
        double dampened = deltaX * 0.3;
        double clamped = Math.Clamp(dampened, -20, 20);

        if (_gestureTranslate == null)
        {
            _gestureTranslate = NotchBorder.RenderTransform as TranslateTransform;
            if (_gestureTranslate == null)
            {
                _gestureTranslate = new TranslateTransform(0, 0);
                NotchBorder.RenderTransform = _gestureTranslate;
            }
        }

        _gestureTranslate.X = clamped;
    }

    private void AnimateGestureSnapBack()
    {
        if (_gestureTranslate == null) return;

        var snapBack = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(350)))
        {
            EasingFunction = _easeSoftSpring
        };
        Timeline.SetDesiredFrameRate(snapBack, VNotch.Services.AnimationConfig.TargetFps);
        snapBack.Completed += (s, e) =>
        {
            _gestureTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _gestureTranslate.X = 0;
        };

        _gestureTranslate.BeginAnimation(TranslateTransform.XProperty, snapBack);
    }

    private void PlayGestureSwipeFeedback(bool isLeft)
    {
        if (_gestureTranslate == null)
        {
            _gestureTranslate = NotchBorder.RenderTransform as TranslateTransform;
            if (_gestureTranslate == null)
            {
                _gestureTranslate = new TranslateTransform(0, 0);
                NotchBorder.RenderTransform = _gestureTranslate;
            }
        }

        double target = isLeft ? -12 : 12;

        var flick = new DoubleAnimationUsingKeyFrames();
        flick.KeyFrames.Add(new EasingDoubleKeyFrame(target,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            _easeQuadOut));
        flick.KeyFrames.Add(new EasingDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(flick, VNotch.Services.AnimationConfig.TargetFps);

        flick.Completed += (s, e) =>
        {
            _gestureTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            _gestureTranslate.X = 0;
        };

        _gestureTranslate.BeginAnimation(TranslateTransform.XProperty, flick);

        // Scale pulse for tactile feedback
        var pulse = new DoubleAnimationUsingKeyFrames();
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.96,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
            _easeQuadOut));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(pulse, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private void PlayGestureSwipeDownFeedback()
    {
        var pullDown = new DoubleAnimationUsingKeyFrames();
        pullDown.KeyFrames.Add(new EasingDoubleKeyFrame(1.08,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            _easeQuadOut));
        pullDown.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(pullDown, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, pullDown);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pullDown);
    }

    private void PlayGestureDoubleTapFeedback()
    {
        var bounce = new DoubleAnimationUsingKeyFrames();
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.92,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
            _easeQuadIn));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.06,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
            _easeQuadOut));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounce, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    private void DisposeGestureController()
    {
        if (_gestureController != null)
        {
            _gestureController.SwipeLeft -= OnGestureSwipeLeft;
            _gestureController.SwipeRight -= OnGestureSwipeRight;
            _gestureController.SwipeDown -= OnGestureSwipeDown;
            _gestureController.DoubleTap -= OnGestureDoubleTap;
        }
    }

    #endregion
}

