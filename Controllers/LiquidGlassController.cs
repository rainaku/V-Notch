using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
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

public sealed class LiquidGlassController
{
    public readonly record struct CaptureRegion(int X, int Y, int Width, int Height, double CornerRadiusDip = 0,
        double SubX = 0, double SubY = 0);

    public struct GlassParams
    {
        public double Refraction;
        public double ChromaticAberration;
        public double Distortion;
        public double ZRadius;
        public double Saturation;
        public double Brightness;
        public int BevelMode;
        /// <summary>Corner radius of the glass panel in DIP (device-independent px).
        /// Scaled to output pixels internally so the refraction SDF matches the
        /// notch's visible rounded-rect.</summary>
        public double CornerRadius;

        public static GlassParams Default => new()
        {
            // Mirror the reference WebGL library's defaults (ybouane/liquidglass).
            Refraction = 0.69,
            ChromaticAberration = 0.05,
            Distortion = 0.0,
            ZRadius = 0.40,
            Saturation = 0.0,
            Brightness = 0.0,
            BevelMode = 0,
            CornerRadius = 20.0
        };
    }

    private const int MaxWidth = 1600;
    private const int MaxHeight = 600;

    // Downscale factor for the BitBlt fallback path.
    private const double ProcessScale = 0.72;

    // Downscale factor for the magnifier path. The refraction is a soft effect,
    // so rendering it (and the blur) at a slightly reduced resolution and letting
    // the present upscale it back is visually indistinguishable but cuts the
    // per-pixel work (maps + refract + blur) by ~1 - scale². Kept high to preserve
    // edge crispness on the bevel.
    private const double MagProcessScale = 0.85;

    private readonly Image _host;
    private readonly Dispatcher _dispatcher;
    private readonly Func<IntPtr> _getHwnd;
    private readonly Func<CaptureRegion?> _regionProvider;

    // Adaptive frame pacing: the glass only needs a high refresh rate while the
    // notch is animating (resizing/sliding) — that's when the per-pixel maps must
    // be rebuilt every frame to track the moving edge. When the notch is static,
    // only the desktop behind it can change, so a low refresh rate is plenty and
    // keeps steady-state CPU minimal.
    private readonly double _activeIntervalMs;
    private readonly double _idleIntervalMs;
    private volatile bool _animating;

    private readonly object _sync = new();
    private GlassParams _params = GlassParams.Default;
    private int _blurBoxRadius;
    private volatile bool _mapsDirty;

    private Thread? _worker;
    private volatile bool _isActive;

    // Owned exclusively by the worker thread once Start() runs.
    private WriteableBitmap? _bitmap;
    private TranslateTransform? _hostTransform;
    private double _presentSubX, _presentSubY;
    private byte[] _outBuffer = Array.Empty<byte>();
    private byte[] _blurTmp = Array.Empty<byte>();

    private IntPtr _memDc;
    private IntPtr _dibBmp;
    private IntPtr _dibBits;
    private IntPtr _oldBmp;
    private int _dibW, _dibH;

    // Staging DIB for the magnifier path: the magnifier delivers its frame at
    // native resolution, so we capture into this native-sized buffer and then
    // StretchBlt it down into the (smaller) working DIB before refraction. This
    // lets the magnifier path share the same downscaled render pipeline as the
    // BitBlt fallback.
    private IntPtr _stagingDc;
    private IntPtr _stagingBmp;
    private IntPtr _stagingBits;
    private IntPtr _stagingOldBmp;
    private int _stagingW, _stagingH;


    private int[] _idxR = Array.Empty<int>();
    private int[] _auxR = Array.Empty<int>();
    private int[] _idxG = Array.Empty<int>();
    private int[] _auxG = Array.Empty<int>();
    private int[] _idxB = Array.Empty<int>();
    private int[] _auxB = Array.Empty<int>();

