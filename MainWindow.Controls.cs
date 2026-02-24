using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Media Controls

    private bool _isPlaying = true;

    private async void PlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        _isPlaying = !_isPlaying;
        UpdatePlayPauseIcon();
        PlayButtonPressAnimation(PlayPauseButton);

        await _mediaService.PlayPauseAsync();
    }

    private async void NextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        PlayNextSkipAnimation();

        if (_currentMediaInfo?.IsVideoSource == true)
        {
            await SeekRelative(15);
        }
        else
        {
            await _mediaService.NextTrackAsync();
        }
    }

    private async void PrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        PlayPrevSkipAnimation();

        if (_currentMediaInfo?.IsVideoSource == true)
        {
            await SeekRelative(-15);
        }
        else
        {
            await _mediaService.PreviousTrackAsync();
        }
    }

    private void UpdatePlayPauseIcon()
    {
        var duration = TimeSpan.FromMilliseconds(180);

        if (_isPlaying)
        {
            AnimateIconSwitch(PlayIcon, PauseIcon, duration, _easeQuadInOut);
            AnimateIconSwitch(InlinePlayIcon, InlinePauseIcon, duration, _easeQuadInOut);
        }
        else
        {
            AnimateIconSwitch(PauseIcon, PlayIcon, duration, _easeQuadInOut);
            AnimateIconSwitch(InlinePauseIcon, InlinePlayIcon, duration, _easeQuadInOut);
        }
    }

    private async void InlinePlayPauseButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        _isPlaying = !_isPlaying;
        UpdatePlayPauseIcon();
        PlayButtonPressAnimation(InlinePlayPauseButton);

        await _mediaService.PlayPauseAsync();
    }

    private async void InlineNextButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        PlayNextSkipAnimation(InlineNextArrow0, InlineNextArrow1, InlineNextArrow2);

        if (_currentMediaInfo?.IsVideoSource == true)
        {
            await SeekRelative(15);
        }
        else
        {
            await _mediaService.NextTrackAsync();
        }
    }

    private async void InlinePrevButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if ((DateTime.Now - _lastMediaActionTime).TotalMilliseconds < 500) return;
        _lastMediaActionTime = DateTime.Now;

        PlayPrevSkipAnimation(InlinePrevArrow0, InlinePrevArrow1, InlinePrevArrow2);

        if (_currentMediaInfo?.IsVideoSource == true)
        {
            await SeekRelative(-15);
        }
        else
        {
            await _mediaService.PreviousTrackAsync();
        }
    }

    private void SendMediaKey(byte key)
    {
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    #endregion

    #region Media Widget Click

    private void ThumbnailBorder_Click(object sender, MouseButtonEventArgs e)
    {
        MediaWidget_Click(sender, e);
    }

    private void MediaWidget_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isMusicExpanded)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source == InlineControls) return;
                if (source == MediaWidget) break;
                source = VisualTreeHelper.GetParent(source);
            }
        }

        e.Handled = true;
        if (_isMusicAnimating) return;

        if (_isMusicExpanded)
            CollapseMusicWidget();
        else
            ExpandMusicWidget();
    }

    #endregion

    #region Volume Control

    private float _currentVolume = 0.5f;

    private void VolumeIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_volumeService.IsAvailable)
        {
            _volumeService.ToggleMute();
            SyncVolumeFromSystem();
        }
    }

    private void VolumeIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(1, 1.2, _dur150) { EasingFunction = _easeQuadOut };
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        var animX = new DoubleAnimation(VolumeIconScale.ScaleX, 1, _dur150) { EasingFunction = _easeQuadOut };
        var animY = new DoubleAnimation(VolumeIconScale.ScaleY, 1, _dur150) { EasingFunction = _easeQuadOut };
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        VolumeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void VolumeBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _isDraggingVolume = true;
        VolumeBarContainer.CaptureMouse();
        SetVolumeFromMousePosition(e);
    }

    private void VolumeBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingVolume && e.LeftButton == MouseButtonState.Pressed)
        {
            SetVolumeFromMousePosition(e);
        }
    }

    private void VolumeBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingVolume)
        {
            _isDraggingVolume = false;
            VolumeBarContainer.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void SetVolumeFromMousePosition(MouseEventArgs e)
    {
        const double volumeBarWidth = 100.0;
        var pos = e.GetPosition(VolumeBarContainer);
        float newVolume = (float)Math.Clamp(pos.X / volumeBarWidth, 0.0, 1.0);

        _currentVolume = newVolume;
        VolumeBarScale.ScaleX = newVolume;
        UpdateVolumeIcon(newVolume, false);

        if (_volumeService.IsAvailable)
        {
            _volumeService.SetVolume(newVolume);
        }
    }

    private void SyncVolumeFromSystem()
    {
        if (_isDraggingVolume) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDraggingVolume) return;

            if (_volumeService.IsAvailable)
            {
                _currentVolume = _volumeService.GetVolume();
                bool isMuted = _volumeService.GetMute();

                VolumeBarScale.ScaleX = _currentVolume;
                UpdateVolumeIcon(_currentVolume, isMuted);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UpdateVolumeIcon(float volume, bool isMuted)
    {
        if (isMuted || volume <= 0.01f)
        {
            VolumeIcon.Text = "\uE74F";
        }
        else if (volume < 0.33f)
        {
            VolumeIcon.Text = "\uE993";
        }
        else if (volume < 0.66f)
        {
            VolumeIcon.Text = "\uE994";
        }
        else
        {
            VolumeIcon.Text = "\uE995";
        }
    }

    #endregion
}