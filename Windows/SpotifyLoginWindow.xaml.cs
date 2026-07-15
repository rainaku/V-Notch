using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VNotch.Services;

namespace VNotch;

public partial class SpotifyLoginWindow : Window
{
    private readonly DispatcherTimer _cookieTimer;
    private bool _cookieCheckInProgress;
    private string? _userDataFolder;

    public string SpotifySpDc { get; private set; } = "";

    public SpotifyLoginWindow()
    {
        InitializeComponent();

        Title = Loc.Get("spotifyLogin.title");
        HeadingText.Text = Loc.Get("spotifyLogin.heading");
        HintText.Text = Loc.Get("spotifyLogin.hint");
        StatusText.Text = Loc.Get("spotifyLogin.opening");
        CancelButton.Content = Loc.Get("spotifyLogin.cancel");

        _cookieTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _cookieTimer.Tick += CookieTimer_Tick;
        Loaded += SpotifyLoginWindow_Loaded;
    }

    private async void SpotifyLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SpotifyLoginWindow_Loaded;
        try
        {
            _userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "V-Notch",
                "SpotifyWebView2",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_userDataFolder);

            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await SpotifyWebView.EnsureCoreWebView2Async(environment);

            SpotifyWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            SpotifyWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            SpotifyWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            SpotifyWebView.CoreWebView2.Navigate(
                "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F");
            _cookieTimer.Start();
            await CheckForSpotifySessionAsync();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("SPOTIFY-LOGIN", $"Unable to initialize Spotify login: {ex.Message}");
            StatusText.Text = Loc.Get("spotifyLogin.failed");
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
    }

    private async void CoreWebView2_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        await CheckForSpotifySessionAsync();
    }

    private async void CookieTimer_Tick(object? sender, EventArgs e)
    {
        await CheckForSpotifySessionAsync();
    }

    private async Task CheckForSpotifySessionAsync()
    {
        if (_cookieCheckInProgress || SpotifyWebView.CoreWebView2 == null)
            return;

        _cookieCheckInProgress = true;
        try
        {
            IReadOnlyList<CoreWebView2Cookie> cookies =
                await SpotifyWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://open.spotify.com/");
            CoreWebView2Cookie? session = cookies.FirstOrDefault(cookie =>
                cookie.Name.Equals("sp_dc", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(cookie.Value));
            if (session == null)
                return;

            SpotifySpDc = session.Value;
            _cookieTimer.Stop();
            StatusText.Text = Loc.Get("spotifyLogin.connected");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Debug("SPOTIFY-LOGIN", () => $"Spotify cookie check failed: {ex.Message}");
        }
        finally
        {
            _cookieCheckInProgress = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cookieTimer.Stop();
        if (SpotifyWebView.CoreWebView2 != null)
        {
            SpotifyWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            SpotifyWebView.CoreWebView2.CookieManager.DeleteAllCookies();
        }
        SpotifyWebView.Dispose();
        if (_userDataFolder != null)
            DeleteTemporaryProfileAsync(_userDataFolder).SafeFireAndForget("SPOTIFY-LOGIN-CLEANUP");
    }

    private static async Task DeleteTemporaryProfileAsync(string userDataFolder)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(userDataFolder))
                    Directory.Delete(userDataFolder, recursive: true);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }
}
