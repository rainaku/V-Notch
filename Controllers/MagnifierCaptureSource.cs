using System;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class MagnifierCaptureSource : IDisposable
{
    // Set true if colours come out with red/blue swapped on a given machine.
    private static readonly bool SwapRedBlue = false;

    private const string MagDll = "Magnification.dll";
    private const string WC_MAGNIFIER = "Magnifier";
    private const int MW_FILTERMODE_EXCLUDE = 0;

    private const int WS_CHILD = 0x40000000;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int LWA_ALPHA = 0x2;
    private const int SW_SHOWNA = 8;
    private const uint PM_REMOVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGIMAGEHEADER
    {
        public uint width;
        public uint height;
        public Guid format;
        public uint stride;
        public uint offset;
        public UIntPtr cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptx;
        public int pty;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MagImageScalingCallback(
        IntPtr hwnd, IntPtr srcdata, MAGIMAGEHEADER srcheader,
        IntPtr destdata, MAGIMAGEHEADER destheader,
        Win32Interop.RECT unclipped, Win32Interop.RECT clipped, IntPtr dirty);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(MagDll)] private static extern bool MagInitialize();
    [DllImport(MagDll)] private static extern bool MagUninitialize();
    [DllImport(MagDll)] private static extern bool MagSetWindowSource(IntPtr hwnd, Win32Interop.RECT rect);
    [DllImport(MagDll)] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int dwFilterMode, int count, IntPtr[] pHWND);
    [DllImport(MagDll)] private static extern bool MagSetImageScalingCallback(IntPtr hwnd, MagImageScalingCallback callback);

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte alpha, int flags);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _initDone = new(false);
    private readonly AutoResetEvent _request = new(false);
    private readonly AutoResetEvent _done = new(false);

    private IntPtr _excludeHwnd;
    private IntPtr _hostWnd;
    private IntPtr _magWnd;
    private MagImageScalingCallback? _callback;   // keep alive
    private WndProcDelegate? _wndProc;             // keep alive

    // Fixed magnifier window size; the scaling callback always receives the source
    // region at its native resolution regardless of window size, so we never resize
    // the window per frame (which caused black flicker).
    private const int MagWindowW = 1600;
    private const int MagWindowH = 700;

    // Request/response. A request id prevents a completion from a timed-out frame
    // being mistaken for the next frame, and the lock snapshots all coordinates as
    // one unit so the pump can never combine two requests.
    private readonly object _requestSync = new();
    private CaptureRequest _pendingRequest;
    private long _requestSequence;
    private long _completedRequestId;
    private volatile bool _captureResult;

    private readonly record struct CaptureRequest(long Id, int X, int Y, int Width, int Height);

    // Callback state (pump thread only). The callback copies the source into our
    // OWN buffer; DoCapture then copies that into the caller's buffer synchronously
    // (while the caller is blocked), so the callback never touches caller memory
    // that could be freed on another thread.
    private byte[] _ownBuffer = Array.Empty<byte>();
    private int _ownWidth, _ownRows, _ownStride;
    private volatile bool _received;

    // Completed frames live in a separate tightly-packed buffer. Magnification can
    // invoke the callback again while the pump drains messages; keeping that traffic
    // away from the completed buffer prevents a late callback from tearing a frame
    // while the render worker copies it into the staging DIB.
    private byte[] _completedBuffer = Array.Empty<byte>();
    private int _completedWidth, _completedHeight;

    public bool IsReady { get; private set; }

    public bool Initialize(IntPtr excludeHwnd)
    {
        _excludeHwnd = excludeHwnd;
        _running = true;
        _thread = new Thread(PumpThread)
        {
            IsBackground = true,
            Name = "LiquidGlassMagnifier"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Wait for the pump thread to finish (or fail) initialization.
        _initDone.Wait(2000);
        return IsReady;
    }

    /// <summary>
    /// Captures the screen rectangle (physical pixels) into <paramref name="destBits"/>
    /// as top-down BGRA with stride = w*4. Blocks until the magnifier delivers a frame
    /// or a short timeout elapses. Safe to call from any thread.
    /// </summary>
    public bool CaptureInto(int x, int y, int w, int h, IntPtr destBits)
    {
        if (!IsReady || !_running || destBits == IntPtr.Zero || w <= 0 || h <= 0) return false;

        long requestId = Interlocked.Increment(ref _requestSequence);
        lock (_requestSync)
            _pendingRequest = new CaptureRequest(requestId, x, y, w, h);

        _request.Set();

        // A timed-out request can complete after the next request has started. Wait
        // only for our own id and ignore stale completion signals within the same
        // deadline instead of copying the wrong-sized/partially updated frame.
        long deadline = Environment.TickCount64 + 60;
        while (Volatile.Read(ref _completedRequestId) != requestId)
        {
            int remaining = (int)Math.Clamp(deadline - Environment.TickCount64, 0, 60);
            if (remaining == 0 || !_done.WaitOne(remaining)) return false;
        }
        if (!_captureResult) return false;

        // Copy the captured pixels into the caller's buffer HERE, on the caller
        // thread. The completed buffer is immutable until the render worker's next
        // serial request, so neither side can change memory during this copy (which
        // previously caused an AccessViolation when a capture overran the timeout).
        try
        {
            return CopyToDest(destBits, w, h);
        }
        catch
        {
            return false;
        }
    }

    private void PumpThread()
    {
        try
        {
            if (!MagInitialize())
            {
                RuntimeLog.Log("LIQUIDGLASS", "MagInitialize failed.");
                _initDone.Set();
                return;
            }

            IntPtr hInst = GetModuleHandleW(null);
            const string hostClass = "VNotchMagHost";
            _wndProc = DefWindowProcW;
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                lpszClassName = hostClass
            };
            RegisterClassW(ref wc);   // harmless if already registered

            _hostWnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW,
                hostClass, "VNotchMagHost", WS_POPUP,
                0, 0, MagWindowW, MagWindowH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hostWnd == IntPtr.Zero) { RuntimeLog.Log("LIQUIDGLASS", "Mag host create failed."); Cleanup(); _initDone.Set(); return; }

            SetLayeredWindowAttributes(_hostWnd, 0, 0, LWA_ALPHA);   // invisible to user
            ShowWindow(_hostWnd, SW_SHOWNA);

            _magWnd = CreateWindowExW(
                0, WC_MAGNIFIER, "VNotchMag", (uint)(WS_CHILD | WS_VISIBLE),
                0, 0, MagWindowW, MagWindowH, _hostWnd, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_magWnd == IntPtr.Zero) { RuntimeLog.Log("LIQUIDGLASS", "Mag control create failed."); Cleanup(); _initDone.Set(); return; }

            var exclude = new[] { _excludeHwnd, _hostWnd, _magWnd };
            MagSetWindowFilterList(_magWnd, MW_FILTERMODE_EXCLUDE, exclude.Length, exclude);

            _callback = ScalingCallback;
            if (!MagSetImageScalingCallback(_magWnd, _callback))
            {
                RuntimeLog.Log("LIQUIDGLASS", "MagSetImageScalingCallback unsupported.");
                Cleanup(); _initDone.Set(); return;
            }

            IsReady = true;
            _initDone.Set();

            // Pump loop: service capture requests and keep the message queue drained.
            while (_running)
            {
                if (_request.WaitOne(15))
                {
                    if (!_running) break;
                    CaptureRequest request;
                    lock (_requestSync) request = _pendingRequest;

                    try { DoCapture(request); }
                    catch (Exception ex) { _captureResult = false; RuntimeLog.Log("LIQUIDGLASS", $"Mag capture failed: {ex.Message}"); }
                    Volatile.Write(ref _completedRequestId, request.Id);
                    _done.Set();
                }
                DrainMessages();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("LIQUIDGLASS", $"Magnifier pump exception: {ex.Message}");
            _initDone.Set();
        }
        finally
        {
            Cleanup();
        }
    }

    private void DoCapture(CaptureRequest request)
    {
        int w = request.Width, h = request.Height;
        if (w <= 0 || h <= 0) { _captureResult = false; return; }

        _received = false;

        var rect = new Win32Interop.RECT
        {
            Left = request.X,
            Top = request.Y,
            Right = request.X + w,
            Bottom = request.Y + h
        };
        if (!MagSetWindowSource(_magWnd, rect))
        {
            _captureResult = false;
            return;
        }

        // The scaling callback may fire synchronously inside MagSetWindowSource or
        // on a subsequent paint — pump briefly until it delivers into _ownBuffer.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_received && sw.Elapsed.TotalMilliseconds < 12)
        {
            if (!DrainMessages())
                Thread.Sleep(0);
        }

        if (!_received)
        {
            _captureResult = false;
            return;
        }

        // Never accept a callback that did not supply the entire requested image.
        // CopyToDest used to copy the available rows and still return success,
        // leaving the rest of a newly allocated staging DIB black for one frame.
        if (!IsCompleteFrame(w, h, _ownWidth, _ownRows, _ownStride, _ownBuffer.Length) ||
            IsBlankFrame(w, h))
        {
            _captureResult = false;
            return;
        }

        SnapshotCompletedFrame(w, h);
        _captureResult = true;
    }

    internal static bool IsCompleteFrame(
        int requestedWidth,
        int requestedHeight,
        int receivedWidth,
        int receivedHeight,
        int receivedStride,
        int bufferLength)
    {
        if (requestedWidth <= 0 || requestedHeight <= 0 ||
            receivedWidth < requestedWidth || receivedHeight < requestedHeight)
            return false;

        long rowBytes = (long)requestedWidth * 4;
        if (receivedStride < rowBytes) return false;

        long requiredBytes = ((long)requestedHeight - 1) * receivedStride + rowBytes;
        return requiredBytes <= bufferLength;
    }

    private unsafe bool IsBlankFrame(int w, int h)
    {
        if (_ownBuffer.Length == 0 || _ownRows == 0 || _ownStride == 0) return true;
        int rows = Math.Min(_ownRows, h);
        if (rows <= 0) return true;

        // Sample a small grid; if every sampled pixel has RGB == 0 it's blank.
        const int steps = 6;
        fixed (byte* baseP = _ownBuffer)
        {
            for (int gy = 0; gy < steps; gy++)
            {
                int y = (rows - 1) * gy / (steps - 1);
                byte* rowP = baseP + y * _ownStride;
                int pixels = Math.Min(_ownWidth, w);
                if (pixels <= 0) return true;
                for (int gx = 0; gx < steps; gx++)
                {
                    int x = (pixels - 1) * gx / (steps - 1);
                    int o = x << 2;
                    if (rowP[o] != 0 || rowP[o + 1] != 0 || rowP[o + 2] != 0)
                        return false;
                }
            }
        }
        return true;
    }

    private void SnapshotCompletedFrame(int width, int height)
    {
        int dstStride = checked(width * 4);
        int needed = checked(dstStride * height);
        if (_completedBuffer.Length < needed)
            _completedBuffer = new byte[needed];

        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(_ownBuffer, row * _ownStride, _completedBuffer, row * dstStride, dstStride);

        _completedWidth = width;
        _completedHeight = height;
    }

    private unsafe bool CopyToDest(IntPtr dest, int destW, int destH)
    {
        byte[] src = _completedBuffer;
        if (src.Length == 0 || _completedWidth != destW || _completedHeight != destH)
            return false;

        int stride = checked(destW * 4);
        int bytes = checked(stride * destH);
        if (src.Length < bytes) return false;

        fixed (byte* srcBase = src)
            Buffer.MemoryCopy(srcBase, (void*)dest, bytes, bytes);
        return true;
    }

    private bool DrainMessages()
    {
        bool any = false;
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            any = true;
        }
        return any;
    }

    private unsafe bool ScalingCallback(IntPtr hwnd, IntPtr srcdata, MAGIMAGEHEADER srcheader,
        IntPtr destdata, MAGIMAGEHEADER destheader,
        Win32Interop.RECT unclipped, Win32Interop.RECT clipped, IntPtr dirty)
    {
        try
        {
            if (srcdata == IntPtr.Zero) return false;

            int rows = (int)srcheader.height;
            int srcStride = (int)srcheader.stride;
            if (rows <= 0 || srcStride <= 0 || rows > 4096 || srcStride > 1 << 18) return false;

            int needed = rows * srcStride;
            if (_ownBuffer.Length < needed)
                _ownBuffer = new byte[needed];

            byte* src = (byte*)srcdata;
            if (!SwapRedBlue)
            {
                fixed (byte* dst = _ownBuffer)
                    Buffer.MemoryCopy(src, dst, _ownBuffer.Length, (long)needed);
            }
            else
            {
                int pixels = srcStride / 4;
                fixed (byte* dstBase = _ownBuffer)
                {
                    for (int row = 0; row < rows; row++)
                    {
                        byte* s = src + row * srcStride;
                        byte* d = dstBase + row * srcStride;
                        for (int p = 0; p < pixels; p++)
                        {
                            int o = p << 2;
                            d[o + 0] = s[o + 2];
                            d[o + 1] = s[o + 1];
                            d[o + 2] = s[o + 0];
                            d[o + 3] = s[o + 3];
                        }
                    }
                }
            }

            _ownWidth = (int)srcheader.width;
            _ownRows = rows;
            _ownStride = srcStride;
            _received = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Cleanup()
    {
        IsReady = false;
        try
        {
            if (_magWnd != IntPtr.Zero) { DestroyWindow(_magWnd); _magWnd = IntPtr.Zero; }
            if (_hostWnd != IntPtr.Zero) { DestroyWindow(_hostWnd); _hostWnd = IntPtr.Zero; }
            MagUninitialize();
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        _running = false;
        IsReady = false;
        _request.Set();   // wake the pump thread so it can exit
        try { _thread?.Join(500); } catch { /* ignore */ }
        _thread = null;
        _callback = null;
        _wndProc = null;
    }
}
