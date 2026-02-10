using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VNotch;

/// <summary>
/// Partial class for Marquee text scrolling logic
/// </summary>
public partial class MainWindow
{
    #region Marquee Fields

    private DispatcherTimer? _marqueeTimer;
    private double _titleScrollOffset = 0;
    private double _artistScrollOffset = 0;
    private double _titleScrollDistance = 0;
    private double _artistScrollDistance = 0;
    private bool _titleScrollForward = true;
    private bool _artistScrollForward = true;
    private DateTime _titlePauseUntil = DateTime.MinValue;
    private DateTime _artistPauseUntil = DateTime.MinValue;
    private string _lastTitleText = "";
    private string _lastArtistText = "";

    #endregion

    #region Marquee Timer

    private void InitializeMarqueeTimer()
    {
        if (_marqueeTimer == null)
        {
            _marqueeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _marqueeTimer.Tick += MarqueeTimer_Tick;
        }
    }
    
    private void MarqueeTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        
        // Update title scroll
        if (_titleScrollDistance > 0 && now > _titlePauseUntil)
        {
            if (_titleScrollForward)
            {
                _titleScrollOffset -= 1.0;
                if (_titleScrollOffset <= -_titleScrollDistance)
                {
                    _titleScrollOffset = -_titleScrollDistance;
                    _titleScrollForward = false;
                    _titlePauseUntil = now.AddSeconds(1.5);
                }
            }
            else
            {
                _titleScrollOffset += 1.5;
                if (_titleScrollOffset >= 0)
                {
                    _titleScrollOffset = 0;
                    _titleScrollForward = true;
                    _titlePauseUntil = now.AddSeconds(2);
                }
            }
            
            if (TrackTitle.RenderTransform is TranslateTransform titleTransform)
            {
                titleTransform.X = _titleScrollOffset;
            }
        }
        
        // Update artist scroll
        if (_artistScrollDistance > 0 && now > _artistPauseUntil)
        {
            if (_artistScrollForward)
            {
                _artistScrollOffset -= 1.0;
                if (_artistScrollOffset <= -_artistScrollDistance)
                {
                    _artistScrollOffset = -_artistScrollDistance;
                    _artistScrollForward = false;
                    _artistPauseUntil = now.AddSeconds(1.5);
                }
            }
            else
            {
                _artistScrollOffset += 1.5;
                if (_artistScrollOffset >= 0)
                {
                    _artistScrollOffset = 0;
                    _artistScrollForward = true;
                    _artistPauseUntil = now.AddSeconds(2);
                }
            }
            
            if (TrackArtist.RenderTransform is TranslateTransform artistTransform)
            {
                artistTransform.X = _artistScrollOffset;
            }
        }
    }

    #endregion

    #region Text Update with Marquee

    private void UpdateTitleText(string newText)
    {
        if (newText == _lastTitleText) return;
        _lastTitleText = newText;
        
        TrackTitle.Text = newText;
        
        _titleScrollOffset = 0;
        _titleScrollForward = true;
        _titlePauseUntil = DateTime.Now.AddSeconds(2);
        
        if (TrackTitle.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
        }
        
        TrackTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = TrackTitle.DesiredSize.Width;
        double containerWidth = TitleScrollContainer.Width > 0 ? TitleScrollContainer.Width : 250;
        
        if (textWidth > containerWidth)
        {
            _titleScrollDistance = textWidth - containerWidth + 15;
            InitializeMarqueeTimer();
            _marqueeTimer?.Start();
        }
        else
        {
            _titleScrollDistance = 0;
        }
    }
    
    private void UpdateArtistText(string newText)
    {
        if (newText == _lastArtistText) return;
        _lastArtistText = newText;
        
        TrackArtist.Text = newText;
        
        _artistScrollOffset = 0;
        _artistScrollForward = true;
        _artistPauseUntil = DateTime.Now.AddSeconds(2.5);
        
        if (TrackArtist.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
        }
        
        TrackArtist.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = TrackArtist.DesiredSize.Width;
        double containerWidth = ArtistScrollContainer.Width > 0 ? ArtistScrollContainer.Width : 250;
        
        if (textWidth > containerWidth)
        {
            _artistScrollDistance = textWidth - containerWidth + 15;
            InitializeMarqueeTimer();
            _marqueeTimer?.Start();
        }
        else
        {
            _artistScrollDistance = 0;
        }
    }

    #endregion
}
