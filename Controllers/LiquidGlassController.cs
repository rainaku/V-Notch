using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>
/// Renders the live "liquid glass" backdrop by capturing the desktop behind the
/// notch, refracting it and frosting it. All heavy work (GDI screen capture,
/// refraction gather, box blur) runs on a dedicated background thread; the UI
/// thread only does the final <see cref="WriteableBitmap.WritePixels(Int32Rect, Array, int, int)"/>.
/// Processing happens at a reduced resolution (see <see cref="ProcessScale"/>) and
/// WPF stretches the result back up (the host Image uses Stretch=Fill), which the
/// frosted look hides while cutting per-frame cost roughly 3-4x.
/// </summary>
public sealed class LiquidGlassController
{
    public readonly record struct CaptureRegion(int X, int Y, int Width, int Height);

    public struct GlassParams
    {
        public double Refraction;
        public double ChromaticAberration;
        public double Distortion;
        public double ZRadius;
        public double Saturation;
        public double Brightness;
        public int BevelMode;

        public static GlassParams Default => new()
        {
            Refraction = 1.0,
            ChromaticAberration = 0.32,
            Distortion = 0.0,
            ZRadius = 0.20,
            Saturation = 0.0,
            Brightness = 0.0,
            BevelMode = 0
        };
    }

    private const int MaxWidth = 1600;
    private const int MaxHeight = 600;

    private const double ProcessScale = 0.72;

    private readonly Image _host;
    private readonly Dispatcher _dispatcher;
    private readonly Func<IntPtr> _getHwnd;
    private readonly Func<CaptureRegion?> _regionProvider;
    private readonly double _frameIntervalMs;

    private readonly object _sync = new();
    private GlassParams _params = GlassParams.Default;
    private int _blurBoxRadius;
    private volatile bool _mapsDirty;

    private Thread? _worker;
    private volatile bool _isActive;

    // Owned exclusively by the worker thread once Start() runs.
    private WriteableBitmap? _bitmap;
    private byte[] _outBuffer = Array.Empty<byte>();
    private byte[] _blurTmp = Array.Empty<byte>();

    private IntPtr _memDc;
    private IntPtr _dibBmp;
    private IntPtr _dibBits;
    private IntPtr _oldBmp;
    private int _dibW, _dibH;

    // Bilinear sample maps. For each channel: _idx* = byte offset of the
    // top-left source texel; _aux* packs the bilinear weights and edge flags:
    //   bit0      = has right neighbour (sample +1 in x)
    //   bit1      = has bottom neighbour (sample +1 in y)
    //   bits2-10  = wx, horizontal weight in fixed point (0..256)
    //   bits12-20 = wy, vertical weight in fixed point (0..256)
    private int[] _idxR = Array.Empty<int>();
    private int[] _auxR = Array.Empty<int>();
    private int[] _idxG = Array.Empty<int>();
    private int[] _auxG = Array.Empty<int>();
    private int[] _idxB = Array.Empty<int>();
    private int[] _auxB = Array.Empty<int>();

    private int _outW, _outH;
    private int _srcW, _srcH;
    private int _margin;

    private MagnifierCaptureSource? _mag;
    private volatile bool _magReady;
    private int _magFailStreak;
    private double _bitmapDpi = 96;
    private volatile bool _magPath;

    public LiquidGlassController(Image host, Func<IntPtr> getHwnd, Func<CaptureRegion?> regionProvider, int fps = 30)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _getHwnd = getHwnd ?? throw new ArgumentNullException(nameof(getHwnd));
        _regionProvider = regionProvider ?? throw new ArgumentNullException(nameof(regionProvider));
        _dispatcher = host.Dispatcher;

