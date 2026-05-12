using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using VNotch.Services;

namespace VNotch.Controllers;

/// <summary>
/// Manages camera lifecycle: initialization, frame reading, and cleanup.
/// Decoupled from UI — raises events with frame data for the view layer to render.
/// </summary>
public sealed class CameraPreviewController : IDisposable
{
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private bool _isActive;
    private bool _morphPending;
    private int _fadeToken;

    // ─── Public State ───

    public bool IsActive => _isActive;
    public bool IsMorphPending => _morphPending;
    public int FadeToken => _fadeToken;

    // ─── Events ───

    /// <summary>Raised when a new frame is available for rendering.</summary>
    public event Action<byte[], int, int>? FrameAvailable;

    /// <summary>Raised when camera initialization fails.</summary>
    public event Action<string>? CameraError;

    /// <summary>Raised when camera starts successfully and morph-in should begin.</summary>
    public event Action? CameraReady;

    /// <summary>Raised when camera is stopping (for UI to animate out).</summary>
    public event Action? CameraStopping;

    // ─── Lifecycle ───

    public void PrimeMorphIn()
    {
        _morphPending = true;
    }

    public void CompleteMorphIn()
    {
        _morphPending = false;
    }

    public int IncrementFadeToken()
    {
        return ++_fadeToken;
    }

    public async Task StartAsync()
    {
        if (_isActive && _frameReader != null) return;

        try
        {
            _fadeToken++;
            PrimeMorphIn();
            _isActive = true;

            _mediaCapture = new MediaCapture();

            var frameSourceGroups = await MediaFrameSourceGroup.FindAllAsync();
            var selectedGroup = frameSourceGroups.FirstOrDefault(g =>
                g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));

            if (selectedGroup == null)
                throw new Exception("Cannot detect camera device");

            var settings = new MediaCaptureInitializationSettings
            {
                SourceGroup = selectedGroup,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video
            };

            await _mediaCapture.InitializeAsync(settings);

            var colorSource = _mediaCapture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);

            if (colorSource == null)
                throw new Exception("Cannot detect color source");

            _frameReader = await _mediaCapture.CreateFrameReaderAsync(
                colorSource, MediaEncodingSubtypes.Bgra8);
            _frameReader.FrameArrived += OnFrameArrived;
            await _frameReader.StartAsync();

            CameraReady?.Invoke();
        }
        catch (Exception ex)
        {
            await StopAsync();
            CameraError?.Invoke(ex.Message);
        }
    }

    public async Task StopAsync()
    {
        _isActive = false;
        _morphPending = false;
        _fadeToken++;

        CameraStopping?.Invoke();

        if (_frameReader != null)
        {
            _frameReader.FrameArrived -= OnFrameArrived;
            await _frameReader.StopAsync();
            _frameReader.Dispose();
            _frameReader = null;
        }

        if (_mediaCapture != null)
        {
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        if (frame?.VideoMediaFrame?.SoftwareBitmap == null) return;

        var softwareBitmap = frame.VideoMediaFrame.SoftwareBitmap;

        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            softwareBitmap = SoftwareBitmap.Convert(
                softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        int width = softwareBitmap.PixelWidth;
        int height = softwareBitmap.PixelHeight;
        var buffer = new byte[width * height * 4];

        softwareBitmap.CopyToBuffer(buffer.AsBuffer());
        softwareBitmap.Dispose();

        FrameAvailable?.Invoke(buffer, width, height);
    }

    // ─── Dispose ───

    public void Dispose()
    {
        _isActive = false;
        _morphPending = false;

        if (_frameReader != null)
        {
            _frameReader.FrameArrived -= OnFrameArrived;
            _frameReader.Dispose();
            _frameReader = null;
        }

        if (_mediaCapture != null)
        {
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }
    }
}
