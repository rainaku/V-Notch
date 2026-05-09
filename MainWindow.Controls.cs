using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using VNotch.Services;
using VNotch.Models;

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

    private void ThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private void CompactThumbnailBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCurrentMediaSourceFromThumbnail();
    }

    private async void OpenCurrentMediaSourceFromThumbnail()
    {
        var info = _currentMediaInfo;
        if (info == null || !info.IsAnyMediaPlaying) return;
        await TryActivateMediaProcess(info);
    }

    private async Task<bool> TryActivateMediaProcess(MediaInfo info)
    {
        var candidates = GetMediaProcessCandidates(info).ToList();
        var processNames = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        bool preferBrowserTabMatch = info.IsVideoSource || info.MediaSource is "YouTube" or "SoundCloud" or "Facebook" or "TikTok" or "Instagram" or "Twitter" or "Browser";

        if (preferBrowserTabMatch && TryActivateBestMediaWindow(info, processNames, out bool usedBrowser))
        {
            return true;
        }

        foreach (string processName in candidates)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    process.Refresh();
                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (TryActivateWindow(hwnd))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        if (TryActivateBestMediaWindow(info, processNames, out bool fallbackUsedBrowser))
        {
            return true;
        }

        return false;
    }

    private static bool TryActivateBestMediaWindow(MediaInfo info, ISet<string> processNames, out bool usedBrowser)
    {
        usedBrowser = false;
        IntPtr bestHwnd = IntPtr.Zero;
        string bestProcessName = string.Empty;
        int bestScore = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0) return true;

            string processName;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                return true;
            }

            if (processNames.Count > 0 && !processNames.Contains(processName)) return true;

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            int score = ScoreMediaWindow(title, processName, info);
            if (score <= 0 && info.IsVideoSource && IsBrowserProcess(processName)) score = 1;
            if (score > bestScore)
            {
                bestScore = score;
                bestHwnd = hwnd;
                bestProcessName = processName;
            }

            return true;
        }, IntPtr.Zero);

        usedBrowser = IsBrowserProcess(bestProcessName);
        return bestHwnd != IntPtr.Zero && TryActivateWindow(bestHwnd);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        var sb = new System.Text.StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static int ScoreMediaWindow(string title, string processName, MediaInfo info)
    {
        int score = 0;
        string source = info.MediaSource ?? string.Empty;
        string track = NormalizeMediaTitle(info.CurrentTrack);
        string artist = NormalizeMediaTitle(info.CurrentArtist);
        string window = NormalizeMediaTitle(title);

        if (!string.IsNullOrWhiteSpace(source) && title.Contains(source, StringComparison.OrdinalIgnoreCase)) score += 80;
        if (!string.IsNullOrWhiteSpace(track) && window.Contains(track, StringComparison.OrdinalIgnoreCase)) score += 140;
        if (!string.IsNullOrWhiteSpace(artist) && artist is not "youtube" and not "browser" && window.Contains(artist, StringComparison.OrdinalIgnoreCase)) score += 70;

        if (source.Equals("YouTube", StringComparison.OrdinalIgnoreCase) && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (source.Equals("SoundCloud", StringComparison.OrdinalIgnoreCase) && title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (source.Equals("Facebook", StringComparison.OrdinalIgnoreCase) && title.Contains("Facebook", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (source.Equals("TikTok", StringComparison.OrdinalIgnoreCase) && title.Contains("TikTok", StringComparison.OrdinalIgnoreCase)) score += 90;
        if (source.Equals("Instagram", StringComparison.OrdinalIgnoreCase) && title.Contains("Instagram", StringComparison.OrdinalIgnoreCase)) score += 90;
        if ((source.Equals("Twitter", StringComparison.OrdinalIgnoreCase) || source.Equals("X", StringComparison.OrdinalIgnoreCase)) && (title.Contains("Twitter", StringComparison.OrdinalIgnoreCase) || title.Contains(" / X", StringComparison.OrdinalIgnoreCase))) score += 90;
        if (info.IsVideoSource && IsBrowserProcess(processName)) score += 25;

        return score;
    }

    private static string NormalizeMediaTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string normalized = Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+-\s+(youtube|google chrome|microsoft edge|mozilla firefox|brave|opera|vivaldi).*$", "", RegexOptions.IgnoreCase);
        return normalized;
    }

    private static bool IsBrowserProcess(string processName)
    {
        return processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("browser", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("arc", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("sidekick", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hwnd, SW_SHOW);
        }

        return SetForegroundWindow(hwnd);
    }

    private static IEnumerable<string> GetMediaProcessCandidates(MediaInfo info)
    {
        var candidates = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (candidates.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(value);
        }

        foreach (Match match in Regex.Matches(info.SourceAppId ?? string.Empty, @"([A-Za-z0-9_\-]+)\.exe", RegexOptions.IgnoreCase))
        {
            Add(match.Groups[1].Value);
        }

        string sourceAppId = info.SourceAppId ?? string.Empty;
        string mediaSource = info.MediaSource ?? string.Empty;

        if (sourceAppId.Contains("spotify", StringComparison.OrdinalIgnoreCase) ||
            mediaSource.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            Add("Spotify");
        }

        if (sourceAppId.Contains("applemusic", StringComparison.OrdinalIgnoreCase) ||
            sourceAppId.Contains("apple music", StringComparison.OrdinalIgnoreCase) ||
            mediaSource.Equals("Apple Music", StringComparison.OrdinalIgnoreCase))
        {
            Add("AppleMusic");
        }

        if (sourceAppId.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            sourceAppId.Contains("edge", StringComparison.OrdinalIgnoreCase))
        {
            Add("msedge");
        }

        if (sourceAppId.Contains("chrome", StringComparison.OrdinalIgnoreCase))
        {
            Add("chrome");
        }

        if (sourceAppId.Contains("firefox", StringComparison.OrdinalIgnoreCase))
        {
            Add("firefox");
        }

        if (sourceAppId.Contains("brave", StringComparison.OrdinalIgnoreCase))
        {
            Add("brave");
        }

        if (sourceAppId.Contains("opera", StringComparison.OrdinalIgnoreCase))
        {
            Add("opera");
        }

        if (sourceAppId.Contains("vivaldi", StringComparison.OrdinalIgnoreCase))
        {
            Add("vivaldi");
        }

        if (sourceAppId.Contains("arc", StringComparison.OrdinalIgnoreCase))
        {
            Add("arc");
        }

        if (sourceAppId.Contains("sidekick", StringComparison.OrdinalIgnoreCase))
        {
            Add("sidekick");
        }

        if (string.IsNullOrWhiteSpace(sourceAppId) &&
            mediaSource is "YouTube" or "SoundCloud" or "Browser" or "Facebook" or "TikTok" or "Instagram" or "Twitter")
        {
            Add("msedge");
            Add("chrome");
            Add("firefox");
            Add("brave");
            Add("opera");
        }

        if (mediaSource is "YouTube" or "SoundCloud" or "Browser" or "Facebook" or "TikTok" or "Instagram" or "Twitter")
        {
            Add("msedge");
            Add("chrome");
            Add("firefox");
            Add("brave");
            Add("opera");
            Add("vivaldi");
        }

        return candidates;
    }

    private void SendMediaKey(byte key)
    {
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    #endregion

    #region Volume Control

    private float _currentVolume = 0.5f;

    private void VolumeIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_mediaService.TryToggleCurrentSessionMute())
        {
            SyncVolumeFromActiveSession();
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

        _mediaService.TrySetCurrentSessionVolume(newVolume);
    }

    private void SyncVolumeFromActiveSession()
    {
        if (_isDraggingVolume) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isDraggingVolume) return;

            if (_mediaService.TryGetCurrentSessionVolume(out float volume, out bool isMuted))
            {
                _currentVolume = volume;
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
