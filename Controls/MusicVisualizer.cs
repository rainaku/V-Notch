using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Dsp;

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

        private const int BarCount = 5;
        private const double MinHeightRatio = 0.2;
        private const double MaxHeightRatio = 0.9;
        private const double BarWidthRatio = 0.10; // Percent of total width per bar
        private const double BarSpacingRatio = 0.05;
        private const double CornerRadiusRatio = 0.5; // Circle-like ends

        // Smoothing tau (ms)
        private const double TauPlaying = 60;
        private const double TauDecay = 180;
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
                if (_state == VisualizerState.Playing && _capture == null)
                {
                    StartAudioCapture();
                }
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
                VisualizerState.Playing => 16,  // ~60 FPS
                VisualizerState.Seeking => 16,  // ~60 FPS
                VisualizerState.Paused => 100,
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
        }

        private bool UpdateAnimation(double dt, double totalSec)
        {
            bool isSettled = true;
            
            double targetOpacity = _state switch
            {
                VisualizerState.Idle => 0.2,
                VisualizerState.Paused => 0.5,
                VisualizerState.Playing => 1.0,
                VisualizerState.Seeking => 1.0,
                _ => 0.2
            };
            
            _currentOpacity += (targetOpacity - _currentOpacity) * (1 - Math.Exp(-dt * 1000 / TauOpacity));

            string sid = TrackId ?? "";

            // Calculate current volumes
            float[] levels = GetLatestFftLevels();
            bool isAudioActive = false;
            foreach (var lvl in levels) { if (lvl > 0.05f) { isAudioActive = true; break; } }

            for (int i = 0; i < BarCount; i++)
            {
                double targetH;

                if (_state == VisualizerState.Playing && isAudioActive)
                {
                    // Actual audio reaction
                    targetH = MinHeightRatio + levels[i] * (MaxHeightRatio - MinHeightRatio);
                }
                else
                {
                    // Fallback to idle/animation if no audio detected or paused
                    targetH = GetFallbackTargetHeightAt(i, totalSec, sid);
                }

                double tau = (targetH > _currentHeights[i]) ? TauPlaying : TauDecay;
                
                double oldH = _currentHeights[i];
                _currentHeights[i] += (targetH - _currentHeights[i]) * (1 - Math.Exp(-dt * 1000 / tau));
                
                if (Math.Abs(_currentHeights[i] - oldH) > 0.001) isSettled = false;
            }

            return isSettled;
        }

        private double GetFallbackTargetHeightAt(int index, double t, string sid)
        {
            if (_state == VisualizerState.Idle || _state == VisualizerState.Paused) return MinHeightRatio;

            uint hash = GetDeterministicHash(sid + index);
            double phase = (hash % 1000) / 1000.0 * Math.PI * 2;
            double freq = 1.8 + (hash % 60) / 100.0;
            
            double val = Math.Sin(t * freq * Math.PI * 2 + phase) * 0.35;
            val += Math.Sin(t * 0.9 * Math.PI * 2 + phase * 0.5) * 0.2;
            
            if (_state == VisualizerState.Seeking)
                val += Math.Sin(t * 5.0 + phase) * 0.15;

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
                hash = (hash ^ (uint)c) * 16777619;
            return hash;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;

            if (width < 1 || height < 1 || ActiveBrush == null) return;

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            
            double barWidth = width * BarWidthRatio;
            double spacing = width * BarSpacingRatio;
            double totalContentWidth = (barWidth * BarCount) + (spacing * (BarCount - 1));
            
            double startX = (width - totalContentWidth) / 2;
            double centerY = height / 2;

            double snappedW = Math.Max(1.0, Math.Round(barWidth * dpi.DpiScaleX) / dpi.DpiScaleX);

            dc.PushOpacity(_currentOpacity);

            for (int i = 0; i < BarCount; i++)
            {
                double barHeight = _currentHeights[i] * height;
                double x = startX + i * (barWidth + spacing);
                double y = centerY - barHeight / 2;

                double snappedX = Math.Round(x * dpi.DpiScaleX) / dpi.DpiScaleX;
                double snappedY = Math.Round(y * dpi.DpiScaleY) / dpi.DpiScaleY;
                double snappedH = Math.Round(barHeight * dpi.DpiScaleY) / dpi.DpiScaleY;

                double radius = snappedW * CornerRadiusRatio;
                
                dc.DrawRoundedRectangle(ActiveBrush, null, 
                    new Rect(snappedX, snappedY, snappedW, snappedH), 
                    radius, radius);
            }

            dc.Pop();
        }

        #region Audio Loopback Capture
        
        private static WasapiLoopbackCapture? _capture;
        private static readonly object _lockObj = new object();
        private const int FftLength = 1024;
        private const int M = 10; // Log2(1024)
        private static readonly float[] _audioBuffer = new float[FftLength];
        private static int _audioBufferPos = 0;
        private static readonly Complex[] _fftData = new Complex[FftLength];
        private static readonly float[] _bands = new float[BarCount];

        private static void StartAudioCapture()
        {
            lock (_lockObj)
            {
                if (_capture != null) return;

                try
                {
                    _capture = new WasapiLoopbackCapture();
                    _capture.DataAvailable += OnAudioDataAvailable;
                    _capture.RecordingStopped += (s, e) => 
                    {
                        lock (_lockObj) { _capture?.Dispose(); _capture = null; }
                    };
                    _capture.StartRecording();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to initialize audio capture: " + ex.Message);
                    _capture = null;
                }
            }
        }

        private static void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_capture == null) return;

            var waveFormat = _capture.WaveFormat;
            int bytesPerSample = waveFormat.BitsPerSample / 8;
            int samplesRecorded = e.BytesRecorded / bytesPerSample;
            int channels = waveFormat.Channels;

            var waveBuffer = new WaveBuffer(e.Buffer);

            lock (_lockObj)
            {
                for (int i = 0; i < samplesRecorded; i += channels)
                {
                    float sample = waveBuffer.FloatBuffer[i];
                    if (channels > 1 && i + 1 < samplesRecorded)
                    {
                        sample = (sample + waveBuffer.FloatBuffer[i + 1]) / 2f;
                    }

                    _audioBuffer[_audioBufferPos++] = sample;

                    if (_audioBufferPos >= FftLength)
                    {
                        PerformFft();
                        _audioBufferPos = 0;
                    }
                }
            }
        }

        private static void PerformFft()
        {
            for (int i = 0; i < FftLength; i++)
            {
                // Apply Hamming window
                double window = 0.54 - 0.46 * Math.Cos((2 * Math.PI * i) / (FftLength - 1));
                _fftData[i].X = (float)(_audioBuffer[i] * window);
                _fftData[i].Y = 0;
            }

            FastFourierTransform.FFT(true, M, _fftData);

            // Group into 5 frequency bands
            int[] bandRanges = new int[]
            {
                1, 4,     // Bass
                5, 12,    // Low Mids
                13, 35,   // Mids
                36, 100,  // High Mids
                101, 300  // Highs
            };

            for (int i = 0; i < BarCount; i++)
            {
                int start = bandRanges[i * 2];
                int end = bandRanges[i * 2 + 1];
                
                double maxVal = 0;
                for (int j = start; j <= end; j++)
                {
                    double mag = Math.Sqrt(_fftData[j].X * _fftData[j].X + _fftData[j].Y * _fftData[j].Y);
                    if (mag > maxVal) maxVal = mag;
                }

                // Logarithmic dB scale: maxVal typically ~100+ for loud signals
                // log10(100) = 2. So 10*log10 => ~20
                double db = 10 * Math.Log10(maxVal + 1);
                
                // Add simple EQ weight since high frequencies have lower amplitude
                double eqWeight = 1.0 + (i * 0.5); 
                
                float normalized = (float)Math.Clamp((db * eqWeight) / 20.0, 0, 1);
                
                // Give it a bouncy easing
                if (normalized > _bands[i])
                    _bands[i] = _bands[i] * 0.4f + normalized * 0.6f; // Attack fast
                else
                    _bands[i] = _bands[i] * 0.85f + normalized * 0.15f; // Decay slowly
            }
        }

        private static float[] GetLatestFftLevels()
        {
            lock (_lockObj)
            {
                return new float[] { _bands[0], _bands[1], _bands[2], _bands[3], _bands[4] };
            }
        }

        #endregion
    }
}
