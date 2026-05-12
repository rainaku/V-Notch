using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;
public partial class MainWindow
{
    private void NotchWrapper_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isExpanded || _isAnimating) return;
        if (e.Handled) return;

        e.Handled = true;

        // Cooldown prevents rapid double-fire (touchpad inertia, multiple queued events)
        if ((DateTime.UtcNow - _lastViewSwitchUtc) < ViewSwitchCooldown) return;

        if (e.Delta < 0) 
        {
            if (!_isSecondaryView)
            {
                SwitchToSecondaryView();
            }
        }
        else if (e.Delta > 0) 
        {
            if (_isSecondaryView)
            {
                SwitchToPrimaryView();
            }
        }
    }

    private void SecondaryContent_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; 
    }

    private void HomeIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isSecondaryView && !_isAnimating)
        {
            SwitchToPrimaryView();
        }
    }

    private void FileShelfIconButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_isSecondaryView && !_isAnimating)
        {
            SwitchToSecondaryView();
        }
    }

    private void SwitchToSecondaryView()
    {
        if (_isSecondaryView || _isAnimating) return;
        _isSecondaryView = true;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;

        // Refresh shelf text to current language
        UpdateShelfCapacityIndicator();

        UpdateNavIconsActiveState();
        NavIconsPanel.Visibility = Visibility.Visible;
        NavIconsPanel.Opacity = 1;

        // Delay background appearance with fade animation
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Visible;
        var navBgFadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(300)))
        {
            EasingFunction = _easePowerOut3,
            BeginTime = TimeSpan.FromMilliseconds(200)
        };
        Timeline.SetDesiredFrameRate(navBgFadeIn, 120);
        NavIconsBackground.BeginAnimation(OpacityProperty, navBgFadeIn);

        NotchBorder.IsHitTestVisible = false;

        var durOut  = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn   = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        
        var primaryGroup = new TransformGroup();
        var primaryScale = new ScaleTransform(1, 1);
        var primaryTranslate = new TranslateTransform(0, 0);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut    = MakeAnim(1, 0,    durOut, _easeQuadIn);
        var slideUp    = MakeAnim(0, -16,  durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideUp,    fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        
        var expandedBlur = ExpandedContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        ExpandedContent.Effect = expandedBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, e) =>
        {
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedContent.RenderTransform = null;
            ExpandedContent.Effect = null;
            expandedBlur.Radius = 0;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeOut);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        expandedBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        
        SecondaryContent.Visibility = Visibility.Visible;
        SecondaryContent.Opacity = 0;
        EnableKeyboardInput();

        var secondaryGroup     = new TransformGroup();
        var secondaryScale     = new ScaleTransform(0.93, 0.93);
        var secondaryTranslate = new TranslateTransform(0, 26);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn       = MakeAnim(0, 1,    durIn, _easeExpOut6,   inDelay);
        var springSlide  = MakeAnim(26, 0,   durIn, _easeExpOut7,   inDelay);
        var springScaleX = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1, durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn,       fps);
        Timeline.SetDesiredFrameRate(springSlide,  fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);



        fadeIn.Completed += (s, e) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            SecondaryContent.Opacity = 1;
            SecondaryContent.BeginAnimation(OpacityProperty, null);
            SecondaryContent.RenderTransform = null;

            if (_isCameraActive)
            {
                AnimateCameraSectionToShelf(true);
            }
            else
            {
                ResetCameraSectionLayoutInstant();
            }
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeIn);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
    }

    private void SwitchToPrimaryView()
    {
        if (!_isSecondaryView || _isAnimating) return;
        _isSecondaryView = false;
        _isAnimating = true;
        _lastViewSwitchUtc = DateTime.UtcNow;

        UpdateNavIconsActiveState();
        NavIconsBackground.BeginAnimation(OpacityProperty, null);
        NavIconsBackground.Opacity = 0;
        NavIconsBackground.Visibility = Visibility.Collapsed;

        NotchBorder.IsHitTestVisible = false;
        ResetCameraSectionLayoutInstant();

        var durOut  = new Duration(TimeSpan.FromMilliseconds(180));
        var durIn   = new Duration(TimeSpan.FromMilliseconds(480));
        var inDelay = TimeSpan.FromMilliseconds(50);
        const int fps = 144;

        
        var secondaryGroup     = new TransformGroup();
        var secondaryScale     = new ScaleTransform(1, 1);
        var secondaryTranslate = new TranslateTransform(0, 0);
        secondaryGroup.Children.Add(secondaryScale);
        secondaryGroup.Children.Add(secondaryTranslate);
        SecondaryContent.RenderTransform = secondaryGroup;
        SecondaryContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeOut    = MakeAnim(1, 0,    durOut, _easeQuadIn);
        var slideDown  = MakeAnim(0, 16,   durOut, _easeQuadIn);
        var scaleDownX = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        var scaleDownY = MakeAnim(1, 0.93, durOut, _easeQuadIn);
        Timeline.SetDesiredFrameRate(slideDown,  fps);
        Timeline.SetDesiredFrameRate(scaleDownX, fps);
        Timeline.SetDesiredFrameRate(scaleDownY, fps);

        
        var secondaryBlur = SecondaryContent.Effect as BlurEffect ?? new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        SecondaryContent.Effect = secondaryBlur;
        var blurOutAnim = MakeAnim(0, 10, durOut, _easeQuadIn);

        fadeOut.Completed += (s, e) =>
        {
            SecondaryContent.Visibility = Visibility.Collapsed;
            SecondaryContent.RenderTransform = null;
            SecondaryContent.Effect = null;
            secondaryBlur.Radius = 0;
            DisableKeyboardInput();
        };

        SecondaryContent.BeginAnimation(OpacityProperty, fadeOut);
        secondaryTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        secondaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);
        secondaryBlur.BeginAnimation(BlurEffect.RadiusProperty, blurOutAnim);

        
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Effect = null;

        var primaryGroup     = new TransformGroup();
        var primaryScale     = new ScaleTransform(0.93, 0.93);
        var primaryTranslate = new TranslateTransform(0, -26);
        primaryGroup.Children.Add(primaryScale);
        primaryGroup.Children.Add(primaryTranslate);
        ExpandedContent.RenderTransform = primaryGroup;
        ExpandedContent.RenderTransformOrigin = new Point(0.5, 0.5);

        var fadeIn       = MakeAnim(0, 1,     durIn, _easeExpOut6,    inDelay);
        var springSlide  = MakeAnim(-26, 0,   durIn, _easeExpOut7,    inDelay);
        var springScaleX = MakeAnim(0.93, 1,  durIn, _easeSoftSpring, inDelay);
        var springScaleY = MakeAnim(0.93, 1,  durIn, _easeSoftSpring, inDelay);
        Timeline.SetDesiredFrameRate(fadeIn,       fps);
        Timeline.SetDesiredFrameRate(springSlide,  fps);
        Timeline.SetDesiredFrameRate(springScaleX, fps);
        Timeline.SetDesiredFrameRate(springScaleY, fps);



        fadeIn.Completed += (s, e) =>
        {
            _isAnimating = false;
            NotchBorder.IsHitTestVisible = true;
            ExpandedContent.Opacity = 1;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
            ExpandedContent.RenderTransform = null;
        };

        ExpandedContent.BeginAnimation(OpacityProperty, fadeIn);
        primaryTranslate.BeginAnimation(TranslateTransform.YProperty, springSlide);
        primaryScale.BeginAnimation(ScaleTransform.ScaleXProperty, springScaleX);
        primaryScale.BeginAnimation(ScaleTransform.ScaleYProperty, springScaleY);
    }

    private void UpdateNavIconsActiveState()
    {
        if (_isSecondaryView)
        {
            HomeIconButton.Opacity = 0.4;
            FileShelfIconButton.Opacity = 1.0;
        }
        else
        {
            HomeIconButton.Opacity = 1.0;
            FileShelfIconButton.Opacity = 0.4;
        }
    }

}

