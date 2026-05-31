using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;

namespace VNotch;
public partial class MainWindow
{
    #region Camera Logic

    private bool _isCameraActive = false;
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private int _cameraPreviewFadeToken = 0;
    private bool _cameraPreviewMorphPending = false;
    private const int CameraSectionExpandDurationMs = 420;
    private const int CameraSectionCollapseDurationMs = 420;

    // ─── Performance: frame throttling & buffer reuse ───
    private long _lastFrameTimestamp;
    private const long FrameIntervalTicks = 333_333; // ~30fps cap (10_000_000 / 30)
    private byte[]? _cameraFrameBuffer;
    private int _cameraFrameBufferSize;
    private WriteableBitmap? _cameraWriteableBitmap;
    private bool _cameraFrameDispatchPending = false;

    // ─── Lifecycle serialization: prevent race conditions on rapid click ───
    private bool _cameraStarting = false;
    private bool _cameraStopping = false;

    private void PrimeCameraPreviewMorphIn()
    {
        _cameraPreviewMorphPending = true;

        CameraPreviewImage.BeginAnimation(OpacityProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        CameraOverlay.BeginAnimation(OpacityProperty, null);

        CameraPreviewImage.Opacity = 0.0;
        CameraPreviewScale.ScaleX = 1.06;
        CameraPreviewScale.ScaleY = 1.06;
        CameraPreviewBlur.Radius = 16.0;

        CameraOverlay.Visibility = Visibility.Visible;
        CameraOverlay.Opacity = 1.0;
    }

    private void AnimateCameraPreviewMorphIn()
    {
        if (!_cameraPreviewMorphPending) return;
        _cameraPreviewMorphPending = false;

        int token = _cameraPreviewFadeToken;
        var morphDuration = new Duration(TimeSpan.FromMilliseconds(380));
        var overlayDuration = new Duration(TimeSpan.FromMilliseconds(320));

        var previewFadeIn = MakeAnim(CameraPreviewImage.Opacity, 0.8, morphDuration, _easeExpOut6, null);
        var scaleXIn = MakeAnim(CameraPreviewScale.ScaleX, 1.0, morphDuration, _easeExpOut6, null);
        var scaleYIn = MakeAnim(CameraPreviewScale.ScaleY, 1.0, morphDuration, _easeExpOut6, null);
        var blurClear = MakeAnim(CameraPreviewBlur.Radius, 0.0, morphDuration, _easeExpOut6, null);

        var overlayFadeOut = MakeAnim(CameraOverlay.Opacity, 0.0, overlayDuration, _easeQuadOut, TimeSpan.FromMilliseconds(40));
        overlayFadeOut.Completed += (s, e) =>
        {
            if (token != _cameraPreviewFadeToken || !_isCameraActive) return;
            CameraOverlay.BeginAnimation(OpacityProperty, null);
            CameraOverlay.Visibility = Visibility.Collapsed;
            CameraOverlay.Opacity = 0.0;
        };

        CameraPreviewImage.BeginAnimation(OpacityProperty, previewFadeIn, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXIn, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYIn, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, blurClear, HandoffBehavior.SnapshotAndReplace);
        CameraOverlay.BeginAnimation(OpacityProperty, overlayFadeOut, HandoffBehavior.SnapshotAndReplace);
    }

    private double ComputeCameraCornerRadius(bool expandedToShelf)
    {
        return expandedToShelf ? 16.0 : 12.0;
    }

    private void ApplyCameraCornerRadius(bool expandedToShelf)
    {
        double radius = ComputeCameraCornerRadius(expandedToShelf);

        double w = CameraSection.ActualWidth;
        double h = CameraSection.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Apply corner radius and use a RectangleGeometry clip to ensure all corners are rounded
        CameraSection.CornerRadius = new CornerRadius(radius);
        var clipRect = new RectangleGeometry(new System.Windows.Rect(0, 0, w, h), radius, radius);
        CameraSection.Clip = clipRect;
    }

    private void AnimateCameraSectionToShelf(bool expand)
    {
        if (!_isSecondaryView || _isAnimating) return;
        if (expand == _isCameraSectionExpanded) return;

        int token = ++_cameraSectionAnimToken;
        var duration = new Duration(TimeSpan.FromMilliseconds(expand ? CameraSectionExpandDurationMs : CameraSectionCollapseDurationMs));
        var easing = (IEasingFunction)_easeExpOut6;

        CameraSection.BeginAnimation(WidthProperty, null);
        CameraSection.BeginAnimation(HeightProperty, null);
        CameraSection.BeginAnimation(MarginProperty, null);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        FileShelf.BeginAnimation(OpacityProperty, null);
        ApplyCameraCornerRadius(expand);

        if (expand)
        {
            double compactWidth = CameraSection.ActualWidth;
            if (compactWidth <= 1)
            {
                compactWidth = _cameraSectionCompactWidth > 1 ? _cameraSectionCompactWidth : 120;
            }
            double compactHeight = CameraSection.ActualHeight;
            if (compactHeight <= 1)
            {
                compactHeight = _cameraSectionCompactHeight > 1 ? _cameraSectionCompactHeight : 100;
            }
            _cameraSectionCompactWidth = compactWidth;
            _cameraSectionCompactHeight = compactHeight;
            _cameraSectionCompactMargin = CameraSection.Margin;

            double targetWidth = SecondaryContent.ActualWidth;
            if (targetWidth <= 1)
            {
                SecondaryContent.UpdateLayout();
                targetWidth = SecondaryContent.ActualWidth;
            }
            if (targetWidth <= 1)
            {
                double shelfWidth = FileShelf.ActualWidth;
                targetWidth = shelfWidth > 1 ? (shelfWidth + compactWidth) : Math.Max(compactWidth, 320);
            }
            targetWidth = Math.Max(compactWidth, targetWidth);

            // Target height = target width to make it square
            double targetHeight = targetWidth;

            // Calculate new notch height to fit the square camera
            // SecondaryContent margin: top=30, bottom=12 => 42 total vertical margin
            double notchTargetHeight = targetHeight + 30 + 12 + 2; // camera height + margins + border
            double currentNotchHeight = _expandedHeight; // NotchBorder is at expanded height when in secondary view

            // Resize window to fit the new notch height
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double newWindowHeightDip = notchTargetHeight + 80;
            this.Height = newWindowHeightDip;
            _windowHeight = (int)Math.Round(newWindowHeightDip * dpiScale);
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);

            // Animate NotchBorder height
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = currentNotchHeight; // Set local value before starting new animation
            var notchHeightAnim = MakeAnim(currentNotchHeight, notchTargetHeight, duration, easing, null);
            NotchBorder.BeginAnimation(HeightProperty, notchHeightAnim, HandoffBehavior.SnapshotAndReplace);

            FileShelf.Visibility = Visibility.Visible;
            FileShelf.IsHitTestVisible = false;

            CameraSection.Width = compactWidth;
            CameraSection.Height = compactHeight;
            CameraSection.VerticalAlignment = VerticalAlignment.Top;
            CameraSection.HorizontalAlignment = HorizontalAlignment.Left;
            CameraSection.Margin = _cameraSectionCompactMargin;
            Grid.SetColumn(CameraSection, 0);
            Grid.SetColumnSpan(CameraSection, 2);
            Panel.SetZIndex(CameraSection, 10);

            var widthAnim = MakeAnim(compactWidth, targetWidth, duration, easing, null);
            var heightAnim = MakeAnim(compactHeight, targetHeight, duration, easing, null);
            var marginAnim = new ThicknessAnimation(CameraSection.Margin, new Thickness(0, 0, 0, 0), duration)
            {
                EasingFunction = easing
            };
            Timeline.SetDesiredFrameRate(marginAnim, 120);

            var shelfFadeOut = MakeAnim(FileShelf.Opacity, 0.0, new Duration(TimeSpan.FromMilliseconds(220)), _easeQuadOut, null);

            var squashX = new DoubleAnimationUsingKeyFrames { Duration = duration };
            squashX.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleX, KeyTime.FromPercent(0.0)));
            squashX.KeyFrames.Add(new EasingDoubleKeyFrame(0.985, KeyTime.FromPercent(0.34), _easeSineInOut));
            squashX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
            Timeline.SetDesiredFrameRate(squashX, 120);