        _frameIntervalMs = 1000.0 / Math.Clamp(fps, 5, 60);
    }

    public bool IsActive => _isActive;

    public void SetParams(GlassParams p)
    {
        lock (_sync)
        {
            bool geometryChanged =
                Math.Abs(p.Refraction - _params.Refraction) > 1e-4 ||
                Math.Abs(p.ChromaticAberration - _params.ChromaticAberration) > 1e-4 ||
                Math.Abs(p.Distortion - _params.Distortion) > 1e-4 ||
                Math.Abs(p.ZRadius - _params.ZRadius) > 1e-4 ||
                p.BevelMode != _params.BevelMode;

            _params = p;
            if (geometryChanged) _mapsDirty = true;
        }
    }

    public void SetBlur(int boxRadius)
    {
        lock (_sync) _blurBoxRadius = Math.Clamp(boxRadius, 0, 60);
    }

    /// <summary>
    /// Kept for settings compatibility. Capture exclusion is no longer used —
    /// the magnifier excludes the notch from the capture without WDA, so the
    /// notch stays visible in screenshots.
    /// </summary>
    public void SetCaptureExclusion(bool exclude) { /* no-op */ }

    public void Start()
    {
        if (_isActive) return;
        _isActive = true;

        // Set up the magnifier capture source once (it owns a pump thread and a
        // hidden host window). If unavailable, we fall back to a plain screen
        // BitBlt with a below-notch sample offset to avoid self-capture feedback.
        if (_mag == null)
        {
            _mag = new MagnifierCaptureSource();
            _magReady = _mag.Initialize(_getHwnd());
            if (!_magReady)
                RuntimeLog.Log("LIQUIDGLASS", "Magnifier unavailable; using BitBlt fallback (offset sampling).");
        }

        // Bitmap DPI so the magnifier path can present device-pixel-perfect (1:1)
        // with Stretch=None — avoids stretching a frozen frame while the notch
        // animates (which made the refracted desktop appear to drift).
        uint dpiNow = GetDpiForWindow(_getHwnd());
        _bitmapDpi = dpiNow > 0 ? dpiNow : 96;

        if (_worker is { IsAlive: true }) return;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "LiquidGlassRender",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    public void Stop()
    {
        if (!_isActive) return;
        _isActive = false;

        // Safety: clear any display affinity a previous build may have set.
        SetWindowDisplayAffinitySafe(WDA_NONE);

        // Do NOT join the worker here: it may be inside a synchronous Dispatcher
        // call waiting for this (UI) thread, which would deadlock. It observes
        // _isActive, releases its GDI/buffers and exits on its own.
        try
        {
            _host.Source = null;
            _bitmap = null;
        }
        catch { /* shutting down */ }
    }

    private void SetWindowDisplayAffinitySafe(uint affinity)
    {
        var hwnd = _getHwnd();
        if (hwnd == IntPtr.Zero) return;
        try
        {
            SetWindowDisplayAffinity(hwnd, affinity);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("LIQUIDGLASS", $"SetWindowDisplayAffinity({affinity}) failed: {ex.Message}");
        }
    }

    private void WorkerLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (_isActive)
            {
                double frameStart = clock.Elapsed.TotalMilliseconds;

                CaptureRegion? region = TryGetRegionOnUi();
                if (!_isActive) break;

                if (region is { } r)
                {
                    try
                    {
                        if (ProcessFrame(r))
                            Present();
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Log("LIQUIDGLASS", $"Render failed: {ex.Message}");
                    }
                }

                if (!_isActive) break;

                double elapsed = clock.Elapsed.TotalMilliseconds - frameStart;
                int sleep = (int)Math.Round(_frameIntervalMs - elapsed);
                Thread.Sleep(sleep > 1 ? sleep : 1);
            }
        }
        finally
        {
            ReleaseGdiResources();
            _outBuffer = _blurTmp = Array.Empty<byte>();
            _idxR = _auxR = _idxG = _auxG = _idxB = _auxB = Array.Empty<int>();
            _outW = _outH = _srcW = _srcH = _margin = 0;
            _worker = null;
        }
    }

    private CaptureRegion? TryGetRegionOnUi()
    {
        try
        {
            return _dispatcher.Invoke(_regionProvider, DispatcherPriority.Send);
        }
        catch (Exception)
        {
            // Dispatcher shutting down or unavailable.
            return null;
        }
    }

    private bool ProcessFrame(CaptureRegion region)
    {
        GlassParams p;
        int blurRadius;
        lock (_sync)
        {
            p = _params;
            blurRadius = _blurBoxRadius;
        }

        int displayW = Math.Min(region.Width, MaxWidth);
        int displayH = Math.Min(region.Height, MaxHeight);
        if (displayW <= 1 || displayH <= 1) return false;

        // The magnifier samples at native resolution (it excludes the notch via a
        // filter, so no downscale trick is needed). The BitBlt fallback downscales
        // for performance and offsets the sample below the notch to avoid feedback.
        bool useMag = _magReady && _mag != null;
        _magPath = useMag;

        int outW, outH;
        if (useMag)
        {
            outW = displayW;
            outH = displayH;
        }
        else
        {
            outW = Math.Max(8, (int)Math.Round(displayW * ProcessScale));
            outH = Math.Max(8, (int)Math.Round(displayH * ProcessScale));
        }

        // Constant sample padding (independent of notch size) so the refraction
        // geometry and the desktop behind stay locked while the notch animates.
        int margin = 32;
        int srcW = outW + margin * 2;
        int srcH = outH + margin;

        EnsureMaps(p, outW, outH, srcW, srcH, margin);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        try
        {
            if (!EnsureGdiResources(srcW, srcH, screenDc)) return false;

            if (useMag)
            {
                // Sample exactly behind the notch (margin px padding on the sides/bottom).
                int srcX = region.X - margin;
                int srcY = region.Y;
                if (srcX < 0) srcX = 0;
                if (srcY < 0) srcY = 0;
                if (!_mag!.CaptureInto(srcX, srcY, srcW, srcH, _dibBits))
                {
                    // If the magnifier keeps failing (e.g. it conflicts with another
                    // capture tool), permanently fall back to the BitBlt path so the
                    // glass keeps working instead of freezing on the last frame.
                    if (++_magFailStreak >= 30)
                    {
                        _magReady = false;
                        RuntimeLog.Log("LIQUIDGLASS", "Magnifier failing repeatedly; falling back to BitBlt.");
                    }
                    return false;
                }
                _magFailStreak = 0;
            }
            else
            {
                double inv = 1.0 / ProcessScale;
                int physMargin = (int)Math.Round(margin * inv);
                int physSrcW = (int)Math.Round(srcW * inv);
                int physSrcH = (int)Math.Round(srcH * inv);
                int srcX = region.X - physMargin;
                // Offset the sample below the notch so the BitBlt doesn't capture
                // the notch itself (DWM composites it -> feedback ghost).
                int srcY = region.Y + displayH + 2;
                if (srcX < 0) srcX = 0;
                if (srcY < 0) srcY = 0;

                if (!StretchBlt(_memDc, 0, 0, srcW, srcH, screenDc, srcX, srcY, physSrcW, physSrcH, SRCCOPY))
                    return false;
                GdiFlush();
            }

            Refract(p);

            int effBlur = useMag ? blurRadius : (int)Math.Round(blurRadius * ProcessScale);
            if (effBlur > 0)
                BlurOutput(effBlur);

            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void Present()
    {
        if (!_isActive) return;
        int w = _outW, h = _outH;
        byte[] buffer = _outBuffer;
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (!_isActive) return;

                if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h
                    || Math.Abs(_bitmap.DpiX - 96.0) > 0.5)
                {
                    _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    _host.Stretch = Stretch.Fill;
                    _host.HorizontalAlignment = HorizontalAlignment.Stretch;
                    _host.VerticalAlignment = VerticalAlignment.Stretch;
                    _host.Source = _bitmap;
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, w, h), buffer, w * 4, 0);
                if (!ReferenceEquals(_host.Source, _bitmap))
                    _host.Source = _bitmap;
            }, DispatcherPriority.Render);
        }
        catch (Exception)
        {
            // Dispatcher shutting down.
        }
    }

    private bool EnsureGdiResources(int srcW, int srcH, IntPtr screenDc)
    {
        if (_memDc != IntPtr.Zero && _dibBits != IntPtr.Zero && _dibW == srcW && _dibH == srcH)
            return true;

        ReleaseGdiResources();

        _memDc = CreateCompatibleDC(screenDc);
        if (_memDc == IntPtr.Zero) return false;

        // HALFTONE gives the best downscale quality for StretchBlt; cost is
        // negligible for these small regions and it avoids aliasing artifacts.
        SetStretchBltMode(_memDc, HALFTONE);

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = srcW,
                biHeight = -srcH,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            },
            bmiColors = new uint[256]
        };

        _dibBmp = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out _dibBits, IntPtr.Zero, 0);
        if (_dibBmp == IntPtr.Zero || _dibBits == IntPtr.Zero)
        {
            ReleaseGdiResources();
            return false;
        }

        _oldBmp = SelectObject(_memDc, _dibBmp);
        _dibW = srcW; _dibH = srcH;
        return true;
    }

    private void ReleaseGdiResources()
    {
        if (_memDc != IntPtr.Zero && _oldBmp != IntPtr.Zero) SelectObject(_memDc, _oldBmp);
        if (_dibBmp != IntPtr.Zero) DeleteObject(_dibBmp);
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
        _memDc = _dibBmp = _dibBits = _oldBmp = IntPtr.Zero;
        _dibW = _dibH = 0;
    }

    private const int ParallelPixelThreshold = 40_000;

    private static readonly ParallelOptions ParallelOpts = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8))
    };

    private unsafe void Refract(GlassParams p)
    {
        int count = _outW * _outH;
        int stride = _srcW * 4;
        byte* src = (byte*)_dibBits;

        double sat = p.Saturation;
        double bright = p.Brightness;
        bool adjust = Math.Abs(sat) > 1e-3 || Math.Abs(bright) > 1e-3;
        double satFactor = 1.0 + sat;
        double brightAdd = bright * 255.0;

        fixed (byte* dst = _outBuffer)
        fixed (int* ir = _idxR, ar = _auxR, ig = _idxG, ag = _auxG, ib = _idxB, ab = _auxB)
        {
            if (count >= ParallelPixelThreshold)
            {
                IntPtr srcP = (IntPtr)src, dstP = (IntPtr)dst;
                IntPtr irP = (IntPtr)ir, arP = (IntPtr)ar, igP = (IntPtr)ig, agP = (IntPtr)ag, ibP = (IntPtr)ib, abP = (IntPtr)ab;
                bool adj = adjust; double sf = satFactor, ba = brightAdd; int st = stride;

                Parallel.ForEach(Partitioner.Create(0, count), ParallelOpts, range =>
                {
                    Gather((byte*)srcP, (byte*)dstP, (int*)irP, (int*)arP, (int*)igP, (int*)agP, (int*)ibP, (int*)abP,
                        st, range.Item1, range.Item2, adj, sf, ba);
                });
            }
            else
            {
                Gather(src, dst, ir, ar, ig, ag, ib, ab, stride, 0, count, adjust, satFactor, brightAdd);
            }
        }
    }

    private static unsafe void Gather(byte* src, byte* dst,
        int* idxR, int* auxR, int* idxG, int* auxG, int* idxB, int* auxB,
        int stride, int start, int end, bool adjust, double satFactor, double brightAdd)
    {
        for (int i = start; i < end; i++)
        {
            int o = i << 2;
            int b = Bilerp(src, idxB[i], auxB[i], stride, 0);
            int g = Bilerp(src, idxG[i], auxG[i], stride, 1);
            int r = Bilerp(src, idxR[i], auxR[i], stride, 2);

            if (!adjust)
            {
                dst[o + 0] = (byte)b;
                dst[o + 1] = (byte)g;
                dst[o + 2] = (byte)r;
                dst[o + 3] = 255;
            }
            else
            {
                double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                dst[o + 0] = ClampByte(lum + (b - lum) * satFactor + brightAdd);
                dst[o + 1] = ClampByte(lum + (g - lum) * satFactor + brightAdd);
                dst[o + 2] = ClampByte(lum + (r - lum) * satFactor + brightAdd);
                dst[o + 3] = 255;
            }
        }
    }

    /// <summary>Bilinear sample of one channel using a packed weight map entry.</summary>
    private static unsafe int Bilerp(byte* src, int idx, int aux, int stride, int chan)
    {
        int stepX = (aux & 1) != 0 ? 4 : 0;
        int stepY = (aux & 2) != 0 ? stride : 0;
        int wx = (aux >> 2) & 0x1FF;
        int wy = (aux >> 12) & 0x1FF;

        int bas = idx + chan;
        int p00 = src[bas];
        int p10 = src[bas + stepX];
        int p01 = src[bas + stepY];
        int p11 = src[bas + stepX + stepY];

        int top = p00 * (256 - wx) + p10 * wx;
        int bot = p01 * (256 - wx) + p11 * wx;
        return (top * (256 - wy) + bot * wy) >> 16;
    }

    private static byte ClampByte(double v)
    {
        if (v <= 0) return 0;
        if (v >= 255) return 255;
        return (byte)(v + 0.5);
    }

    private void BlurOutput(int radius)
    {
        int w = _outW, h = _outH;
        if (w < 2 || h < 2) return;
        int r = Math.Min(radius, Math.Min(w, h) / 2);
        if (r < 1) return;

        byte[] a = _outBuffer, b = _blurTmp;
        for (int pass = 0; pass < 3; pass++)
        {
            ParallelRange(h, (y0, y1) => BoxBlurHorizontal(a, b, w, h, r, y0, y1));
            ParallelRange(w, (x0, x1) => BoxBlurVertical(b, a, w, h, r, x0, x1));
        }
    }

    private static void ParallelRange(int length, Action<int, int> body)
    {
        if (length >= 64)
            Parallel.ForEach(Partitioner.Create(0, length), ParallelOpts, range => body(range.Item1, range.Item2));
        else
            body(0, length);
    }

    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int h, int radius, int y0, int y1)
    {
        int window = 2 * radius + 1;
        for (int y = y0; y < y1; y++)
        {
            int rowBase = y * w * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = dx < 0 ? 0 : (dx >= w ? w - 1 : dx);
                int o = rowBase + nx * 4;
                sumB += src[o]; sumG += src[o + 1]; sumR += src[o + 2]; sumA += src[o + 3];
            }
            for (int x = 0; x < w; x++)
            {
                int t = rowBase + x * 4;
                dst[t] = (byte)(sumB / window);
                dst[t + 1] = (byte)(sumG / window);
                dst[t + 2] = (byte)(sumR / window);
                dst[t + 3] = (byte)(sumA / window);

                int outX = x - radius; outX = outX < 0 ? 0 : outX;
                int inX = x + 1 + radius; inX = inX >= w ? w - 1 : inX;
                int oOut = rowBase + outX * 4;
                int oIn = rowBase + inX * 4;
                sumB += src[oIn] - src[oOut];
                sumG += src[oIn + 1] - src[oOut + 1];
                sumR += src[oIn + 2] - src[oOut + 2];
                sumA += src[oIn + 3] - src[oOut + 3];
            }
        }
    }

    private static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int radius, int x0, int x1)
    {
        int window = 2 * radius + 1;
        int rowStride = w * 4;
        for (int x = x0; x < x1; x++)
        {
            int col = x * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int ny = dy < 0 ? 0 : (dy >= h ? h - 1 : dy);
                int o = ny * rowStride + col;
                sumB += src[o]; sumG += src[o + 1]; sumR += src[o + 2]; sumA += src[o + 3];
            }
            for (int y = 0; y < h; y++)
            {
                int t = y * rowStride + col;
                dst[t] = (byte)(sumB / window);
                dst[t + 1] = (byte)(sumG / window);
                dst[t + 2] = (byte)(sumR / window);
                dst[t + 3] = (byte)(sumA / window);

                int outY = y - radius; outY = outY < 0 ? 0 : outY;
                int inY = y + 1 + radius; inY = inY >= h ? h - 1 : inY;
                int oOut = outY * rowStride + col;
                int oIn = inY * rowStride + col;
                sumB += src[oIn] - src[oOut];
                sumG += src[oIn + 1] - src[oOut + 1];
                sumR += src[oIn + 2] - src[oOut + 2];
                sumA += src[oIn + 3] - src[oOut + 3];
            }
        }
    }

    private void EnsureMaps(GlassParams p, int outW, int outH, int srcW, int srcH, int margin)
    {
        if (!_mapsDirty && _outW == outW && _outH == outH && _srcW == srcW && _srcH == srcH && _margin == margin
            && _idxR.Length == outW * outH)
            return;

        _mapsDirty = false;
        _outW = outW; _outH = outH; _srcW = srcW; _srcH = srcH; _margin = margin;

        int n = outW * outH;
        if (_outBuffer.Length != n * 4)
        {
            _outBuffer = new byte[n * 4];
            _blurTmp = new byte[n * 4];
        }
        if (_idxR.Length != n)
        {
            _idxR = new int[n]; _auxR = new int[n];
            _idxG = new int[n]; _auxG = new int[n];
            _idxB = new int[n]; _auxB = new int[n];
        }

        double zr = Math.Clamp(p.ZRadius, 0.05, 0.6);
        // Fixed-pixel rim: the refraction geometry must NOT scale with the notch
        // size, otherwise the bent content visibly slides/zooms every time the
        // notch animates. A constant band + offset keeps the desktop behind the
        // glass locked in place; the notch just reveals more of it as it grows.
        double bandX = Math.Clamp(zr * 130.0, 8.0, 90.0);
        double bandY = Math.Clamp(zr * 130.0 * 1.2, 8.0, 90.0);
        double maxOff = Math.Min(margin - 2.0, 30.0) * Math.Clamp(p.Refraction, 0.0, 1.5);
        double ca = Math.Clamp(p.ChromaticAberration, 0.0, 1.0);
        double distAmp = Math.Clamp(p.Distortion, 0.0, 1.0) * Math.Min(margin - 2.0, 14.0);
        int bevel = p.BevelMode;
        int maxSrcX = srcW - 1;
        int maxSrcY = srcH - 1;

        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                double dispX = 0;
                int distL = x;
                int distR = outW - 1 - x;
                if (distL < bandX) dispX -= maxOff * EdgeCurve(distL / bandX, bevel);
                if (distR < bandX) dispX += maxOff * EdgeCurve(distR / bandX, bevel);

                double dispY = 0;
                int distB = outH - 1 - y;
                if (distB < bandY) dispY += maxOff * EdgeCurve(distB / bandY, bevel);

                if (distAmp > 0)
                {
                    dispX += distAmp * Math.Sin(y * 0.45 + x * 0.11);
                    dispY += distAmp * 0.6 * Math.Sin(x * 0.5 + y * 0.13);
                }

                double baseX = x + margin;
                double baseY = y;

                int idx = y * outW + x;
                SetSample(baseX + dispX, baseY + dispY, srcW, maxSrcX, maxSrcY, _idxG, _auxG, idx);
                SetSample(baseX + dispX * (1.0 + ca), baseY + dispY * (1.0 + ca), srcW, maxSrcX, maxSrcY, _idxR, _auxR, idx);
                SetSample(baseX + dispX * (1.0 - ca), baseY + dispY * (1.0 - ca), srcW, maxSrcX, maxSrcY, _idxB, _auxB, idx);
            }
        }
    }

    private static void SetSample(double sx, double sy, int srcW, int maxX, int maxY,
        int[] idxArr, int[] auxArr, int i)
    {
        int ix = (int)Math.Floor(sx);
        int iy = (int)Math.Floor(sy);
        double fx = sx - ix;
        double fy = sy - iy;

        int flags = 0;
        if (ix < 0) { ix = 0; fx = 0; }
        else if (ix >= maxX) { ix = maxX; fx = 0; }
        else flags |= 1; // has right neighbour

        if (iy < 0) { iy = 0; fy = 0; }
        else if (iy >= maxY) { iy = maxY; fy = 0; }
        else flags |= 2; // has bottom neighbour

        int wx = (int)Math.Round(fx * 256.0);
        if (wx < 0) wx = 0; else if (wx > 256) wx = 256;
        int wy = (int)Math.Round(fy * 256.0);
        if (wy < 0) wy = 0; else if (wy > 256) wy = 256;

        idxArr[i] = (iy * srcW + ix) << 2;
        auxArr[i] = flags | (wx << 2) | (wy << 12);
    }

    private static double EdgeCurve(double u, int mode)
    {
        if (u < 0) u = 0; else if (u > 1) u = 1;
        if (mode == 1)
            return Math.Cos(u * (Math.PI / 2.0));
        double v = 1.0 - u;
        return v * v;
    }
}
