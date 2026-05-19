using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace VNotch.Services;

public interface IWindowTitleScanner
{
    List<string> GetAllWindowTitles(bool isThrottled);
    string? TryGetBrowserUrl();
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
            bool isBrowser = processName.Contains("chrome") ||
                            processName.Contains("msedge") ||
                            processName.Contains("firefox") ||
                            processName.Contains("brave") ||
                            processName.Contains("opera") ||
                            processName.Contains("vivaldi");

            if (!isBrowser) return null;

            var element = AutomationElement.FromHandle(hwnd);
            if (element == null) return null;

            // Strategy 1: Find address bar by ControlType.Edit with URL-like value
            // Chrome/Edge/Brave/Vivaldi use a single Edit control for the address bar
            AutomationElement? addressBar = null;

            if (processName.Contains("firefox"))
            {
                // Firefox: the URL bar has AutomationId "urlbar-input"
                addressBar = element.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
            }
            else
            {
                // Chromium-based: find Edit control (address bar)
                // The address bar is typically the first Edit with a URL-like value
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

                            // Check if it looks like a URL
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

            if (addressBar == null) return null;

            if (addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object? urlPattern))
            {
                var vp = (ValuePattern)urlPattern;
                string url = vp.Current.Value ?? "";

                // Normalize: some browsers strip the protocol
                if (!url.StartsWith("http") && url.Contains("."))
                    url = "https://" + url;

                return string.IsNullOrWhiteSpace(url) ? null : url;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowTitleScanner] Browser URL extraction failed: {ex.Message}");
        }

        return null;
    }
}
