using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace VNotch.Controls
{
    public enum VisualizerState
    {
        Idle,
        Playing,
        Paused,
        Seeking
    }

    public class MusicVisualizer : FrameworkElement
    {
        #region Dependency Properties

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(MusicVisualizer),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty IsBufferingProperty =
            DependencyProperty.Register(nameof(IsBuffering), typeof(bool), typeof(MusicVisualizer),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty TrackIdProperty =
            DependencyProperty.Register(nameof(TrackId), typeof(string), typeof(MusicVisualizer),
                new PropertyMetadata(string.Empty, OnStateChanged));

        public static readonly DependencyProperty ActiveBrushProperty =
            DependencyProperty.Register(nameof(ActiveBrush), typeof(Brush), typeof(MusicVisualizer),
                new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        public bool IsBuffering
        {
            get => (bool)GetValue(IsBufferingProperty);
            set => SetValue(IsBufferingProperty, value);
        }

        public string TrackId
        {
            get => (string)GetValue(TrackIdProperty);
            set => SetValue(TrackIdProperty, value);
        }

        public Brush ActiveBrush
        {
            get => (Brush)GetValue(ActiveBrushProperty);
            set => SetValue(ActiveBrushProperty, value);
        }

        #endregion

        #region Constants & Config

        private const int BarCount = 3;
        private const double MinHeightRatio = 0.2;
        private const double MaxHeightRatio = 0.9;
        private const double BarWidthRatio = 0.22; // Percent of total width per bar
        private const double BarSpacingRatio = 0.12;
        private const double CornerRadiusRatio = 0.5; // Circle-like ends

        // Smoothing tau (ms)
        private const double TauPlaying = 100;
        private const double TauDecay = 150;
        private const double TauOpacity = 200;

        #endregion

        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private double _lastTickSeconds;
        
        private readonly double[] _currentHeights = new double[BarCount];
        private double _currentOpacity = 0.2;
        private VisualizerState _state = VisualizerState.Idle;

        public MusicVisualizer()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += OnTick;
            
            Loaded += (s, e) => UpdateTimerState();
            Unloaded += (s, e) => _timer.Stop();
            IsVisibleChanged += (s, e) => UpdateTimerState();

            // Initial heights
            for (int i = 0; i < BarCount; i++) _currentHeights[i] = MinHeightRatio;
        }

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MusicVisualizer viz)
            {
                viz.UpdateInternalState();
            }
        }

        private void UpdateInternalState()
        {
            var oldState = _state;

            if (string.IsNullOrEmpty(TrackId))
                _state = VisualizerState.Idle;
            else if (IsBuffering)
                _state = VisualizerState.Seeking;
            else if (IsPlaying)
                _state = VisualizerState.Playing;
            else
                _state = VisualizerState.Paused;

            if (oldState != _state)
            {
                UpdateTimerState();
            }
        }

        private void UpdateTimerState()
        {
            if (!IsVisible || _state == VisualizerState.Idle)
            {
                if (_timer.IsEnabled)
                {
                    _timer.Stop();
                    _stopwatch.Stop();
                }
                return;
            }

            double interval = _state switch
            {
                VisualizerState.Playing => 33,  // ~30 FPS
                VisualizerState.Seeking => 33,  // ~30 FPS
                VisualizerState.Paused => 200,  // 5 FPS
                _ => 1000
            };

            _timer.Interval = TimeSpan.FromMilliseconds(interval);

            if (!_timer.IsEnabled)
            {
                _timer.Start();
                if (!_stopwatch.IsRunning) _stopwatch.Start();
                _lastTickSeconds = _stopwatch.Elapsed.TotalSeconds;
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            double totalSec = _stopwatch.Elapsed.TotalSeconds;
            double dt = totalSec - _lastTickSeconds;
            _lastTickSeconds = totalSec;

            if (dt <= 0) return;

            bool isSettled = UpdateAnimation(dt, totalSec);
            InvalidateVisual();

            // If paused and settled, we can slow down or stop the timer
            if (_state == VisualizerState.Paused && isSettled)
            {
                // Optionally stop timer if everything reached baseline
            }
        }

        private bool UpdateAnimation(double dt, double totalSec)
        {
            bool isSettled = true;
            
            // 1. Target Opacity
            double targetOpacity = _state switch
            {
                VisualizerState.Idle => 0.2,
                VisualizerState.Paused => 0.5,
                VisualizerState.Playing => 1.0,
                VisualizerState.Seeking => 1.0,
                _ => 0.2
            };
            
            _currentOpacity += (targetOpacity - _currentOpacity) * (1 - Math.Exp(-dt * 1000 / TauOpacity));

            // 2. Target Heights
            string sid = TrackId ?? "";
            for (int i = 0; i < BarCount; i++)
            {
                double targetH = GetTargetHeightAt(i, totalSec, sid);
                double tau = (targetH > _currentHeights[i]) ? TauPlaying : TauDecay;
                
                double oldH = _currentHeights[i];
                _currentHeights[i] += (targetH - _currentHeights[i]) * (1 - Math.Exp(-dt * 1000 / tau));
                
                if (Math.Abs(_currentHeights[i] - oldH) > 0.001) isSettled = false;
            }

            return isSettled;
        }

        private double GetTargetHeightAt(int index, double t, string sid)
        {
            if (_state == VisualizerState.Idle) return MinHeightRatio;
            if (_state == VisualizerState.Paused) return MinHeightRatio;

            uint hash = GetDeterministicHash(sid + index);
            
            // Base parameters from hash
            double phase = (hash % 1000) / 1000.0 * Math.PI * 2;
            double freq = 1.8 + (hash % 60) / 100.0; // 1.8 to 2.4 Hz
            
            double val = Math.Sin(t * freq * Math.PI * 2 + phase) * 0.35;
            val += Math.Sin(t * 0.9 * Math.PI * 2 + phase * 0.5) * 0.2;
            
            // Seeking adds more frequency variance
            if (_state == VisualizerState.Seeking)
            {
                val += Math.Sin(t * 5.0 + phase) * 0.15;
            }

            // Noise (Sample & Hold)
            double noiseRate = 12.0;
            uint noiseSeed = GetDeterministicHash(sid + index + (int)Math.Floor(t * noiseRate));
            val += ((noiseSeed % 200) / 100.0 - 1.0) * 0.12;

            double result = 0.5 + val;
            return Math.Clamp(result, MinHeightRatio, MaxHeightRatio);
        }

        private uint GetDeterministicHash(string str)
        {
            uint hash = 2166136261;
            foreach (char c in str)
            {
                hash = (hash ^ (uint)c) * 16777619;
            }
            return hash;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;

            if (width < 1 || height < 1 || ActiveBrush == null) return;

            // Align to pixels
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            
            double barWidth = width * BarWidthRatio;
            double spacing = width * BarSpacingRatio;
            double totalContentWidth = (barWidth * BarCount) + (spacing * (BarCount - 1));
            
            double startX = (width - totalContentWidth) / 2;
            double centerY = height / 2;

            dc.PushOpacity(_currentOpacity);

            for (int i = 0; i < BarCount; i++)
            {
                double barHeight = _currentHeights[i] * height;
                double x = startX + i * (barWidth + spacing);
                double y = centerY - barHeight / 2;

                // Pixel snapping
                double snappedX = Math.Round(x * dpi.DpiScaleX) / dpi.DpiScaleX;
                double snappedY = Math.Round(y * dpi.DpiScaleY) / dpi.DpiScaleY;
                double snappedW = Math.Round((x + barWidth) * dpi.DpiScaleX) / dpi.DpiScaleX - snappedX;
                double snappedH = Math.Round((y + barHeight) * dpi.DpiScaleY) / dpi.DpiScaleY - snappedY;

                double radius = snappedW * CornerRadiusRatio;
                
                dc.DrawRoundedRectangle(ActiveBrush, null, 
                    new Rect(snappedX, snappedY, snappedW, snappedH), 
                    radius, radius);
            }

            dc.Pop();
        }
    }
}
