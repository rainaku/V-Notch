using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CefSharp;
using CefSharp.Wpf;

namespace VNotch.Controls;

public partial class YouTubePlayerControl : UserControl
{
    private bool _isPlayerReady = false;
    private string? _pendingVideoId;

    public event EventHandler<double>? PositionChanged;
    public event EventHandler<double>? DurationChanged;
    public event EventHandler<string>? VideoStateChanged;

    public YouTubePlayerControl()
    {
        InitializeComponent();
        
        Browser.JavascriptObjectRepository.Settings.LegacyBindingEnabled = true;
        Browser.JavascriptObjectRepository.Register("cefCallback", new YouTubeCallbackProxy(this));
        
        Browser.FrameLoadEnd += OnFrameLoadEnd;
        
        LoadPlayer();
    }

    private void LoadPlayer()
    {
        string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "youtube_player.html");
        if (File.Exists(htmlPath))
        {
            Browser.LoadUrl(htmlPath);
        }
    }

    private void OnFrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
    {
        if (e.Frame.IsMain)
        {
            Dispatcher.Invoke(() =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                Browser.Visibility = Visibility.Visible;
            });
        }
    }

    public void LoadVideo(string videoId)
    {
        if (_isPlayerReady)
        {
            Browser.ExecuteScriptAsync($"loadVideo('{videoId}')");
        }
        else
        {
            _pendingVideoId = videoId;
        }
    }

    public void Play() => Browser.ExecuteScriptAsync("playVideo()");
    public void Pause() => Browser.ExecuteScriptAsync("pauseVideo()");
    public void SeekTo(double seconds) => Browser.ExecuteScriptAsync($"seekTo({seconds})");
    public void SetVolume(int volume) => Browser.ExecuteScriptAsync($"setVolume({volume})");

    private class YouTubeCallbackProxy
    {
        private readonly YouTubePlayerControl _parent;
        public YouTubeCallbackProxy(YouTubePlayerControl parent) => _parent = parent;

        public void onReady()
        {
            _parent._isPlayerReady = true;
            if (_parent._pendingVideoId != null)
            {
                _parent.Dispatcher.Invoke(() => _parent.LoadVideo(_parent._pendingVideoId));
                _parent._pendingVideoId = null;
            }
        }

        public void onStateChange(int state, double position, double duration)
        {
            string stateStr = state switch
            {
                0 => "ended",
                1 => "playing",
                2 => "paused",
                3 => "buffering",
                5 => "cued",
                _ => "unknown"
            };

            _parent.Dispatcher.Invoke(() =>
            {
                _parent.VideoStateChanged?.Invoke(_parent, stateStr);
                _parent.PositionChanged?.Invoke(_parent, position);
                _parent.DurationChanged?.Invoke(_parent, duration);
            });
        }

        public void onPositionUpdate(double position, double duration)
        {
            _parent.Dispatcher.Invoke(() =>
            {
                _parent.PositionChanged?.Invoke(_parent, position);
                _parent.DurationChanged?.Invoke(_parent, duration);
            });
        }

        public void onError(int code)
        {
            System.Diagnostics.Debug.WriteLine($"YouTube Player Error: {code}");
        }
    }
}
