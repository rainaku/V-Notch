using System;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.Wpf;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace VNotch.Services;

public class YouTubeVideoInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
}

public class YouTubeService : IDisposable
{
    private ChromiumWebBrowser? _browser;
    private bool _isInitialized;
    private readonly TaskCompletionSource<bool> _initTcs = new();

    public YouTubeService()
    {
        InitializeBrowser();
    }

    private void InitializeBrowser()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _browser = new ChromiumWebBrowser();
            _browser.Width = 10;
            _browser.Height = 10;
            _browser.Visibility = Visibility.Collapsed;
            
            _browser.IsBrowserInitializedChanged += (s, e) =>
            {
                if (_browser.IsBrowserInitialized)
                {
                    _isInitialized = true;
                    _initTcs.TrySetResult(true);
                }
            };

            // Add to hidden container if needed, but for off-screen it's fine
        });
    }

    public async Task<YouTubeVideoInfo?> SearchFirstVideoAsync(string query)
    {
        if (!_isInitialized) await _initTcs.Task;
        if (_browser == null) return null;

        string searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}";
        
        // Load the page
        await _browser.LoadUrlAsync(searchUrl);
        
        // Polling wait for YouTube's heavy JS to render results
        bool success = false;
        for (int i = 0; i < 30; i++) // Max 6 seconds
        {
            var checkResponse = await _browser.EvaluateScriptAsync(@"
                (function() {
                    return (window.ytInitialData && window.ytInitialData.contents) || 
                           (document.querySelector('ytd-video-renderer, ytd-grid-video-renderer') !== null);
                })();
            ");
            if (checkResponse.Success && checkResponse.Result is bool b && b)
            {
                success = true;
                break;
            }
            await Task.Delay(200);
        }
        
        if (!success) await Task.Delay(400); // One last short breath

        string script = @"
            (function() {
                try {
                    const data = window.ytInitialData;
                    let contents = null;
                    
                    // Try different paths for contents in ytInitialData
                    if (data && data.contents && data.contents.twoColumnSearchResultsRenderer) {
                        contents = data.contents.twoColumnSearchResultsRenderer.primaryContents.sectionListRenderer.contents[0].itemSectionRenderer.contents;
                    }

                    if (contents) {
                        const firstVideo = contents.find(c => c.videoRenderer || c.playlistVideoRenderer);
                        
                        if (firstVideo) {
                            const vr = firstVideo.videoRenderer || firstVideo.playlistVideoRenderer;
                            return {
                                Id: vr.videoId,
                                Title: vr.title.runs[0].text,
                                Author: vr.ownerText ? vr.ownerText.runs[0].text : (vr.shortBylineText ? vr.shortBylineText.runs[0].text : ''),
                                ThumbnailUrl: vr.thumbnail.thumbnails[vr.thumbnail.thumbnails.length - 1].url,
                                DurationText: vr.lengthText ? (vr.lengthText.simpleText || vr.lengthText.runs[0].text) : '0:00'
                            };
                        }
                    }
                } catch (e) { console.error('ytInitialData error:', e); }

                // Fallback to DOM parsing
                try {
                    const videoEl = document.querySelector('ytd-video-renderer, ytd-grid-video-renderer');
                    if (videoEl) {
                        const titleLink = videoEl.querySelector('a#video-title');
                        const thumbImg = videoEl.querySelector('img');
                        
                        if (titleLink) {
                            const href = titleLink.href;
                            const url = new URL(href);
                            const videoId = url.searchParams.get('v');
                            
                            return {
                                Id: videoId,
                                Title: titleLink.innerText.trim(),
                                Author: '', 
                                ThumbnailUrl: thumbImg ? thumbImg.src : (videoId ? `https://i.ytimg.com/vi/${videoId}/mqdefault.jpg` : ''),
                                DurationText: '0:00'
                            };
                        }
                    }
                } catch (e) { console.error('DOM fallback error:', e); }
                return null;
            })();
        ";

        var response = await _browser.EvaluateScriptAsync(script);
        if (response.Success && response.Result != null)
        {
            var dict = response.Result as IDictionary<string, object>;
            if (dict != null)
            {
                var info = new YouTubeVideoInfo
                {
                    Id = dict.ContainsKey("Id") ? dict["Id"]?.ToString() ?? "" : "",
                    Title = dict.ContainsKey("Title") ? dict["Title"]?.ToString() ?? "" : "",
                    Author = dict.ContainsKey("Author") ? dict["Author"]?.ToString() ?? "" : "",
                    ThumbnailUrl = dict.ContainsKey("ThumbnailUrl") ? dict["ThumbnailUrl"]?.ToString() ?? "" : ""
                };

                // Ensure high quality thumbnail URL
                if (!string.IsNullOrEmpty(info.Id) && (string.IsNullOrEmpty(info.ThumbnailUrl) || info.ThumbnailUrl.Contains("googleusercontent")))
                {
                    info.ThumbnailUrl = $"https://i.ytimg.com/vi/{info.Id}/hqdefault.jpg";
                }
                else if (!string.IsNullOrEmpty(info.ThumbnailUrl) && info.ThumbnailUrl.StartsWith("//"))
                {
                    info.ThumbnailUrl = "https:" + info.ThumbnailUrl;
                }

                if (dict.ContainsKey("DurationText"))
                {
                    string dText = dict["DurationText"]?.ToString() ?? "";
                    info.Duration = ParseDuration(dText);
                }

                return info;
            }
        }

        return null;
    }

    private TimeSpan ParseDuration(string text)
    {
        if (string.IsNullOrEmpty(text)) return TimeSpan.Zero;
        var parts = text.Split(':').Reverse().ToArray();
        double seconds = 0;
        if (parts.Length > 0 && double.TryParse(parts[0], out double s)) seconds += s;
        if (parts.Length > 1 && double.TryParse(parts[1], out double m)) seconds += m * 60;
        if (parts.Length > 2 && double.TryParse(parts[2], out double h)) seconds += h * 3600;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        _browser?.Dispose();
    }
}
