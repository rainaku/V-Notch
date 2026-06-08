using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using VNotch.Services;

namespace VNotch.Controllers;

/// <summary>
/// Owns the webcam capture pipeline (WinRT <see cref="MediaCapture"/> + <see cref="MediaFrameReader"/>,
/// device selection, lifecycle serialization, 30 fps throttling and frame-buffer reuse) that previously
/// lived inline inside the MainWindow god-class.
///
/// This controller deliberately knows nothing about WPF, XAML elements or window layout. It surfaces
/// decoded BGRA frames via <see cref="FrameAvailable"/>; the owning view is responsible for rendering
/// them, for all animations and for deciding (via the <c>isContextValid</c> callback passed to
/// <see cref="StartAsync"/>) whether the surrounding UI is still in a state where the preview should run.
/// </summary>
public sealed class WebcamCaptureController : IDisposable
{
    private readonly object _lifecycleLock = new();

    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private MediaCapture? _initializingMediaCapture;

    private bool _isActive;
    private bool _starting;
    private bool _stopping;

    // Bumped on every start/stop. A start operation captures the token at entry and bails the moment
    // the field no longer matches it (i.e. a newer start/stop superseded it). Mirrors the old
    // _cameraPreviewFadeToken semantics so the view's fade animations stay in lockstep.
    private int _fadeToken;

    // ─── Performance: frame throttling & buffer reuse ───
    private long _lastFrameTimestamp;
    private const long FrameIntervalTicks = 333_333; // ~30fps cap (10_000_000 / 30)
    private byte[]? _frameBuffer;
    private int _frameBufferSize;

    /// <summary>True between a successful start and the next stop.</summary>
    public bool IsActive => _isActive;

    public bool IsStarting => _starting;
    public bool IsStopping => _stopping;

    /// <summary>True once a frame reader is attached (i.e. fully started, not just initializing).</summary>
    public bool HasReader => _frameReader != null;

    /// <summary>Current fade/generation token. Increments on every start and stop.</summary>
    public int FadeToken => _fadeToken;

