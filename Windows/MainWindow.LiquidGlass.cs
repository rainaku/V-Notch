using System;
using System.Windows;
using System.Windows.Media;
using VNotch.Controllers;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class MainWindow
{
    private LiquidGlassController? _liquidGlass;

    private const string LiquidGlassStyleId = "liquidglass";

    private bool IsLiquidGlassEnabled =>
        string.Equals(_settings.NotchStyle, LiquidGlassStyleId, StringComparison.OrdinalIgnoreCase);

    private void ApplyLiquidGlassSkin()
    {
        if (GlassBackdropHost == null) return;

        if (IsLiquidGlassEnabled)
        {
            NotchBackground.Opacity = 0;
            ExpandedContent.Background = System.Windows.Media.Brushes.Transparent;

            GlassBackdropHost.Visibility = Visibility.Visible;
            GlassTintOverlay.Visibility = Visibility.Visible;
            GlassFresnelBorder.Visibility = Visibility.Visible;
            GlassSpecularOverlay.Visibility = Visibility.Visible;
            GlassRimBorder.Visibility = Visibility.Visible;

            _liquidGlass ??= new LiquidGlassController(
                GlassBackdropImage,
                () => _hwnd,
                GetGlassCaptureRegion,
                fps: 60);

            ApplyLiquidGlassConfig();
            _liquidGlass.Start();
            SyncGlassCornerRadius(NotchBorder.CornerRadius);
            ApplyGlassContentShadow(true);
            ApplyGlassToTimerBar(true);
            ApplyGlassToTimerFinishedView(true);
            ApplyGlassPanelMaterial(true);
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
            HideLyricsBlurBackground();
        }
        else
        {
            _liquidGlass?.Stop();

            ApplyGlassContentShadow(false);
            ApplyGlassToTimerBar(false);
            ApplyGlassToTimerFinishedView(false);
            ApplyGlassPanelMaterial(false);

            GlassBackdropHost.Visibility = Visibility.Collapsed;
            GlassTintOverlay.Visibility = Visibility.Collapsed;
            GlassFresnelBorder.Visibility = Visibility.Collapsed;
            GlassSpecularOverlay.Visibility = Visibility.Collapsed;
            GlassRimBorder.Visibility = Visibility.Collapsed;

            NotchBackground.Opacity = 1;
            ExpandedContent.Background = (System.Windows.Media.Brush)FindResource("NotchGradient");
            RestoreNotchShadowDefaults();
        }
    }

    private void ApplyLiquidGlassConfig()
    {
        if (GlassBackdropHost == null) return;
        var cfg = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();

        double dipRadius = Math.Clamp(cfg.BlurAmount, 0, 1) * 28.0;
        double boxScale = GetGlassDpiScale();
        int boxRadius = (int)Math.Round(dipRadius * boxScale / 3.0);
        _liquidGlass?.SetBlur(boxRadius);

        GlassBackdropHost.Opacity = Math.Clamp(cfg.Opacity, 0, 1);

        GlassRimBorder.BorderBrush = MakeWhite(Math.Clamp(cfg.EdgeHighlight, 0, 1) * 0.78);
        GlassSpecularOverlay.Opacity = Math.Clamp(cfg.Specular, 0, 1);
        GlassFresnelBorder.Opacity = Math.Clamp(cfg.Fresnel, 0, 1) * 0.6;

        SyncGlassCornerRadius(NotchBorder.CornerRadius);

        if (NotchShadowWrapper?.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
        {
            if (!_notchShadowDefaultsCaptured)
            {
                _notchShadowDefaultOpacity = dse.Opacity;
                _notchShadowDefaultBlur = dse.BlurRadius;
                _notchShadowDefaultsCaptured = true;
            }
            dse.Opacity = Math.Clamp(cfg.ShadowOpacity, 0, 1);
            dse.BlurRadius = Math.Clamp(cfg.ShadowSpread, 0, 60);
        }

        _liquidGlass?.SetParams(new LiquidGlassController.GlassParams
        {
            Refraction = cfg.Refraction,
            ChromaticAberration = cfg.ChromaticAberration,
            Distortion = cfg.Distortion,
            ZRadius = cfg.ZRadius,
            Saturation = cfg.Saturation,
            Brightness = cfg.Brightness,
            BevelMode = cfg.BevelMode
        });
    }

    private static SolidColorBrush MakeWhite(double alpha)
    {
        byte a = (byte)Math.Clamp(alpha * 255.0, 0, 255);
        var b = new SolidColorBrush(Color.FromArgb(a, 255, 255, 255));
        b.Freeze();
        return b;
    }

    // Liquid-glass "material" matching the audio redirect frame.
    private static readonly SolidColorBrush _glassPanelBg = Frozen(0x33, 0, 0, 0);
    private static readonly SolidColorBrush _glassPanelBorder = Frozen(0x26, 255, 255, 255);
    private static readonly SolidColorBrush _glassDashStroke = Frozen(0x40, 255, 255, 255);
    private static readonly SolidColorBrush _defaultPanelBg = Frozen(0xFF, 0x1A, 0x1A, 0x1A);
    private static readonly SolidColorBrush _defaultDashStroke = Frozen(0xFF, 0x33, 0x33, 0x33);
    private static readonly SolidColorBrush _cameraOverlayDefault = Frozen(0x40, 0, 0, 0);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Gives the camera box and file-tray the same translucent glass material as
    /// the audio redirect frame (only while the Liquid Glass skin is active), so
    /// the refracted backdrop shows through them. Restores the solid look otherwise.
    /// </summary>
    private void ApplyGlassPanelMaterial(bool glass)
    {
        if (CameraSection == null) return;

        if (glass)
        {
            CameraSection.Background = _glassPanelBg;
            CameraSection.BorderBrush = _glassPanelBorder;
            CameraSection.BorderThickness = new Thickness(1);
            // The camera icon overlay adds an extra dark wash on top of the glass
            // background, making the box darker than the file tray — clear it.
            if (CameraOverlay != null)
                CameraOverlay.Background = System.Windows.Media.Brushes.Transparent;

            FileShelf.Background = _glassPanelBg;
            FileShelf.BorderBrush = _glassPanelBorder;
            FileShelf.BorderThickness = new Thickness(1);

            if (FileShelfDashedBorder != null)
                FileShelfDashedBorder.Stroke = _glassDashStroke;
        }
        else
        {
            CameraSection.Background = _defaultPanelBg;
            CameraSection.BorderBrush = null;
            CameraSection.BorderThickness = new Thickness(0);
            if (CameraOverlay != null)
                CameraOverlay.Background = _cameraOverlayDefault;

            FileShelf.Background = _defaultPanelBg;
            FileShelf.BorderBrush = null;
            FileShelf.BorderThickness = new Thickness(0);

            if (FileShelfDashedBorder != null)
                FileShelfDashedBorder.Stroke = _defaultDashStroke;
        }
    }

    private bool _notchShadowDefaultsCaptured;
    private double _notchShadowDefaultOpacity = 0.6;
    private double _notchShadowDefaultBlur = 20;

    private void RestoreNotchShadowDefaults()
    {
        if (!_notchShadowDefaultsCaptured) return;
        if (NotchShadowWrapper?.Effect is System.Windows.Media.Effects.DropShadowEffect dse)
        {
            dse.Opacity = _notchShadowDefaultOpacity;
            dse.BlurRadius = _notchShadowDefaultBlur;
        }
    }

    private void SyncGlassCornerRadius(CornerRadius cr)
    {
        if (GlassBackdropHost == null) return;

        // Always follow the notch body's actual corner radius (already DI-aware
        // and animated by the caller) so the glass matches the notch border.
        GlassBackdropHost.CornerRadius = cr;
        GlassTintOverlay.CornerRadius = cr;
        GlassFresnelBorder.CornerRadius = cr;
        GlassSpecularOverlay.CornerRadius = cr;
        GlassRimBorder.CornerRadius = cr;
    }

    private double _glassDpiScale;

    private double GetGlassDpiScale()
    {
        if (_glassDpiScale > 0) return _glassDpiScale;
        if (_hwnd == IntPtr.Zero) return 1.0;
        uint dpi = GetDpiForWindow(_hwnd);
        _glassDpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
        return _glassDpiScale;
    }

    private void InvalidateGlassDpiScale() => _glassDpiScale = 0;

    private System.Windows.Media.Effects.DropShadowEffect? _glassContentShadow;

    private void ApplyGlassContentShadow(bool enable)
    {
        if (NotchContent == null) return;

        if (enable)
        {
            if (_glassContentShadow == null)
            {
                _glassContentShadow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 4,
                    ShadowDepth = 0,
                    Opacity = 0.85,
                    RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                };
                _glassContentShadow.Freeze();
            }
            NotchContent.Effect = _glassContentShadow;
        }
        else
        {
            NotchContent.Effect = null;
        }
    }

    private Brush? _timerBarDefaultBg;
    private bool _timerBarDefaultsCaptured;
    private Brush? _glassPanelTint;
    private void ApplyGlassToTimerBar(bool glass)
    {
        if (TimerControlBar == null) return;

        if (!_timerBarDefaultsCaptured)
        {
            _timerBarDefaultBg = TimerControlBar.Background;
            _timerBarDefaultsCaptured = true;
        }

        if (glass)
        {
            if (_glassPanelTint == null)
            {
                _glassPanelTint = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
                _glassPanelTint.Freeze();
            }
            TimerControlBar.Background = _glassPanelTint;
            if (TimerControlBarShadow != null) TimerControlBarShadow.Opacity = 0;
        }
        else
        {
            TimerControlBar.Background = _timerBarDefaultBg;
            if (TimerControlBarShadow != null) TimerControlBarShadow.Opacity = 0.45;
        }
    }

    // Glass material for the countdown "time's up" view: the full surface plus the
    // restart and dismiss buttons. Uses the SAME shared glass material as the rest of
    // the skin (_glassPanelTint for surfaces, _glassPanelBg + _glassPanelBorder for
    // panels/buttons) so it stays consistent with the configured Liquid Glass look.
    private bool _countdownGlassDefaultsCaptured;
    private Brush? _countdownSurfaceDefaultBg;
    private Brush? _countdownRestartDefaultBg;
    private Brush? _countdownDismissDefaultBg;
    private Brush? _countdownTextDefaultFg;

    private void ApplyGlassToTimerFinishedView(bool glass)
    {
        if (CountdownCompleteSurface == null) return;

        if (!_countdownGlassDefaultsCaptured)
        {
            _countdownSurfaceDefaultBg = CountdownCompleteSurface.Background;
            _countdownRestartDefaultBg = CountdownRestartBtn?.Background;
            _countdownDismissDefaultBg = CountdownDismissBtn?.Background;
            _countdownTextDefaultFg = CountdownCompleteText?.Foreground;
            _countdownGlassDefaultsCaptured = true;
        }

        if (glass)
        {
            if (_glassPanelTint == null)
            {
                _glassPanelTint = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
                _glassPanelTint.Freeze();
            }
            CountdownCompleteSurface.Background = _glassPanelTint;

            // The default alert orange (#FFFF9B3D) turns into a muddy, dim brown
            // over the live refracted backdrop. Use white so the "00:00" stays
            // legible, matching the other numbers on the Liquid Glass skin.
            if (CountdownCompleteText != null)
                CountdownCompleteText.Foreground = System.Windows.Media.Brushes.White;

            if (CountdownRestartBtn != null)
            {
                CountdownRestartBtn.Background = _glassPanelBg;
                CountdownRestartBtn.BorderBrush = _glassPanelBorder;
                CountdownRestartBtn.BorderThickness = new Thickness(1);
            }

            if (CountdownDismissBtn != null)
            {
                CountdownDismissBtn.Background = _glassPanelBg;
                CountdownDismissBtn.BorderBrush = _glassPanelBorder;
                CountdownDismissBtn.BorderThickness = new Thickness(1);
            }
        }
        else
        {
            CountdownCompleteSurface.Background = _countdownSurfaceDefaultBg;

            if (CountdownCompleteText != null && _countdownTextDefaultFg != null)
                CountdownCompleteText.Foreground = _countdownTextDefaultFg;

            if (CountdownRestartBtn != null)
            {
                CountdownRestartBtn.Background = _countdownRestartDefaultBg;
                CountdownRestartBtn.BorderBrush = null;
                CountdownRestartBtn.BorderThickness = new Thickness(0);
            }

            if (CountdownDismissBtn != null)
            {
                CountdownDismissBtn.Background = _countdownDismissDefaultBg;
                CountdownDismissBtn.BorderBrush = null;
                CountdownDismissBtn.BorderThickness = new Thickness(0);
            }
        }
    }

    private void HideLyricsBlurBackground()
    {
        if (LyricsBlurBackground == null) return;
        LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
        LyricsBlurBackground.Opacity = 0;
        LyricsBlurBackground.Visibility = Visibility.Collapsed;
    }

    private LiquidGlassController.CaptureRegion? GetGlassCaptureRegion()
    {
        if (_hwnd == IntPtr.Zero || !IsEffectivelyNotchVisible) return null;

        double notchW = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth;
        double notchH = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight;
        if (notchW <= 0 || notchH <= 0) return null;

        double dpiScale = GetGlassDpiScale();

        int physW = (int)Math.Round(notchW * dpiScale);
        int physH = (int)Math.Round(notchH * dpiScale);
        int physLeft = _fixedX + (int)Math.Round((_windowWidth - physW) / 2.0);
        int physTop = _fixedY;

        double translateY = NotchContainerTranslate?.Y ?? 0;
        physTop += (int)Math.Round(translateY * dpiScale);

        // The region is exactly behind the notch. The magnifier capture excludes
        // the notch itself, so there's no self-feedback (the BitBlt fallback adds
        // its own below-notch offset instead).

        if (physTop < 0) { physH += physTop; physTop = 0; }
        if (physLeft < 0) { physW += physLeft; physLeft = 0; }
        if (physW <= 1 || physH <= 1) return null;

        return new LiquidGlassController.CaptureRegion(physLeft, physTop, physW, physH);
    }
}
