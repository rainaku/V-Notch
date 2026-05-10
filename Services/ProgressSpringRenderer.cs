using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VNotch.Services;

/// <summary>
/// Critically-damped spring that follows the progress-bar target ratio during
/// user seeks. Hooks <see cref="CompositionTarget.Rendering"/> for per-frame
/// updates and drives a hidden 144fps animation to pin the render thread at
/// high frame rate. When the spring settles (or its safety timeout elapses)
/// the loop unhooks itself automatically.
///
/// Extracted from <c>MainWindow.Progress.cs</c> so the physics, the render
/// hook plumbing, and the fps-boost animation no longer live on
/// <see cref="VNotch.MainWindow"/>.
/// </summary>
internal sealed class ProgressSpringRenderer
{
    #region Tuning

    private const double SpringStiffness = 105.0;
    private const double SpringDamping = 28.0;
    private const double SpringSettleThreshold = 0.0012;
    private const double SpringTargetFollowSpeed = 30.0;
    private const double SpringMaxVelocity = 2.4;
    private const double SpringMaxStepPerFrame = 0.030;
    private const int SpringSettleFramesRequired = 3;
    private const int SpringTimeoutMs = 1400;

    #endregion

    #region Collaborators & state

    private readonly Action<double> _applyRatio;
    private readonly Func<bool> _shouldRender;
    private readonly Func<double> _getPlaybackRate;

    private readonly Stopwatch _stopwatch = new();
    private readonly TranslateTransform _fpsBoostTarget = new();
    private DoubleAnimation? _fpsBoostAnim;

    private bool _hooked;
    private bool _active;
    private DateTime _startTimeUtc = DateTime.MinValue;

    private double _displayRatio;
    private double _targetRatio;
    private double _springTargetRatio;
    private double _velocity;
    private int _settleFrames;

    #endregion

    public ProgressSpringRenderer(
        Action<double> applyRatio,
        Func<bool> shouldRender,
        Func<double> getPlaybackRate)
    {
        _applyRatio = applyRatio ?? throw new ArgumentNullException(nameof(applyRatio));
        _shouldRender = shouldRender ?? throw new ArgumentNullException(nameof(shouldRender));
        _getPlaybackRate = getPlaybackRate ?? throw new ArgumentNullException(nameof(getPlaybackRate));
    }

    #region Public state

    public bool IsActive => _active;

    public bool IsHooked => _hooked;

    public double DisplayRatio
    {
        get => _displayRatio;
        set => _displayRatio = value;
    }

    public double TargetRatio
    {
        get => _targetRatio;
        set => _targetRatio = value;
    }

    public double SpringTargetRatio
    {
        get => _springTargetRatio;
        set => _springTargetRatio = value;
    }

    public double Velocity
    {
        get => _velocity;
        set => _velocity = value;
    }

    public int SettleFrames
    {
        get => _settleFrames;
        set => _settleFrames = value;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Start a new spring animation. The caller is expected to have already set
    /// <see cref="DisplayRatio"/>, <see cref="TargetRatio"/> and
    /// <see cref="SpringTargetRatio"/>.
    /// </summary>
    public void Start()
    {
        _active = true;
        _startTimeUtc = DateTime.UtcNow;
        Hook();
    }

    /// <summary>Stop the spring render hook and release the fps-boost animation.</summary>
    public void Stop()
    {
        _active = false;
        _settleFrames = 0;
        _velocity = 0;
        Unhook();
    }

    /// <summary>Hook the render callback if not already hooked.</summary>
    public void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        _stopwatch.Restart();

        if (_fpsBoostAnim == null)
        {
            _fpsBoostAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(_fpsBoostAnim, 144);
            _fpsBoostAnim.Freeze();
        }

        _fpsBoostTarget.BeginAnimation(TranslateTransform.XProperty, _fpsBoostAnim);
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Unhook the render callback.</summary>
    public void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        _stopwatch.Stop();

        CompositionTarget.Rendering -= OnRendering;
        _fpsBoostTarget.BeginAnimation(TranslateTransform.XProperty, null);
    }

    #endregion

    #region Render loop

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_active)
        {
            Stop();
            return;
        }

        if (!_shouldRender())
        {
            return;
        }

        RenderFrame();
    }

    private void RenderFrame()
    {
        double dt = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();
        dt = Math.Clamp(dt, 0.001, 0.033);

        double rate = _getPlaybackRate();
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0)
        {
            rate = 1.0;
        }
        rate = Math.Clamp(rate, 0.5, 3.0);

        double scaledDt = dt * rate;

        double targetFollow = 1.0 - Math.Exp(-SpringTargetFollowSpeed * scaledDt);
        _springTargetRatio += (_targetRatio - _springTargetRatio) * targetFollow;

        double error = _springTargetRatio - _displayRatio;

        double springForce = SpringStiffness * error - SpringDamping * _velocity;
        _velocity += springForce * scaledDt;
        _velocity = Math.Clamp(_velocity, -SpringMaxVelocity, SpringMaxVelocity);

        double prevDisplay = _displayRatio;
        _displayRatio += _velocity * scaledDt;

        double step = _displayRatio - prevDisplay;
        if (Math.Abs(step) > SpringMaxStepPerFrame)
        {
            _displayRatio = prevDisplay + Math.Sign(step) * SpringMaxStepPerFrame;
        }

        // Crossed the spring target: snap to it to prevent oscillation jitter.
        if ((prevDisplay - _springTargetRatio) * (_displayRatio - _springTargetRatio) < 0)
        {
            _displayRatio = _springTargetRatio;
            _velocity = 0;
        }

        if (Math.Abs(_targetRatio - _displayRatio) < SpringSettleThreshold &&
            Math.Abs(_velocity) < 0.004)
        {
            _settleFrames++;
        }
        else
        {
            _settleFrames = 0;
        }

        if (_settleFrames >= SpringSettleFramesRequired)
        {
            _displayRatio = _targetRatio;
            _velocity = 0;
            _settleFrames = 0;
            _active = false;
            Unhook();
        }

        if ((DateTime.UtcNow - _startTimeUtc).TotalMilliseconds > SpringTimeoutMs)
        {
            _displayRatio = _targetRatio;
            _velocity = 0;
            _settleFrames = 0;
            _active = false;
            Unhook();
        }

        _displayRatio = Math.Clamp(_displayRatio, 0, 1);
        _applyRatio(_displayRatio);
    }

    #endregion
}
