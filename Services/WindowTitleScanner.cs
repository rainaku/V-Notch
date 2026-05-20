using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace VNotch.Services;

public interface IWindowTitleScanner
{
    List<string> GetAllWindowTitles(bool isThrottled);
    string? TryGetBrowserUrl();
    /// <summary>
    /// Scans ALL visible browser windows (not just foreground) to find a media URL.
    /// Returns the first YouTube or SoundCloud URL found, or null.
    /// </summary>
    string? TryGetMediaUrlFromAnyBrowser();

    /// <summary>
    /// Drops the cached browser URL/media-URL results so the next call always
    /// performs a fresh UI Automation walk. Call this when you know the active
    /// media changed (e.g. SMTC fired a new track) so a stale URL from the
    /// previous track does not leak into the next thumbnail lookup.
    /// </summary>
    void InvalidateUrlCaches();
}

public sealed class WindowTitleScanner : IWindowTitleScanner
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static readonly string[] _platformKeywords =
    {
        "spotify", "youtube", "soundcloud", "facebook", "tiktok", "instagram", "twitter", " / x", "apple music", "apple", "music"
    };

    private static readonly string[] _browserProcessNames =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi"
    };

    private readonly object _cacheLock = new();
    private List<string> _cachedWindowTitles = new();
    private DateTime _lastWindowEnumTime = DateTime.MinValue;

    public List<string> GetAllWindowTitles(bool isThrottled)
    {
        lock (_cacheLock)
        {
            int cacheDurationMs = isThrottled ? 300 : 700;
            if ((DateTime.Now - _lastWindowEnumTime).TotalMilliseconds < cacheDurationMs)
            {
                return _cachedWindowTitles;
            }

            var titles = new List<string>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                int length = GetWindowTextLength(hWnd);
                if (length == 0)
                {
                    return true;
                }

                var sb = new StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();

                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                string lowerTitle = title.ToLowerInvariant();
                foreach (var keyword in _platformKeywords)
                {
                    if (lowerTitle.Contains(keyword, StringComparison.Ordinal))
                    {
                        titles.Add(title);
                        break;
                    }
                }

                return true;
            }, IntPtr.Zero);

            _cachedWindowTitles = titles;
            _lastWindowEnumTime = DateTime.Now;
            return titles;
        }
    }

    // ─── Browser URL extraction via UI Automation ───

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private string? _cachedBrowserUrl;
    private DateTime _lastBrowserUrlTime = DateTime.MinValue;

    private string? _cachedAnyBrowserMediaUrl;
    private DateTime _lastAnyBrowserMediaUrlTime = DateTime.MinValue;

    public string? TryGetBrowserUrl()
    {
        lock (_cacheLock)
        {
            if ((DateTime.Now - _lastBrowserUrlTime).TotalMilliseconds < 1000)
                return _cachedBrowserUrl;

            _cachedBrowserUrl = ExtractBrowserUrlCore();
            _lastBrowserUrlTime = DateTime.Now;
            return _cachedBrowserUrl;
        }
    }

    public string? TryGetMediaUrlFromAnyBrowser()
    {
        lock (_cacheLock)
        {
            // Cache successful lookups for 1.5s; cache *misses* for only 400ms
            // so a tab that just started playing in the background is picked
            // up quickly when the user opens the notch a moment later.
            int ttlMs = !string.IsNullOrEmpty(_cachedAnyBrowserMediaUrl) ? 1500 : 400;
            if ((DateTime.Now - _lastAnyBrowserMediaUrlTime).TotalMilliseconds < ttlMs)
                return _cachedAnyBrowserMediaUrl;

            _cachedAnyBrowserMediaUrl = ExtractMediaUrlFromAllBrowserWindows();
            _lastAnyBrowserMediaUrlTime = DateTime.Now;
            return _cachedAnyBrowserMediaUrl;
        }
    }

    public void InvalidateUrlCaches()
    {
        lock (_cacheLock)
        {
            _cachedBrowserUrl = null;
            _lastBrowserUrlTime = DateTime.MinValue;
            _cachedAnyBrowserMediaUrl = null;
            _lastAnyBrowserMediaUrlTime = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Scans ALL visible browser windows to find a YouTube or SoundCloud URL.
    /// Unlike TryGetBrowserUrl which only checks the foreground window,
    /// this enumerates all top-level windows and checks each browser window's
    /// address bar AND tab strip (Chromium-based browsers expose per-tab URLs
    /// via UI Automation HelpText, so background tabs are also covered).
    /// </summary>
    private string? ExtractMediaUrlFromAllBrowserWindows()
    {
        // First try the foreground window (fastest path). If it already shows
        // a media URL, we're done.
        var foregroundUrl = ExtractBrowserUrlCore();
        if (!string.IsNullOrEmpty(foregroundUrl) && IsMediaUrl(foregroundUrl))
            return foregroundUrl;

        string? foundUrl = null;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return true;

            string? processName = null;
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                processName = proc.ProcessName.ToLowerInvariant();
            }
            catch { return true; }

            bool isBrowser = false;
            foreach (var name in _browserProcessNames)
            {
                if (processName.Contains(name))
                {
                    isBrowser = true;
                    break;
                }
            }
            if (!isBrowser) return true;

            // ExtractUrlFromWindowHandle now also walks the tab strip when the
            // address bar isn't a media URL, so a background YouTube tab in
            // this window will still be discovered.
            var url = ExtractUrlFromWindowHandle(hWnd, processName);
            if (!string.IsNullOrEmpty(url) && IsMediaUrl(url))
            {
                foundUrl = url;
                return false; // Stop enumeration — found a media URL
            }

            return true;
        }, IntPtr.Zero);

        return foundUrl;
    }

    /// <summary>
    /// Extracts the URL from a specific browser window handle using UI Automation.
    /// Falls back to scanning <see cref="TabItem"/> elements (Chromium exposes
    /// each tab's URL via <c>HelpText</c>) so background tabs can also be
    /// inspected, not just the active one.
    /// </summary>
    private static string? ExtractUrlFromWindowHandle(IntPtr hwnd, string processName)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if (element == null) return null;

            AutomationElement? addressBar = null;

            if (processName.Contains("firefox"))
            {
                addressBar = element.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
            }
            else
            {
                // Chromium-based: find Edit control (address bar)
                var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var edits = element.FindAll(TreeScope.Descendants, editCondition);

                foreach (AutomationElement edit in edits)
                {
                    try
                    {
                        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
                        {
                            var valuePattern = (ValuePattern)pattern;
                            string val = valuePattern.Current.Value ?? "";

                            if (val.Contains("youtube.com/watch") || val.Contains("youtu.be/") ||
                                val.Contains("soundcloud.com/") ||
                                val.StartsWith("http://") || val.StartsWith("https://") ||
                                val.Contains(".com/") || val.Contains(".org/"))
                            {
                                addressBar = edit;
                                break;
                            }
                        }
                    }
                    catch { continue; }
                }
            }

            if (addressBar != null &&
                addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object? urlPattern))
            {
                var vp = (ValuePattern)urlPattern;
                string url = vp.Current.Value ?? "";

                if (!url.StartsWith("http") && url.Contains("."))
                    url = "https://" + url;

                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }

            // ── Fallback: scan tabs for a media URL ──────────────────────────
            // Address bar only reflects the *active* tab. When the YouTube /
            // SoundCloud tab is in the background, address bar will be a
            // different URL, so we additionally walk the tab strip.
            // Chromium-based browsers expose the per-tab URL via
            // AutomationElement.HelpText. Firefox also exposes it on tab
            // descendants.
            var tabUrl = TryFindMediaUrlInTabs(element);
            if (!string.IsNullOrEmpty(tabUrl))
                return tabUrl;
        }
        catch { }

        return null;
    }

    private static string? TryFindMediaUrlInTabs(AutomationElement root)
    {
        try
        {
            var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            var tabs = root.FindAll(TreeScope.Descendants, tabCondition);
            if (tabs == null || tabs.Count == 0) return null;

            foreach (AutomationElement tab in tabs)
            {
                string? candidate = TryReadTabUrl(tab);
                if (!string.IsNullOrEmpty(candidate) && IsMediaUrl(candidate))
                {
                    return candidate;
                }
            }
        }
        catch { }
        return null;
    }

    private static string? TryReadTabUrl(AutomationElement tab)
    {
        try
        {
            // Chromium puts the full URL in HelpText for accessibility.
            string help = tab.Current.HelpText ?? string.Empty;
            if (LooksLikeUrl(help)) return NormalizeUrl(help);

            // Some Chromium variants and Firefox expose URL as the Name on a
            // descendant Document or as the Value on a child Hyperlink.
            string name = tab.Current.Name ?? string.Empty;
            if (LooksLikeUrl(name)) return NormalizeUrl(name);

            // Firefox-specific: tabs may have a child element with AutomationId
            // "urlbar-input" containing the URL of the corresponding tab is not
            // exposed, but a "tab" element's Description sometimes carries it.
            // Best effort — scan one level of descendants for any element with
            // a media URL in its Name/HelpText/Value.
            var subtree = tab.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.IsControlElementProperty, true));

            foreach (AutomationElement child in subtree)
            {
                string childHelp = child.Current.HelpText ?? string.Empty;
                if (LooksLikeUrl(childHelp)) return NormalizeUrl(childHelp);

                string childName = child.Current.Name ?? string.Empty;
                if (LooksLikeUrl(childName)) return NormalizeUrl(childName);

                if (child.TryGetCurrentPattern(ValuePattern.Pattern, out object? p))
                {
                    string v = ((ValuePattern)p).Current.Value ?? string.Empty;
                    if (LooksLikeUrl(v)) return NormalizeUrl(v);
                }
            }
        }
        catch { }
        return null;
    }

    private static bool LooksLikeUrl(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUrl(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("http", StringComparison.OrdinalIgnoreCase) && s.Contains('.'))
        {
            s = "https://" + s;
        }
        return s;
    }

    /// <summary>
    /// Checks if a URL is a YouTube or SoundCloud media URL.
    /// </summary>
    private static bool IsMediaUrl(string url)
    {
        return url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("music.youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("soundcloud.com/", StringComparison.OrdinalIgnoreCase);
    }

    private string? ExtractBrowserUrlCore()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;

            string? processName = null;
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                processName = proc.ProcessName.ToLowerInvariant();
            }
            catch { return null; }

            // Only attempt for known browsers
            bool isBrowser = false;
            foreach (var name in _browserProcessNames)
            {
                if (processName.Contains(name))
                {
                    isBrowser = true;
                    break;
                }
            }

            if (!isBrowser) return null;

            return ExtractUrlFromWindowHandle(hwnd, processName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowTitleScanner] Browser URL extraction failed: {ex.Message}");
        }

        return null;
    }
}