    /// <summary>
    /// True while any capture resource is alive or in flight. Used by the view to decide whether a
    /// view-exit must tear the preview down before resetting layout.
    /// </summary>
    public bool IsLifecycleActive
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _isActive
                    || _starting
                    || _stopping
                    || _frameReader != null
                    || _mediaCapture != null
                    || _initializingMediaCapture != null;
            }
        }
    }

    /// <summary>Raised on a background thread with a reused BGRA buffer, width and height per frame.</summary>
    public event Action<byte[], int, int>? FrameAvailable;

    /// <summary>Increments and returns the fade token (used by the view when priming a fresh fade).</summary>
    public int NextFadeToken() => ++_fadeToken;

    /// <summary>
    /// Initializes the camera and starts streaming frames. Returns an error message on failure, or
    /// <c>null</c> on success or when the start was cancelled because <paramref name="isContextValid"/>
    /// became false. Faithfully preserves the original inline cancellation/race handling.
    /// </summary>
    /// <param name="deviceId">Preferred camera device id (MediaFrameSourceGroup id), or null/empty for default.</param>
    /// <param name="isContextValid">Callback returning whether the surrounding UI is still in a state that wants the preview.</param>
    public async Task<string?> StartAsync(string? deviceId, Func<bool> isContextValid)
    {
        if (_isActive && _frameReader != null) return null;
        if (_starting) return null;

        _starting = true;
        _isActive = true;
        _lastFrameTimestamp = 0;
        int startToken = ++_fadeToken;

        bool StillValid() => startToken == _fadeToken && _isActive && isContextValid();

        try
        {
            var (mediaCapture, frameReader, errorMsg) = await Task.Run(async () =>
            {
                MediaCapture? capture = null;
                try
                {
                    var groups = await MediaFrameSourceGroup.FindAllAsync();
                    MediaFrameSourceGroup? group = null;

                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        group = groups.FirstOrDefault(g => g.Id == deviceId &&
                            g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));
                    }

                    group ??= groups.FirstOrDefault(g =>
                        g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));

                    if (group == null)
                        return ((MediaCapture?)null, (MediaFrameReader?)null, "Cannot detect camera device");

                    capture = new MediaCapture();
                    lock (_lifecycleLock)
                    {
                        _initializingMediaCapture = capture;
                    }

                    var initSettings = new MediaCaptureInitializationSettings
                    {
                        SourceGroup = group,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                        StreamingCaptureMode = StreamingCaptureMode.Video
                    };

                    await capture.InitializeAsync(initSettings);
                    if (!StillValid())
                    {
                        lock (_lifecycleLock)
                        {
                            if (ReferenceEquals(_initializingMediaCapture, capture))
                            {
                                _initializingMediaCapture = null;
                            }
                        }
                        capture.Dispose();
                        return ((MediaCapture?)null, (MediaFrameReader?)null, (string?)null);
                    }

                    var colorSource = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
                    if (colorSource == null)
                    {
                        lock (_lifecycleLock)
                        {
                            if (ReferenceEquals(_initializingMediaCapture, capture))
                            {
                                _initializingMediaCapture = null;
                            }
                        }
                        capture.Dispose();
                        return ((MediaCapture?)null, (MediaFrameReader?)null, "Cannot detect color source");
                    }

                    var reader = await capture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
                    lock (_lifecycleLock)
                    {
                        if (ReferenceEquals(_initializingMediaCapture, capture))
                        {
                            _initializingMediaCapture = null;
                        }
                    }
                    return (capture, reader, (string?)null);
                }
                catch (Exception ex)
                {
                    if (capture != null)
                    {
                        lock (_lifecycleLock)
                        {
                            if (ReferenceEquals(_initializingMediaCapture, capture))
                            {
                                _initializingMediaCapture = null;
                            }
                        }
                        try { capture.Dispose(); } catch { }
                    }
                    return ((MediaCapture?)null, (MediaFrameReader?)null, ex.Message);
                }
            });

            if (!StillValid())
            {
                await DisposeResourcesAsync(frameReader, mediaCapture);
                return null;
            }

            if (mediaCapture == null || frameReader == null)
            {
                return errorMsg ?? "Cannot detect camera device";
            }

            _mediaCapture = mediaCapture;
            _frameReader = frameReader;
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_initializingMediaCapture, mediaCapture))
                {
                    _initializingMediaCapture = null;
                }
            }
            _frameReader.FrameArrived += OnFrameArrived;
            await frameReader.StartAsync();

            if (!StillValid())
            {
                frameReader.FrameArrived -= OnFrameArrived;
                if (ReferenceEquals(_frameReader, frameReader))
                {
                    _frameReader = null;
                }
                if (ReferenceEquals(_mediaCapture, mediaCapture))
                {
                    _mediaCapture = null;
                }
                await DisposeResourcesAsync(frameReader, mediaCapture);
            }

            return null;
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// Immediate, animation-free teardown (mirrors the old StopCameraPreviewSafe core): resets all
    /// volatile flags/buffers, bumps the fade token and detaches the frame handler at once, returning the
    /// live reader/capture (+ any in-flight initializing capture) for the caller to dispose.
    /// </summary>
    public (MediaFrameReader? reader, MediaCapture? capture, MediaCapture? initializing) DetachForSafeStop()
    {
        _isActive = false;
        _starting = false;
        _stopping = false;
        _fadeToken++;
        _frameBuffer = null;
        _frameBufferSize = 0;

        MediaCapture? initializingCapture;
        lock (_lifecycleLock)
        {
            initializingCapture = _initializingMediaCapture;
            _initializingMediaCapture = null;
        }

        var reader = _frameReader;
        var capture = _mediaCapture;
        _frameReader = null;
        _mediaCapture = null;

        if (reader != null)
        {
            reader.FrameArrived -= OnFrameArrived;
        }

        return (reader, capture, initializingCapture);
    }

    /// <summary>
    /// Begins a graceful (animated) stop: if not already stopping, marks stopping + inactive, clears the
    /// frame buffer, bumps the fade token and detaches/nulls the live reader+capture so frame flow stops
    /// immediately. The caller animates the fade-out, then disposes the returned resources off-thread and
    /// finally calls <see cref="EndGracefulStop"/>. Returns false (no-op) if a stop is already in progress.
    /// </summary>
    public bool TryBeginGracefulStop(out int fadeToken, out MediaFrameReader? reader, out MediaCapture? capture)
    {
        fadeToken = _fadeToken;
        reader = null;
        capture = null;

        if (_stopping) return false;
        _stopping = true;
        _isActive = false;
        _frameBuffer = null;
        _frameBufferSize = 0;
        fadeToken = ++_fadeToken;

        reader = _frameReader;
        capture = _mediaCapture;
        _frameReader = null;
        _mediaCapture = null;

        if (reader != null)
        {
            reader.FrameArrived -= OnFrameArrived;
        }

        return true;
    }

    /// <summary>Ends a graceful stop started by <see cref="TryBeginGracefulStop"/>.</summary>
    public void EndGracefulStop() => _stopping = false;

    public static async Task DisposeResourcesAsync(MediaFrameReader? reader, MediaCapture? capture)
    {
        if (reader != null)
        {
            try { await reader.StopAsync(); }
            catch { /* Reader may already be stopped/disposed when cancelling an in-flight start. */ }
            try { reader.Dispose(); } catch { }
        }

        try { capture?.Dispose(); } catch { }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // ─── Frame throttle: skip frames if we're rendering faster than 30fps ───
        long now = Stopwatch.GetTimestamp();
        if (now - _lastFrameTimestamp < FrameIntervalTicks)
            return;
        _lastFrameTimestamp = now;

        using var frame = sender.TryAcquireLatestFrame();
        if (frame?.VideoMediaFrame?.SoftwareBitmap == null) return;

        var softwareBitmap = frame.VideoMediaFrame.SoftwareBitmap;

        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        int width = softwareBitmap.PixelWidth;
        int height = softwareBitmap.PixelHeight;
        int requiredSize = width * height * 4;

        // Reuse buffer to avoid GC pressure
        if (_frameBuffer == null || _frameBufferSize < requiredSize)
        {
            _frameBuffer = new byte[requiredSize];
            _frameBufferSize = requiredSize;
        }

        softwareBitmap.CopyToBuffer(_frameBuffer.AsBuffer());
        softwareBitmap.Dispose();

        FrameAvailable?.Invoke(_frameBuffer, width, height);
    }

    public void Dispose()
    {
        var (reader, capture, initializing) = DetachForSafeStop();
        _ = DisposeResourcesAsync(reader, capture);
        if (initializing != null && !ReferenceEquals(initializing, capture))
        {
            _ = DisposeResourcesAsync(null, initializing);
        }
    }
}