    // Per-output-pixel flag (1 = inside the bevel rim band where chromatic
    // aberration is strongest). Built alongside the sample maps and used to limit
    // the edge anti-alias pass to the colour-fringe band only.
    private byte[] _edgeMask = Array.Empty<byte>();

    private int _outW, _outH;
    private int _srcW, _srcH;
    private int _margin;
    private double _outScale = 1.0;
    private double _mapCornerRadius = -1;
    private double _mapZRadius = -1;
    private int _mapNotchW = -1, _mapNotchH = -1, _mapNotchOffX = -1, _mapNotchOffY = -1;

    private MagnifierCaptureSource? _mag;
    private volatile bool _magReady;
    private int _magFailStreak;
    private double _bitmapDpi = 96;
    private volatile bool _magPath;

    public LiquidGlassController(Image host, Func<IntPtr> getHwnd, Func<CaptureRegion?> regionProvider,
        int activeFps = 60, int idleFps = 30)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _getHwnd = getHwnd ?? throw new ArgumentNullException(nameof(getHwnd));
        _regionProvider = regionProvider ?? throw new ArgumentNullException(nameof(regionProvider));
        _dispatcher = host.Dispatcher;

        int active = Math.Clamp(activeFps, 5, 120);
        _activeIntervalMs = 1000.0 / active;
        _idleIntervalMs = 1000.0 / Math.Clamp(idleFps, 1, active);
    }

    public bool IsActive => _isActive;

    /// <summary>
    /// Switches between the high (animating) and low (idle) refresh rates. Called
    /// by the UI whenever the notch starts or finishes an animation, so the glass
    /// only spends CPU at full rate while there's actually motion to track.
    /// </summary>
    public void SetAnimating(bool animating) => _animating = animating;

    public void SetParams(GlassParams p)
    {
        lock (_sync)
        {
            bool geometryChanged =
                Math.Abs(p.Refraction - _params.Refraction) > 1e-4 ||
                Math.Abs(p.ChromaticAberration - _params.ChromaticAberration) > 1e-4 ||
                Math.Abs(p.Distortion - _params.Distortion) > 1e-4 ||
                Math.Abs(p.ZRadius - _params.ZRadius) > 1e-4 ||
                Math.Abs(p.CornerRadius - _params.CornerRadius) > 1e-4 ||
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
            Priority = ThreadPriority.Normal
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

                // Pick the cadence for THIS frame: full rate while the notch is
                // animating, throttled when it's static.
                bool animating = _animating;
                double frameIntervalMs = animating ? _activeIntervalMs : _idleIntervalMs;

                // While a screenshot/snip tool overlay is up, the magnifier mirrors
                // that overlay (dimming + selection UI) into the glass, causing a
                // flicker/glitch. Hold the last good frame until it's dismissed.
                if (IsCaptureOverlayActive())
                {
                    Thread.Sleep((int)Math.Max(15, frameIntervalMs));
                    continue;
                }

                CaptureRegion? region = GetRegionCached(animating, frameStart);
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
                int sleep = (int)Math.Round(frameIntervalMs - elapsed);
                Thread.Sleep(sleep > 1 ? sleep : 1);
            }
        }
        finally
        {
            ReleaseGdiResources();
            _outBuffer = _blurTmp = Array.Empty<byte>();
            _idxR = _auxR = _idxG = _auxG = _idxB = _auxB = Array.Empty<int>();
            _edgeMask = Array.Empty<byte>();
            _outW = _outH = _srcW = _srcH = _margin = 0;
            _worker = null;
        }
    }

    private CaptureRegion? _cachedRegion;
    private double _lastRegionFetchMs = double.NegativeInfinity;
    private const double IdleRegionRefreshMs = 250.0;

    /// <summary>
    /// Returns the capture region, throttling the (synchronous) round-trip to the
    /// UI thread. While animating the notch moves every frame, so we must re-query
    /// each frame; while static the region only changes on DPI/monitor events, so
    /// we reuse the cached region and refresh it occasionally.
    /// </summary>
    private CaptureRegion? GetRegionCached(bool animating, double nowMs)
    {
        if (animating || nowMs - _lastRegionFetchMs >= IdleRegionRefreshMs)
        {
            _cachedRegion = TryGetRegionOnUi();
            _lastRegionFetchMs = nowMs;
        }
        return _cachedRegion;
    }

    // Process names (without .exe) of common screenshot / screen-snip tools whose
    // fullscreen overlays would otherwise be mirrored into the glass.
    private static readonly string[] _captureProcessNames =
    {
        "snippingtool", "screenclippinghost", "screensketch", "sharex",
        "greenshot", "lightshot", "snagit32", "snagiteditor", "flameshot",
        "picpick", "screenpresso", "ksnip"
    };

    private long _lastOverlayCheckTicks;
    private bool _overlayActiveCached;

    /// <summary>
    /// True while a screenshot/snip overlay appears to own the foreground. Throttled
    /// so the per-frame cost stays negligible.
    /// </summary>
    private bool IsCaptureOverlayActive()
    {
        long now = Environment.TickCount64;
        if (now - _lastOverlayCheckTicks < 120) return _overlayActiveCached;
        _lastOverlayCheckTicks = now;
        _overlayActiveCached = DetectCaptureOverlay();
        return _overlayActiveCached;
    }

    private static bool DetectCaptureOverlay()
    {
        bool found = false;
        try
        {
            // Win+Shift+S (and most snip tools) drop a large fullscreen overlay that
            // does NOT necessarily become the foreground window, so enumerate all
            // visible top-level windows rather than only checking the foreground.
            var pidNames = new Dictionary<uint, string>();

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (!GetWindowRect(hwnd, out var r)) return true;

                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                // Only large windows can be a capture overlay; this also keeps the
                // per-window cost (class/process lookups) tiny.
                if (w < 400 || h < 400) return true;

                var sb = new StringBuilder(128);
                if (GetClassName(hwnd, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls.IndexOf("ScreenClipping", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = true;
                        return false;
                    }
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;

                if (!pidNames.TryGetValue(pid, out string? name))
                {
                    name = SafeProcessName(pid);
                    pidNames[pid] = name;
                }
                if (name.Length == 0) return true;

                for (int i = 0; i < _captureProcessNames.Length; i++)
                {
                    if (string.Equals(name, _captureProcessNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Enumeration can transiently fail — treat as no overlay.
        }
        return found;
    }

    private static string SafeProcessName(uint pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
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

        // Use the live (possibly animating) notch corner radius when supplied so
        // the refraction SDF stays glued to the visible rounded-rect.
        if (region.CornerRadiusDip > 0)
            p.CornerRadius = region.CornerRadiusDip;

        // Sub-pixel remainder between the notch's true position and the integer
        // pixel the capture had to snap to — compensated at present time.
        _presentSubX = region.SubX;
        _presentSubY = region.SubY;

        int displayW = Math.Min(region.Width, MaxWidth);
        int displayH = Math.Min(region.Height, MaxHeight);
        if (displayW <= 1 || displayH <= 1) return false;

        // Both paths render at a reduced internal scale and let the present upscale
        // back to native — the refraction is soft, so the resolution loss is
        // invisible but the per-pixel work (maps + refract + blur) drops by ~scale².
        bool useMag = _magReady && _mag != null;
        _magPath = useMag;
        double scale = useMag ? MagProcessScale : ProcessScale;

        int outW = Math.Max(8, (int)Math.Round(displayW * scale));
        int outH = Math.Max(8, (int)Math.Round(displayH * scale));

        // Output pixels per DIP. Lets EnsureMaps express the SDF corner radius and
        // bevel depth in the same pixel space as the refraction sample maps, so the
        // glass geometry matches the notch at any DPI / capture path (magnifier vs
        // downscaled BitBlt).
        _outScale = displayW > 0 ? (double)outW / displayW * (_bitmapDpi / 96.0) : 1.0;

        // Overscan ring. The notch occupies a sub-rect of a larger output buffer;
        // the extra ring is filled with plain (un-refracted) desktop. Because the
        // notch animates its size while the worker presents asynchronously, the
        // host can momentarily outgrow the captured notch. Presenting at native
        // scale would then leave an uncovered gap (a black frame); the overscan
        // means that gap reveals real desktop at the correct scale instead. The
        // ring is added left/right and below (the notch grows downward from a fixed
        // top edge and stays horizontally centred).
        int overscan = Math.Clamp((int)Math.Round(80 * (_bitmapDpi / 96.0)), 64, 150);
        int bufW = outW + overscan * 2;
        int bufH = outH + overscan;
        int notchOffX = overscan;
        int notchOffY = 0;

        // Constant sample padding (independent of notch size) so the refraction
        // geometry and the desktop behind stay locked while the notch animates.
        int margin = 32;
        int srcW = bufW + margin * 2;
        int srcH = bufH + margin * 2;

        EnsureMaps(p, bufW, bufH, srcW, srcH, margin, outW, outH, notchOffX, notchOffY);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        try
        {
            if (!EnsureGdiResources(srcW, srcH, screenDc)) return false;

            // Physical (native) extents of the region we must sample. The overscan
            // ring and margin are expressed in output px, so they inflate by 1/scale
            // when mapped back to native screen pixels.
            double inv = 1.0 / scale;
            int physMargin = (int)Math.Round(margin * inv);
            int physOverscan = (int)Math.Round(overscan * inv);
            int physSrcW = (int)Math.Round(srcW * inv);
            int physSrcH = (int)Math.Round(srcH * inv);

            if (useMag)
            {
                // The magnifier excludes the notch from its capture, so we can sample
                // the notch's own rectangle directly (no below-notch feedback offset).
                int srcX = region.X - physOverscan - physMargin;
                int srcY = region.Y - physMargin;
                if (srcX < 0) srcX = 0;
                if (srcY < 0) srcY = 0;

                if (!EnsureStagingResources(physSrcW, physSrcH, screenDc)) return false;

                // Capture the native region into the staging buffer, then downscale
                // it into the working DIB the refraction reads from.
                if (!_mag!.CaptureInto(srcX, srcY, physSrcW, physSrcH, _stagingBits))
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

                if (!StretchBlt(_memDc, 0, 0, srcW, srcH, _stagingDc, 0, 0, physSrcW, physSrcH, SRCCOPY))
                    return false;
                GdiFlush();
            }
            else
            {
                int srcX = region.X - physOverscan - physMargin;
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

            int effBlur = (int)Math.Round(blurRadius * scale);
            if (effBlur > 0)
                BlurOutput(effBlur);
            else if (p.ChromaticAberration > 1e-3)
                EdgeAntiAlias();

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

        // Present the captured desktop at its TRUE native scale instead of
        // stretching it to fill the host. Stretching makes the backdrop visibly
        // zoom in/out during the notch's resize animation, because the bitmap is
        // sized to the notch at capture time but the host has already animated to a
        // different size by present time. With Stretch=None the bitmap renders at a
        // fixed scale (DPI chosen so 1 output px == 1 screen px), and a size
        // mismatch merely reveals slightly more / less desktop (clipped by the
        // host) rather than scaling it. Anchored top-centre to match the notch,
        // which grows downward from a fixed top edge and stays horizontally centred.
        //
        // Both capture paths pre-downscale by their internal scale, so the present
        // DPI is scaled up to land the frozen frame back at native screen scale.
        // Magnifier and BitBlt paths both render at a reduced internal scale, so
        // the present DPI is scaled up to land the bitmap back at native screen
        // scale (1 output px maps to 1/scale screen px).
        double presentDpi = _bitmapDpi * (_magPath ? MagProcessScale : ProcessScale);
        if (presentDpi < 1.0) presentDpi = 96.0;

        // Compensate the capture's pixel snapping with a sub-pixel shift so a
        // captured desktop pixel always lands at its true on-screen position,
        // killing the horizontal/vertical shimmer while the notch animates.
        double dpiScale = _bitmapDpi > 0 ? _bitmapDpi / 96.0 : 1.0;
        double txDip = -_presentSubX / dpiScale;
        double tyDip = -_presentSubY / dpiScale;

        try
        {
            _dispatcher.Invoke(() =>
            {
                if (!_isActive) return;

                if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h
                    || Math.Abs(_bitmap.DpiX - presentDpi) > 0.5)
                {
                    _bitmap = new WriteableBitmap(w, h, presentDpi, presentDpi, PixelFormats.Bgra32, null);
                    _host.Stretch = Stretch.None;
                    _host.HorizontalAlignment = HorizontalAlignment.Center;
                    _host.VerticalAlignment = VerticalAlignment.Top;
                    // The internal frame is rendered downscaled (MagProcessScale /
                    // ProcessScale) and the present DPI upscales it back to native
                    // screen scale. Use linear filtering so that upscale stays smooth
                    // instead of showing blocky nearest-neighbour pixels.
                    RenderOptions.SetBitmapScalingMode(_host, BitmapScalingMode.Linear);
                    _hostTransform ??= new TranslateTransform();
                    _host.RenderTransform = _hostTransform;
                    _host.Source = _bitmap;
                }
                if (_hostTransform != null)
                {
                    _hostTransform.X = txDip;
                    _hostTransform.Y = tyDip;
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

        ReleaseStagingResources();
    }

    /// <summary>
    /// Ensures a native-sized staging DIB the magnifier can capture into before we
    /// downscale it into the working buffer. Plain (no stretch mode) since it's the
    /// StretchBlt source — the HALFTONE downscale quality is set on the dest DC.
    /// </summary>
    private bool EnsureStagingResources(int w, int h, IntPtr screenDc)
    {
        if (w <= 0 || h <= 0) return false;
        if (_stagingDc != IntPtr.Zero && _stagingBits != IntPtr.Zero && _stagingW == w && _stagingH == h)
            return true;

        ReleaseStagingResources();

        _stagingDc = CreateCompatibleDC(screenDc);
        if (_stagingDc == IntPtr.Zero) return false;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            },
            bmiColors = new uint[256]
        };

        _stagingBmp = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out _stagingBits, IntPtr.Zero, 0);
        if (_stagingBmp == IntPtr.Zero || _stagingBits == IntPtr.Zero)
        {
            ReleaseStagingResources();
            return false;
        }

        _stagingOldBmp = SelectObject(_stagingDc, _stagingBmp);
        _stagingW = w; _stagingH = h;
        return true;
    }

    private void ReleaseStagingResources()
    {
        if (_stagingDc != IntPtr.Zero && _stagingOldBmp != IntPtr.Zero) SelectObject(_stagingDc, _stagingOldBmp);
        if (_stagingBmp != IntPtr.Zero) DeleteObject(_stagingBmp);
        if (_stagingDc != IntPtr.Zero) DeleteDC(_stagingDc);
        _stagingDc = _stagingBmp = _stagingBits = _stagingOldBmp = IntPtr.Zero;
        _stagingW = _stagingH = 0;
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

    /// <summary>
    /// Cheap anti-alias for the chromatic-aberration fringe. The displacement field
    /// changes fastest at the rim, so the per-channel offset sampling there aliases
    /// into a jagged colour edge. We approximate a supersample by averaging each
    /// rim-band pixel with its 4 orthogonal neighbours — restricted to the flagged
    /// band so the flat interior stays crisp. Runs only when there's no blur (blur
    /// already smooths the fringe) and chromatic aberration is active.
    /// </summary>
    private void EdgeAntiAlias()
    {
        int w = _outW, h = _outH;
        int n = w * h;
        if (w < 3 || h < 3 || _edgeMask.Length != n || _outBuffer.Length != n * 4 || _blurTmp.Length != n * 4)
            return;

        byte[] src = _outBuffer, tmp = _blurTmp;

        // Phase 1: compute the smoothed value for each flagged pixel into tmp,
        // reading the unmodified src so neighbouring rim pixels don't cascade.
        ParallelRange(h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (_edgeMask[i] == 0) continue;

                    int o = i << 2;
                    int oL = (x > 0 ? i - 1 : i) << 2;
                    int oR = (x < w - 1 ? i + 1 : i) << 2;
                    int oU = (y > 0 ? i - w : i) << 2;
                    int oD = (y < h - 1 ? i + w : i) << 2;

                    tmp[o] = (byte)((src[o] + src[oL] + src[oR] + src[oU] + src[oD]) / 5);
                    tmp[o + 1] = (byte)((src[o + 1] + src[oL + 1] + src[oR + 1] + src[oU + 1] + src[oD + 1]) / 5);
                    tmp[o + 2] = (byte)((src[o + 2] + src[oL + 2] + src[oR + 2] + src[oU + 2] + src[oD + 2]) / 5);
                    tmp[o + 3] = 255;
                }
            }
        });

        // Phase 2: copy the smoothed rim pixels back.
        ParallelRange(h, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (_edgeMask[i] == 0) continue;
                    int o = i << 2;
                    src[o] = tmp[o]; src[o + 1] = tmp[o + 1]; src[o + 2] = tmp[o + 2]; src[o + 3] = 255;
                }
            }
        });
    }

    private void BlurOutput(int radius)
    {
        int w = _outW, h = _outH;
        if (w < 2 || h < 2) return;
        int r = Math.Min(radius, Math.Min(w, h) / 2);
        if (r < 1) return;

        byte[] a = _outBuffer, b = _blurTmp;
        // Two box-blur passes approximate a Gaussian closely enough for this soft
        // backdrop blur; the third pass cost more than it visibly contributed.
        for (int pass = 0; pass < 2; pass++)
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

    private void EnsureMaps(GlassParams p, int outW, int outH, int srcW, int srcH, int margin,
        int notchW, int notchH, int notchOffX, int notchOffY)
    {
        // Quantize the corner radius to 0.5 DIP. The live notch corner radius
        // arrives with continuous sub-pixel jitter (from PointToScreen / animation),
        // which would otherwise invalidate the maps and force a full SDF rebuild
        // every frame even when the geometry is effectively unchanged. A half-pixel
        // step is visually indistinguishable but lets the static notch reuse its
        // maps frame-to-frame.
        p.CornerRadius = Math.Round(p.CornerRadius * 2.0) / 2.0;

        if (!_mapsDirty && _outW == outW && _outH == outH && _srcW == srcW && _srcH == srcH && _margin == margin
            && _idxR.Length == outW * outH
            && _mapNotchW == notchW && _mapNotchH == notchH && _mapNotchOffX == notchOffX && _mapNotchOffY == notchOffY
            && Math.Abs(_mapCornerRadius - p.CornerRadius) < 1e-3
            && Math.Abs(_mapZRadius - p.ZRadius) < 1e-4)
            return;

        _mapsDirty = false;
        _outW = outW; _outH = outH; _srcW = srcW; _srcH = srcH; _margin = margin;
        _mapNotchW = notchW; _mapNotchH = notchH; _mapNotchOffX = notchOffX; _mapNotchOffY = notchOffY;
        _mapCornerRadius = p.CornerRadius;
        _mapZRadius = p.ZRadius;

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
        if (_edgeMask.Length != n)
            _edgeMask = new byte[n];

        // ─────────────────────────────────────────────────────────────────────
        // Faithful CPU port of the ybouane/liquidglass WebGL fragment shader.
        //
        // The glass is modelled as a rounded-rect slab whose top surface is a
        // half-circle bevel (the "pill" cross-section). For every output pixel we:
        //   1. evaluate the rounded-rect signed distance field (SDF),
        //   2. build the bevel height field h(d) = sqrt(d(2zR - d)),
        //   3. take its gradient via finite differences -> surface normal N,
        //   4. refract: dual-surface (biconvex) bend, OR a uniform magnification
        //      for dome mode,
        //   5. add normal-/edge-weighted chromatic aberration and micro noise.
        //
        // The rounded-rect is the *notch* sub-rect (size notchW×notchH at offset
        // notchOff*) inside a larger output buffer. Pixels outside the notch (the
        // overscan ring) are pass-through: they sample plain desktop, so when the
        // animating notch momentarily outgrows the captured size the revealed ring
        // is real desktop at the correct scale rather than a black gap.
        //
        // All displacements are in OUTPUT pixels and added directly to the source
        // sample coordinate (the captured backdrop sits at the same scale, offset
        // by `margin`). The refraction bends inward, so samples stay in bounds.
        // ─────────────────────────────────────────────────────────────────────

        double halfX = notchW * 0.5;
        double halfY = notchH * 0.5;
        double cx = notchOffX + (notchW - 1) * 0.5;
        double cy = notchOffY + (notchH - 1) * 0.5;
        double minHalf = Math.Min(halfX, halfY);

        double r = Math.Clamp(p.CornerRadius * _outScale, 0.0, minHalf);
        double zR = Math.Clamp(Math.Clamp(p.ZRadius, 0.02, 0.95) * 100.0 * _outScale, 3.0, minHalf);

        double maxD = minHalf;
        const double e = 2.0;
        const double ior = 1.5;
        double refrPow = 1.0 - 1.0 / ior;
        double uRefr = Math.Clamp(p.Refraction, 0.0, 3.0);
        double uChroma = Math.Clamp(p.ChromaticAberration, 0.0, 2.0);
        double distort = Math.Clamp(p.Distortion, 0.0, 2.0);
        bool dome = p.BevelMode >= 1;

        int maxSrcX = srcW - 1;
        int maxSrcY = srcH - 1;

        // Build the per-pixel sample maps in parallel across rows. During the
        // notch's resize animation this rebuilds every frame, so keeping it
        // multi-threaded is what lets the glass edge track the expanding notch
        // instead of lagging behind it (which shows as a seam of un-refracted
        // desktop near the growing border).
        void BuildRows(int y0, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                double ly = y - cy;
                for (int x = 0; x < outW; x++)
                {
                    double lx = x - cx;
                    int idx = y * outW + x;

                    double baseX = x + margin;
                    double baseY = y + margin;

                    double inside = -RrSdf(lx, ly, halfX, halfY, r);

                    if (inside <= 0.0)
                    {
                        // Overscan ring (outside the glass): pure pass-through.
                        _edgeMask[idx] = 0;
                        SetSample(baseX, baseY, srcW, maxSrcX, maxSrcY, _idxG, _auxG, idx);
                        SetSample(baseX, baseY, srcW, maxSrcX, maxSrcY, _idxR, _auxR, idx);
                        SetSample(baseX, baseY, srcW, maxSrcX, maxSrcY, _idxB, _auxB, idx);
                        continue;
                    }

                    // Surface-normal via height-field gradient (finite differences).
                    double hC = Bevel(inside, zR);
                    double hR = Bevel(-RrSdf(lx + e, ly, halfX, halfY, r), zR);
                    double hL = Bevel(-RrSdf(lx - e, ly, halfX, halfY, r), zR);
                    double hU = Bevel(-RrSdf(lx, ly + e, halfX, halfY, r), zR);
                    double hD = Bevel(-RrSdf(lx, ly - e, halfX, halfY, r), zR);

                    double hGradX = (hR - hL) / (2.0 * e);
                    double hGradY = (hU - hD) / (2.0 * e);

                    double nx = -hGradX, ny = -hGradY;
                    double nLen = Math.Sqrt(nx * nx + ny * ny + 1.0);
                    nx /= nLen; ny /= nLen;

                    double depth = SmoothStep(0.0, zR, inside);

                    double dispX, dispY;
                    if (!dome)
                    {
                        // Biconvex: entry + exit refraction plus a through-thickness
                        // term. Non-zero only inside the bevel ring (hGrad != 0), so
                        // the flat interior stays locked 1:1 to the desktop behind
                        // it. The reference's depth-scaled "pull toward centre" term
                        // is omitted: it magnifies the whole interior by a
                        // size-relative amount, which makes the backdrop zoom when
                        // the notch changes size.
                        double thickNorm = (hC * 2.0) / Math.Max(zR * 2.0, 1.0);
                        double k = refrPow * (2.0 + thickNorm * 0.5) * uRefr * 30.0;
                        dispX = hGradX * k;
                        dispY = hGradY * k;
                    }
                    else
                    {
                        // Dome / plano-convex: uniform magnification toward centre.
                        dispX = -lx * uRefr * depth * 0.35;
                        dispY = -ly * uRefr * depth * 0.35;
                    }

                    // Micro-distortion noise.
                    if (distort > 0.0)
                    {
                        double ns = 0.08;
                        dispX += (Hash(lx * ns, ly * ns) - 0.5) * distort * 4.0;
                        dispY += (Hash(lx * ns + 37.0, ly * ns + 37.0) - 0.5) * distort * 4.0;
                    }

                    // Edge-weighted chromatic aberration along the surface normal.
                    double edge = SmoothStep(maxD * 0.35, 0.0, inside);
                    // Flag the rim band (where the colour fringe lives) for the
                    // edge anti-alias pass.
                    _edgeMask[idx] = edge > 0.2 ? (byte)1 : (byte)0;
                    double caS = uChroma * 18.0 * (edge * 0.7 + 0.3) * 2.0;
                    double caX = nx * caS;
                    double caY = ny * caS;

                    baseX += dispX;
                    baseY += dispY;

                    SetSample(baseX, baseY, srcW, maxSrcX, maxSrcY, _idxG, _auxG, idx);
                    SetSample(baseX + caX, baseY + caY, srcW, maxSrcX, maxSrcY, _idxR, _auxR, idx);
                    SetSample(baseX - caX, baseY - caY, srcW, maxSrcX, maxSrcY, _idxB, _auxB, idx);
                }
            }
        }

        if (outH >= 64)
            Parallel.ForEach(Partitioner.Create(0, outH), ParallelOpts,
                range => BuildRows(range.Item1, range.Item2));
        else
            BuildRows(0, outH);
    }

    /// <summary>Signed distance to a rounded rectangle centred at the origin.
    /// <paramref name="bx"/>/<paramref name="by"/> are the half-extents; matches
    /// the shader's <c>rrSDF</c>.</summary>
    private static double RrSdf(double px, double py, double bx, double by, double r)
    {
        double qx = Math.Abs(px) - bx + r;
        double qy = Math.Abs(py) - by + r;
        double mx = qx > 0 ? qx : 0;
        double my = qy > 0 ? qy : 0;
        double outer = Math.Sqrt(mx * mx + my * my);
        double inner = Math.Min(Math.Max(qx, qy), 0.0);
        return inner + outer - r;
    }

    /// <summary>Half-circle bevel height at inside-distance <paramref name="d"/>
    /// for a bevel of z-radius <paramref name="zR"/>.</summary>
    private static double Bevel(double d, double zR)
    {
        if (d <= 0.0) return 0.0;
        if (d >= zR) return zR;
        return Math.Sqrt(d * (2.0 * zR - d));
    }

    /// <summary>GLSL-compatible smoothstep (handles edge0 &gt; edge1).</summary>
    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = (x - edge0) / (edge1 - edge0);
        if (t < 0) t = 0; else if (t > 1) t = 1;
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>GLSL-style hash for micro-distortion noise.</summary>
    private static double Hash(double px, double py)
    {
        double s = Math.Sin(px * 127.1 + py * 311.7) * 43758.5453;
        return s - Math.Floor(s);
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
}
