using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    #region Clipboard Peek

    private bool _clipboardListenerRegistered = false;
    private DateTime _lastClipboardFlashUtc = DateTime.MinValue;
    private static readonly TimeSpan ClipboardFlashCooldown = TimeSpan.FromMilliseconds(400);
    private bool _isClipboardPeekActive = false;
    private DispatcherTimer? _clipboardRevertTimer;

    private void RegisterClipboardListener()
    {
        if (_clipboardListenerRegistered || _hwnd == IntPtr.Zero) return;

        if (AddClipboardFormatListener(_hwnd))
        {
            _clipboardListenerRegistered = true;
        }
    }

    private void UnregisterClipboardListener()
    {
        if (!_clipboardListenerRegistered || _hwnd == IntPtr.Zero) return;

        RemoveClipboardFormatListener(_hwnd);
        _clipboardListenerRegistered = false;
    }

    private void HandleClipboardUpdate()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastClipboardFlashUtc) < ClipboardFlashCooldown) return;
        _lastClipboardFlashUtc = now;

        if (!IsEffectivelyNotchVisible) return;

        Dispatcher.BeginInvoke(new Action(PlayClipboardPeek));
    }
    private void PlayClipboardPeek()
    {
        // ─── Gentle scale bounce (only when collapsed and not animating) ───
        if (!_isExpanded && !_isAnimating)
        {
            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            var peakTime = TimeSpan.FromMilliseconds(100);
            var endTime = TimeSpan.FromMilliseconds(500);

            var bounceX = new DoubleAnimationUsingKeyFrames();
            bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.06,
                KeyTime.FromTimeSpan(peakTime), _easeQuadOut));
            bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(endTime), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(bounceX, 144);

            var bounceY = new DoubleAnimationUsingKeyFrames();
            bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.95,
                KeyTime.FromTimeSpan(peakTime), _easeQuadOut));
            bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(endTime), _easeSoftSpring));
            Timeline.SetDesiredFrameRate(bounceY, 144);

            NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
            NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
            NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        }

        // ─── Equalizer → Checkmark + "Copied" text (only in compact music mode) ───
        if (_isMusicCompactMode && !_isExpanded)
        {
            ShowClipboardCopiedState();
        }
    }
    private void ShowClipboardCopiedState()
    {
        // Volume indicator takes priority — don't show "Copied" if volume bar is active
        if (_isVolumeIndicatorActive) return;

        _isClipboardPeekActive = true;

        // Hide privacy dot during clipboard notification
        SuppressPrivacyDot();

        // Update text with localization
        ClipboardCopiedText.Text = Loc.Get("clipboard.copied");

        // ─── Immediately hide all other notch elements to prevent overlap ───
        // Cancel any ongoing thumbnail switch animations that could restore visibility
        CancelThumbnailSwitchAnimations();

        // Force-hide thumbnail immediately (cancel any running animations first)
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailBorder.Opacity = 0;
        CompactThumbnailBorder.Visibility = Visibility.Collapsed;

        // Force-hide MusicViz immediately
        MusicViz.BeginAnimation(OpacityProperty, null);
        MusicViz.Opacity = 0;
        MusicViz.Visibility = Visibility.Collapsed;

        // Hide compact hover info if visible
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        CompactHoverInfo.Visibility = Visibility.Collapsed;

        // ─── Show checkmark icon with scale-in spring ───
        ClipboardCheckIcon.Visibility = Visibility.Visible;
        ClipboardCheckIcon.BeginAnimation(OpacityProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        ClipboardCheckScale.ScaleX = 0.5;
        ClipboardCheckScale.ScaleY = 0.5;

        var checkFadeIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
        var checkScaleIn = MakeAnim(0.5, 1.0, _dur400, _easeSoftSpring);
        Timeline.SetDesiredFrameRate(checkScaleIn, 144);

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, checkFadeIn);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, checkScaleIn);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, checkScaleIn);

        // ─── Show "Copied" text with slide-in from left ───
        ClipboardCopiedText.Visibility = Visibility.Visible;
        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        ClipboardCopiedTranslate.X = -8;

        var textFadeIn = MakeAnim(0.0, 1.0, _dur250, _easeQuadOut);
        var textSlideIn = MakeAnim(-8.0, 0.0, _dur350, _easeExpOut6);
        Timeline.SetDesiredFrameRate(textSlideIn, 144);

        ClipboardCopiedText.BeginAnimation(OpacityProperty, textFadeIn);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, textSlideIn);

        // ─── Schedule revert ───
        _clipboardRevertTimer?.Stop();
        _clipboardRevertTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _clipboardRevertTimer.Tick += (s, e) =>
        {
            _clipboardRevertTimer.Stop();
            RevertClipboardCopiedState();
        };
        _clipboardRevertTimer.Start();
    }
    private void RevertClipboardCopiedState()
    {
        _isClipboardPeekActive = false;

        // Restore privacy dot
        RestorePrivacyDotVisibility();

        // ─── Checkmark: scale 1→0.5 + fade out (reverse of scale-in spring) ───
        ClipboardCheckIcon.BeginAnimation(OpacityProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var checkFadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        var checkScaleOut = MakeAnim(1.0, 0.5, _dur400, _easeSoftSpring);
        Timeline.SetDesiredFrameRate(checkScaleOut, 144);

        checkFadeOut.Completed += (s, e) =>
        {
            if (!_isClipboardPeekActive)
                ClipboardCheckIcon.Visibility = Visibility.Collapsed;
        };

        ClipboardCheckIcon.BeginAnimation(OpacityProperty, checkFadeOut);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, checkScaleOut);
        ClipboardCheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, checkScaleOut);

        // ─── Text: slide 0→-8 + fade out (reverse of slide-in) ───
        ClipboardCopiedText.BeginAnimation(OpacityProperty, null);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        var textFadeOut = MakeAnim(1.0, 0.0, _dur250, _easeQuadOut);
        var textSlideOut = MakeAnim(0.0, -8.0, _dur350, _easeExpOut6);
        Timeline.SetDesiredFrameRate(textSlideOut, 144);

        textFadeOut.Completed += (s, e) =>
        {
            if (!_isClipboardPeekActive)
                ClipboardCopiedText.Visibility = Visibility.Collapsed;
        };

        ClipboardCopiedText.BeginAnimation(OpacityProperty, textFadeOut);
        ClipboardCopiedTranslate.BeginAnimation(TranslateTransform.XProperty, textSlideOut);

        // ─── Restore thumbnail (reverse of hide) ───
        CompactThumbnailBorder.BeginAnimation(OpacityProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactThumbnailScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactThumbnailScale.ScaleX = 1.0;
        CompactThumbnailScale.ScaleY = 1.0;
        CompactThumbnailBorder.Opacity = 1.0;
        CompactThumbnailBorder.Visibility = Visibility.Visible;

        // ─── MusicViz: fade in (reverse of fade out) ───
        MusicViz.Visibility = Visibility.Visible;
        MusicViz.Opacity = 0;
        MusicViz.BeginAnimation(OpacityProperty, null);
        var vizFadeIn = MakeAnim(0.0, 1.0, _dur100, _easeQuadOut);
        MusicViz.BeginAnimation(OpacityProperty, vizFadeIn);
    }

    #endregion
}