            var squashY = new DoubleAnimationUsingKeyFrames { Duration = duration };
            squashY.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleY, KeyTime.FromPercent(0.0)));
            squashY.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromPercent(0.34), _easeSineInOut));
            squashY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
            Timeline.SetDesiredFrameRate(squashY, 120);

            widthAnim.Completed += (s, e) =>
            {
                if (token != _cameraSectionAnimToken) return;
                _isCameraSectionExpanded = true;
                CameraSection.BeginAnimation(WidthProperty, null);
                CameraSection.BeginAnimation(HeightProperty, null);
                CameraSection.Width = targetWidth;
                CameraSection.Height = targetHeight;
                CameraSection.BeginAnimation(MarginProperty, null);
                CameraSection.Margin = new Thickness(0);
                CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                CameraSectionScale.ScaleX = 1.0;
                CameraSectionScale.ScaleY = 1.0;
                ApplyCameraCornerRadius(true);
                FileShelf.BeginAnimation(OpacityProperty, null);
                FileShelf.Opacity = 0;
                FileShelf.Visibility = Visibility.Collapsed;

                // Finalize NotchBorder height
                NotchBorder.BeginAnimation(HeightProperty, null);
                NotchBorder.Height = notchTargetHeight;
            };

            CameraSection.BeginAnimation(WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
            CameraSection.BeginAnimation(HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
            CameraSection.BeginAnimation(MarginProperty, marginAnim, HandoffBehavior.SnapshotAndReplace);
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashX, HandoffBehavior.SnapshotAndReplace);
            CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashY, HandoffBehavior.SnapshotAndReplace);
            FileShelf.BeginAnimation(OpacityProperty, shelfFadeOut, HandoffBehavior.SnapshotAndReplace);
            return;
        }

        double currentWidth = CameraSection.ActualWidth;
        if (currentWidth <= 1)
        {
            currentWidth = (double.IsNaN(CameraSection.Width) || CameraSection.Width <= 1)
                ? (_cameraSectionCompactWidth > 1 ? _cameraSectionCompactWidth : 120)
                : CameraSection.Width;
        }
        double collapsedWidth = _cameraSectionCompactWidth > 1 ? _cameraSectionCompactWidth : currentWidth;

        double currentHeight = CameraSection.ActualHeight;
        if (currentHeight <= 1)
        {
            currentHeight = (double.IsNaN(CameraSection.Height) || CameraSection.Height <= 1)
                ? (_cameraSectionCompactHeight > 1 ? _cameraSectionCompactHeight : 100)
                : CameraSection.Height;
        }
        double collapsedHeight = _cameraSectionCompactHeight > 1 ? _cameraSectionCompactHeight : currentHeight;

        FileShelf.Visibility = Visibility.Visible;
        FileShelf.IsHitTestVisible = false;
        CameraSection.Width = currentWidth;
        CameraSection.Height = currentHeight;
        CameraSection.VerticalAlignment = VerticalAlignment.Top;
        CameraSection.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(CameraSection, 0);
        Grid.SetColumnSpan(CameraSection, 2);
        Panel.SetZIndex(CameraSection, 10);

        // Animate NotchBorder height back to expanded height
        double collapseNotchHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _expandedHeight;
        NotchBorder.BeginAnimation(HeightProperty, null);
        var notchHeightCollapseAnim = MakeAnim(collapseNotchHeight, _expandedHeight, duration, easing, null);
        notchHeightCollapseAnim.Completed += (s, e) =>
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _expandedHeight;
            // Resize window back
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double windowHeightDip = _expandedHeight + 80;
            this.Height = windowHeightDip;
            _windowHeight = (int)Math.Round(windowHeightDip * dpiScale);
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);
        };
        NotchBorder.BeginAnimation(HeightProperty, notchHeightCollapseAnim, HandoffBehavior.SnapshotAndReplace);

        var widthCollapseAnim = MakeAnim(currentWidth, collapsedWidth, duration, easing, null);
        var heightCollapseAnim = MakeAnim(currentHeight, collapsedHeight, duration, easing, null);
        var marginCollapseAnim = new ThicknessAnimation(CameraSection.Margin, _cameraSectionCompactMargin, duration)
        {
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(marginCollapseAnim, 120);

        var shelfFadeIn = MakeAnim(FileShelf.Opacity, 1.0, new Duration(TimeSpan.FromMilliseconds(260)), _easePowerOut3, null);

        var settleX = new DoubleAnimationUsingKeyFrames { Duration = duration };
        settleX.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleX, KeyTime.FromPercent(0.0)));
        settleX.KeyFrames.Add(new EasingDoubleKeyFrame(0.99, KeyTime.FromPercent(0.36), _easeSineInOut));
        settleX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(settleX, 120);

        var settleY = new DoubleAnimationUsingKeyFrames { Duration = duration };
        settleY.KeyFrames.Add(new EasingDoubleKeyFrame(CameraSectionScale.ScaleY, KeyTime.FromPercent(0.0)));
        settleY.KeyFrames.Add(new EasingDoubleKeyFrame(0.94, KeyTime.FromPercent(0.36), _easeSineInOut));
        settleY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeSineInOut));
        Timeline.SetDesiredFrameRate(settleY, 120);

        widthCollapseAnim.Completed += (s, e) =>
        {
            if (token != _cameraSectionAnimToken) return;
            _isCameraSectionExpanded = false;
            ResetCameraSectionLayoutInstant();
        };

        CameraSection.BeginAnimation(WidthProperty, widthCollapseAnim, HandoffBehavior.SnapshotAndReplace);
        CameraSection.BeginAnimation(HeightProperty, heightCollapseAnim, HandoffBehavior.SnapshotAndReplace);
        CameraSection.BeginAnimation(MarginProperty, marginCollapseAnim, HandoffBehavior.SnapshotAndReplace);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, settleX, HandoffBehavior.SnapshotAndReplace);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, settleY, HandoffBehavior.SnapshotAndReplace);
        FileShelf.BeginAnimation(OpacityProperty, shelfFadeIn, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetCameraSectionLayoutInstant()
    {
        _cameraSectionAnimToken++;
        _isCameraSectionExpanded = false;

        CameraSection.BeginAnimation(WidthProperty, null);
        CameraSection.BeginAnimation(HeightProperty, null);
        CameraSection.BeginAnimation(MarginProperty, null);
        CameraSection.Width = double.NaN;
        CameraSection.Height = double.NaN;
        CameraSection.Margin = _cameraSectionCompactMargin;
        CameraSection.HorizontalAlignment = HorizontalAlignment.Stretch;
        CameraSection.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(CameraSection, 0);
        Grid.SetColumnSpan(CameraSection, 1);
        Panel.SetZIndex(CameraSection, 0);
        ApplyCameraCornerRadius(false);

        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraSectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraSectionScale.ScaleX = 1.0;
        CameraSectionScale.ScaleY = 1.0;

        // Reset NotchBorder height only if it was expanded beyond normal for camera
        double currentNotchH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
        if (currentNotchH > _expandedHeight + 1)
        {
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Height = _expandedHeight;
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double windowHeightDip = _expandedHeight + 80;
            this.Height = windowHeightDip;
            _windowHeight = (int)Math.Round(windowHeightDip * dpiScale);
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, _fixedX, _fixedY, _windowWidth, _windowHeight, SWP_NOACTIVATE);
        }

        FileShelf.BeginAnimation(OpacityProperty, null);
        FileShelf.Opacity = 1.0;
        FileShelf.Visibility = Visibility.Visible;
        FileShelf.IsHitTestVisible = true;

        if (_isSecondaryView)
        {
        }
    }

    private void CameraSection_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; 

        if (_isAnimating || !_isSecondaryView) return;
        if (_cameraStarting || _cameraStopping) return; // block rapid clicks

        if (!_isCameraActive)
        {
            AnimateCameraSectionToShelf(true);
            StartCameraPreview();
        }
        else
        {
            StopCameraPreview();
        }
    }

    private void CameraSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyCameraCornerRadius(_isCameraSectionExpanded);
    }

    private async void StartCameraPreview()
    {
        if (_isCameraActive && _frameReader != null) return;
        if (_cameraStarting) return;

        _cameraStarting = true;
        try
        {
            _cameraPreviewFadeToken++;
            CameraPreviewImage.BeginAnimation(OpacityProperty, null);
            CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            PrimeCameraPreviewMorphIn();
            _isCameraActive = true;
            _lastFrameTimestamp = 0;
            CameraErrorOverlay.Visibility = Visibility.Collapsed;

            // ─── Offload all heavy camera init to background thread ───
            var cameraDeviceId = _settings.CameraDeviceId;
            var (mediaCapture, frameReader, errorMsg) = await Task.Run(async () =>
            {
                try
                {
                    var groups = await MediaFrameSourceGroup.FindAllAsync();
                    MediaFrameSourceGroup? group = null;

                    if (!string.IsNullOrEmpty(cameraDeviceId))
                    {
                        group = groups.FirstOrDefault(g => g.Id == cameraDeviceId &&
                            g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));
                    }

                    group ??= groups.FirstOrDefault(g =>
                        g.SourceInfos.Any(s => s.SourceKind == MediaFrameSourceKind.Color));

                    if (group == null)
                        return ((MediaCapture?)null, (MediaFrameReader?)null, "Cannot detect camera device");

                    var capture = new MediaCapture();
                    var initSettings = new MediaCaptureInitializationSettings
                    {
                        SourceGroup = group,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                        StreamingCaptureMode = StreamingCaptureMode.Video
                    };

                    await capture.InitializeAsync(initSettings);

                    var colorSource = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
                    if (colorSource == null)
                    {
                        capture.Dispose();
                        return ((MediaCapture?)null, (MediaFrameReader?)null, "Cannot detect color source");
                    }

                    var reader = await capture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
                    return (capture, reader, (string?)null);
                }
                catch (Exception ex)
                {
                    return ((MediaCapture?)null, (MediaFrameReader?)null, ex.Message);
                }
            });

            if (!_isCameraActive)
            {
                // User cancelled during init — dispose on background
                _ = Task.Run(() =>
                {
                    frameReader?.Dispose();
                    mediaCapture?.Dispose();
                });
                return;
            }

            if (mediaCapture == null || frameReader == null)
                throw new Exception(errorMsg ?? "Cannot detect camera device");

            _mediaCapture = mediaCapture;
            _frameReader = frameReader;
            _frameReader.FrameArrived += FrameReader_FrameArrived;
            await _frameReader.StartAsync();
        }
        catch (Exception ex)
        {
            StopCameraPreviewSafe();
            CameraOverlay.Visibility = Visibility.Collapsed;
            CameraErrorOverlay.Visibility = Visibility.Visible;
            RuntimeLog.Error("CAMERA", ex, "Camera initialization failed");
        }
        finally
        {
            _cameraStarting = false;
        }
    }
    private void StopCameraPreviewSafe()
    {
        _isCameraActive = false;
        _cameraPreviewMorphPending = false;
        _cameraPreviewFadeToken++;
        _cameraFrameBuffer = null;
        _cameraFrameBufferSize = 0;
        _cameraWriteableBitmap = null;
        _cameraFrameDispatchPending = false;

        if (_frameReader != null)
        {
            _frameReader.FrameArrived -= FrameReader_FrameArrived;
            _frameReader.Dispose();
            _frameReader = null;
        }

        if (_mediaCapture != null)
        {
            _mediaCapture.Dispose();
            _mediaCapture = null;
        }
    }

    private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // ─── Frame throttle: skip frames if we're rendering faster than 30fps ───
        long now = Stopwatch.GetTimestamp();
        if (now - _lastFrameTimestamp < FrameIntervalTicks)
            return;
        _lastFrameTimestamp = now;

        // ─── Coalesce: skip if previous frame hasn't been rendered yet ───
        if (_cameraFrameDispatchPending)
            return;

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
        if (_cameraFrameBuffer == null || _cameraFrameBufferSize < requiredSize)
        {
            _cameraFrameBuffer = new byte[requiredSize];
            _cameraFrameBufferSize = requiredSize;
        }

        softwareBitmap.CopyToBuffer(_cameraFrameBuffer.AsBuffer());
        softwareBitmap.Dispose();

        // Capture buffer reference and dimensions for the closure
        var buffer = _cameraFrameBuffer;
        int w = width;
        int h = height;

        _cameraFrameDispatchPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            _cameraFrameDispatchPending = false;
            if (!_isCameraActive) return;
            try
            {
                var wbmp = _cameraWriteableBitmap;
                if (wbmp == null || wbmp.PixelWidth != w || wbmp.PixelHeight != h)
                {
                    wbmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    _cameraWriteableBitmap = wbmp;
                    CameraPreviewImage.Source = wbmp;
                }

                wbmp.WritePixels(new Int32Rect(0, 0, w, h), buffer, w * 4, 0);

                if (_cameraPreviewMorphPending)
                {
                    AnimateCameraPreviewMorphIn();
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("CAMERA", ex, "Frame update failed");
            }
        });
    }

    private async void StopCameraPreview()
    {
        if (_cameraStopping) return;
        _cameraStopping = true;

        try
        {
            AnimateCameraSectionToShelf(false);
            _isCameraActive = false;
            _cameraPreviewMorphPending = false;
            _cameraFrameBuffer = null;
            _cameraFrameBufferSize = 0;
            _cameraWriteableBitmap = null;
            _cameraFrameDispatchPending = false;
            int fadeToken = ++_cameraPreviewFadeToken;

            // ─── Capture references and detach immediately to stop frame flow ───
            var reader = _frameReader;
            var capture = _mediaCapture;
            _frameReader = null;
            _mediaCapture = null;

            if (reader != null)
            {
                reader.FrameArrived -= FrameReader_FrameArrived;
            }

        CameraOverlay.BeginAnimation(OpacityProperty, null);
        CameraOverlay.Visibility = Visibility.Visible;
        CameraOverlay.Opacity = 0.0;
        CameraErrorOverlay.Visibility = Visibility.Collapsed;

        CameraPreviewImage.BeginAnimation(OpacityProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        double previewFrom = CameraPreviewImage.Opacity > 0 ? CameraPreviewImage.Opacity : 0.8;
        var previewFadeOut = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
        };
        previewFadeOut.KeyFrames.Add(new EasingDoubleKeyFrame(previewFrom, KeyTime.FromPercent(0.0)));
        previewFadeOut.KeyFrames.Add(new EasingDoubleKeyFrame(previewFrom, KeyTime.FromPercent(0.72), _easeSineInOut));
        previewFadeOut.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), _easeExpOut6));
        Timeline.SetDesiredFrameRate(previewFadeOut, 120);

        var overlayFadeIn = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
        };
        overlayFadeIn.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
        overlayFadeIn.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(0.66), _easeSineInOut));
        overlayFadeIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), _easeExpOut6));
        Timeline.SetDesiredFrameRate(overlayFadeIn, 120);

        var previewScaleOutX = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
        };
        previewScaleOutX.KeyFrames.Add(new EasingDoubleKeyFrame(CameraPreviewScale.ScaleX, KeyTime.FromPercent(0.0)));
        previewScaleOutX.KeyFrames.Add(new EasingDoubleKeyFrame(CameraPreviewScale.ScaleX, KeyTime.FromPercent(0.72), _easeSineInOut));
        previewScaleOutX.KeyFrames.Add(new EasingDoubleKeyFrame(1.04, KeyTime.FromPercent(1.0), _easeExpOut6));
        Timeline.SetDesiredFrameRate(previewScaleOutX, 120);

        var previewScaleOutY = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
        };
        previewScaleOutY.KeyFrames.Add(new EasingDoubleKeyFrame(CameraPreviewScale.ScaleY, KeyTime.FromPercent(0.0)));
        previewScaleOutY.KeyFrames.Add(new EasingDoubleKeyFrame(CameraPreviewScale.ScaleY, KeyTime.FromPercent(0.72), _easeSineInOut));
        previewScaleOutY.KeyFrames.Add(new EasingDoubleKeyFrame(1.04, KeyTime.FromPercent(1.0), _easeExpOut6));
        Timeline.SetDesiredFrameRate(previewScaleOutY, 120);

        var previewBlurOut = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(CameraSectionCollapseDurationMs))
        };
        previewBlurOut.KeyFrames.Add(new EasingDoubleKeyFrame(CameraPreviewBlur.Radius, KeyTime.FromPercent(0.0)));
        previewBlurOut.KeyFrames.Add(new EasingDoubleKeyFrame(CameraPreviewBlur.Radius, KeyTime.FromPercent(0.70), _easeSineInOut));
        previewBlurOut.KeyFrames.Add(new EasingDoubleKeyFrame(16.0, KeyTime.FromPercent(1.0), _easeExpOut6));
        Timeline.SetDesiredFrameRate(previewBlurOut, 120);

        previewFadeOut.Completed += (s, e) =>
        {
            if (fadeToken != _cameraPreviewFadeToken || _isCameraActive) return;
            CameraPreviewImage.BeginAnimation(OpacityProperty, null);
            CameraPreviewImage.Opacity = 0;
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CameraPreviewScale.ScaleX = 1.06;
            CameraPreviewScale.ScaleY = 1.06;
            CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CameraPreviewBlur.Radius = 16.0;
            CameraPreviewImage.Source = null;
            _cameraWriteableBitmap = null; // ← FIX: Ensure bitmap is cleared after animation
        };
        CameraPreviewImage.BeginAnimation(OpacityProperty, previewFadeOut, HandoffBehavior.SnapshotAndReplace);
        CameraOverlay.BeginAnimation(OpacityProperty, overlayFadeIn, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleXProperty, previewScaleOutX, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewScale.BeginAnimation(ScaleTransform.ScaleYProperty, previewScaleOutY, HandoffBehavior.SnapshotAndReplace);
        CameraPreviewBlur.BeginAnimation(BlurEffect.RadiusProperty, previewBlurOut, HandoffBehavior.SnapshotAndReplace);

            // ─── Offload heavy stop/dispose to background thread ───
            await Task.Run(async () =>
            {
                try
                {
                    if (reader != null)
                    {
                        await reader.StopAsync();
                        reader.Dispose();
                    }
                    capture?.Dispose();
                }
                catch { /* swallow dispose errors */ }
            });
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("CAMERA", ex, "StopCameraPreview failed");
            StopCameraPreviewSafe();
        }
        finally
        {
            _cameraStopping = false;
        }
    }

    #endregion
}

