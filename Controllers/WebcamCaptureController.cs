using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace VNotch.Controllers;

public sealed class WebcamCaptureController : IDisposable
{
    private readonly object _lifecycleLock = new();

    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private MediaCapture? _initializingMediaCapture;

    private bool _isActive;
    private bool _starting;
    private bool _stopping;

    private int _fadeToken;

    private long _lastFrameTimestamp;
    private const long FrameIntervalTicks = 333_333;

    private byte[] _frameBuffer = Array.Empty<byte>();
    private int _frameBufferInUse;
    public bool IsActive => _isActive;

    public bool IsStarting => _starting;
    public bool IsStopping => _stopping;

    public bool HasReader => _frameReader != null;

    public int FadeToken => _fadeToken;

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

    public event Action<byte[], int, int>? FrameAvailable;

    public void ReleaseFrameBuffer()
    {
        Volatile.Write(ref _frameBufferInUse, 0);
    }

    public int NextFadeToken() => ++_fadeToken;

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

    public (MediaFrameReader? reader, MediaCapture? capture, MediaCapture? initializing) DetachForSafeStop()
    {
        _isActive = false;
        _starting = false;
        _stopping = false;
        _fadeToken++;

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

    public bool TryBeginGracefulStop(out int fadeToken, out MediaFrameReader? reader, out MediaCapture? capture)
    {
        fadeToken = _fadeToken;
        reader = null;
        capture = null;

        if (_stopping) return false;
        _stopping = true;
        _isActive = false;
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

    public void EndGracefulStop() => _stopping = false;

    public static async Task DisposeResourcesAsync(MediaFrameReader? reader, MediaCapture? capture)
    {
        if (reader != null)
        {
            try { await reader.StopAsync(); }
            catch { }
            try { reader.Dispose(); } catch { }
        }

        try { capture?.Dispose(); } catch { }
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _lastFrameTimestamp < FrameIntervalTicks)
            return;
        _lastFrameTimestamp = now;

        // Keep at most one copied frame in flight. The UI consumes this buffer
        // asynchronously, so it must release the lease before capture can reuse it.
        if (Interlocked.CompareExchange(ref _frameBufferInUse, 1, 0) != 0)
        {
            return;
        }

        bool handedOff = false;
        SoftwareBitmap? convertedBitmap = null;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            if (frame?.VideoMediaFrame?.SoftwareBitmap == null) return;

            var softwareBitmap = frame.VideoMediaFrame.SoftwareBitmap;
            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                convertedBitmap = SoftwareBitmap.Convert(
                    softwareBitmap,
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
                softwareBitmap = convertedBitmap;
            }

            int width = softwareBitmap.PixelWidth;
            int height = softwareBitmap.PixelHeight;
            int requiredSize = checked(width * height * 4);

            if (_frameBuffer.Length < requiredSize)
            {
                _frameBuffer = GC.AllocateUninitializedArray<byte>(requiredSize);
            }

            softwareBitmap.CopyToBuffer(_frameBuffer.AsBuffer());

            var handler = FrameAvailable;
            if (handler == null) return;

            handler(_frameBuffer, width, height);
            handedOff = true;
        }
        finally
        {
            convertedBitmap?.Dispose();
            if (!handedOff)
            {
                ReleaseFrameBuffer();
            }
        }
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
