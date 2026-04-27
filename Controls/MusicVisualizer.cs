using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Dmo;
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
        private const double MinHeightRatio = 0.12;
        private const double MaxHeightRatio = 0.98;
        private const double BarWidthRatio = 0.10; 
        private const double BarSpacingRatio = 0.05;
        private const double CornerRadiusRatio = 0.5; 

        
        private const double AlphaAttack = 0.94;  // Tăng từ 0.88 -> chuyển động lên chậm hơn
        private const double AlphaRelease = 0.98;  // Tăng từ 0.96 -> chuyển động xuống chậm hơn
        private const double AlphaPauseRelease = 0.99;  // Tăng từ 0.98
        private const double TauOpacity = 200;
        private const double CaptureRetryIntervalMs = 2500;
        private const double NoAudioPulseAmplitude = 0.18;
        private const double NoAudioPulseBase = 0.10;
        private const double LegacyRhythmMinMix = 0.15;  // Giảm từ 0.20 -> ít rhythm hơn, nhiều audio hơn
        private const double LegacyRhythmMaxMix = 0.35;  // Giảm từ 0.42
        private const double AudioPresenceThreshold = 0.010;
        private const double DownwardDropBoost = 0.03;  // Giảm từ 0.05 -> rơi chậm hơn
        private const double MinReleaseAlpha = 0.92;  // Tăng từ 0.86 -> chậm hơn
        private const double MotionContrast = 1.30;
        private const double RightBiasStrength = 1.00;
        private const double RightBiasDeadzone = 0.05;

        #endregion

        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private double _lastTickSeconds;
        private DateTime _lastCaptureRetryUtc = DateTime.MinValue;
        
        private readonly double[] _currentHeights = new double[BarCount];
        private readonly double[] _sortedHeights = new double[BarCount];
        private readonly double[] _drawHeights = new double[BarCount];
        private double _currentOpacity = 0.2;
        private VisualizerState _state = VisualizerState.Idle;

        public MusicVisualizer()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += OnTick;
            
            Loaded += (s, e) => UpdateTimerState();
            Unloaded += (s, e) =>
            {
                _timer.Stop();
                StopAudioCapture();
            };
            IsVisibleChanged += (s, e) => UpdateTimerState();

            
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
                if (_state == VisualizerState.Idle)
                {
                    StopAudioCapture();
                }
            }

            if (_state != VisualizerState.Idle)
            {
                EnsureAudioCaptureStarted(force: oldState != _state);
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
                VisualizerState.Playing => 8,  
                VisualizerState.Seeking => 8,  
                VisualizerState.Paused => 8,   
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
            EnsureAudioCaptureStarted();

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

            
            float[] levels = GetLatestDisplayLevels(out bool hasFreshAudio, out double beatAccent);
            double audioEnergy = 0;
            for (int i = 0; i < BarCount; i++) audioEnergy += Math.Clamp(levels[i], 0f, 1f);
            audioEnergy = Math.Clamp(Math.Pow(audioEnergy / BarCount, 0.85), 0.0, 1.0);

            bool hasAudibleEnergy = hasFreshAudio && audioEnergy > AudioPresenceThreshold;
            bool canDriveFromAudio = hasAudibleEnergy &&
                                     (_state == VisualizerState.Playing || _state == VisualizerState.Paused);

            for (int i = 0; i < BarCount; i++)
            {
                double targetH;

                if (canDriveFromAudio)
                {
                    double band = Math.Clamp(levels[i], 0.0, 1.0);
                    double audioShaped = Math.Pow(band, 0.90);
                    double rhythm = GetAudioReactiveRhythmAt(i, totalSec, sid, audioEnergy);
                    double rhythmMixBase = LegacyRhythmMinMix + ((1.0 - audioEnergy) * (LegacyRhythmMaxMix - LegacyRhythmMinMix));
                    double rhythmMix = rhythmMixBase * (1.0 - (beatAccent * 0.45));
                    double normalized = (audioShaped * (1.0 - rhythmMix)) + (rhythm * rhythmMix);
                    normalized = Math.Clamp(normalized + (beatAccent * GetBeatLiftWeight(i) * 0.62), 0.0, 1.0);
                    normalized = ApplyMotionContrast(normalized);
                    targetH = MapNormalizedToHeight(normalized);
                }
                else if (_state == VisualizerState.Playing)
                {
                    double normalized = ApplyMotionContrast(GetNoAudioPulseAt(i, totalSec, sid));
                    targetH = MapNormalizedToHeight(normalized);
                }
                else if (_state == VisualizerState.Seeking)
                {
                    double normalized = ApplyMotionContrast(GetNoAudioPulseAt(i, totalSec, sid));
                    targetH = MapNormalizedToHeight(normalized);
                }
                else if (_state == VisualizerState.Paused)
                {
                    targetH = MinHeightRatio;
                }
                else
                {
                    targetH = MinHeightRatio;
                }

                double dynamicRelease = Math.Max(0.64, AlphaRelease - (beatAccent * 0.08));
                double alpha;
                if (targetH > _currentHeights[i])
                {
                    alpha = AlphaAttack;
                }
                else
                {
                    if (_state == VisualizerState.Paused)
                    {
                        alpha = AlphaPauseRelease;
                    }
                    else
                    {
                        
                        double fallRatio = Math.Clamp(
                            (_currentHeights[i] - targetH) / (MaxHeightRatio - MinHeightRatio),
                            0.0, 1.0);
                        alpha = Math.Max(MinReleaseAlpha, dynamicRelease - (fallRatio * DownwardDropBoost));
                    }
                }
                
                double oldH = _currentHeights[i];
                _currentHeights[i] = (_currentHeights[i] * alpha) + (targetH * (1 - alpha));
                
                if (Math.Abs(_currentHeights[i] - oldH) > 0.001) isSettled = false;
            }

            return isSettled;
        }

        private double MapNormalizedToHeight(double normalized)
        {
            double clamped = Math.Clamp(normalized, 0.0, 1.0);
            return MinHeightRatio + clamped * (MaxHeightRatio - MinHeightRatio);
        }

        private static double ApplyMotionContrast(double normalized)
        {
            double clamped = Math.Clamp(normalized, 0.0, 1.0);
            return Math.Clamp(0.5 + ((clamped - 0.5) * MotionContrast), 0.0, 1.0);
        }

        private double GetNoAudioPulseAt(int index, double t, string sid)
        {
            uint hash = GetDeterministicHash(sid + index);
            double phase = (hash % 1000) / 1000.0 * Math.PI * 2;
            double freq = 0.34 + (hash % 24) / 100.0;
            double wavePrimary = 0.5 + 0.5 * Math.Sin((t * freq * Math.PI * 2) + phase);
            double waveSecondary = 0.5 + 0.5 * Math.Sin((t * (freq * 1.45) * Math.PI * 2) + (phase * 0.37));
            double wave = (wavePrimary * 0.76) + (waveSecondary * 0.24);
            return NoAudioPulseBase + (wave * NoAudioPulseAmplitude);
        }

        private static double GetBeatLiftWeight(int barIndex)
        {
            return barIndex switch
            {
                0 => 0.16, 
                1 => 0.12,
                2 => 0.08,
                3 => 0.10, 
                4 => 0.13,
                _ => 0.08
            };
        }

        private double GetAudioReactiveRhythmAt(int index, double t, string sid, double energy)
        {
            uint hash = GetDeterministicHash(sid + index);
            double phase = (hash % 1000) / 1000.0 * Math.PI * 2;
            double baseFreq = 0.52 + (hash % 28) / 100.0;
            double speed = 0.52 + (energy * 0.32);

            double value = Math.Sin((t * baseFreq * speed * Math.PI * 2) + phase) * 0.40;
            value += Math.Sin((t * (0.30 + (energy * 0.22)) * Math.PI * 2) + (phase * 0.5)) * 0.20;
            value += Math.Sin((t * (baseFreq * 0.42) * Math.PI * 2) + (phase * 1.7)) * 0.08;

            double noiseRate = 1.2 + (energy * 1.4);
            uint noiseSeed = GetDeterministicHash(sid + index + (int)Math.Floor(t * noiseRate));
            value += (((noiseSeed % 200) / 100.0) - 1.0) * (0.008 + (energy * 0.012));

            return Math.Clamp(0.5 + value, 0.0, 1.0);
        }

        private uint GetDeterministicHash(string str)
        {
            uint hash = 2166136261;
            foreach (char c in str)
                hash = (hash ^ (uint)c) * 16777619;
            return hash;
        }

        private void EnsureAudioCaptureStarted(bool force = false)
        {
            if (_state == VisualizerState.Idle) return;
            if (_capture != null) return;

            var now = DateTime.UtcNow;
            if (!force && (now - _lastCaptureRetryUtc).TotalMilliseconds < CaptureRetryIntervalMs) return;

            _lastCaptureRetryUtc = now;
            StartAudioCapture();
        }

        private void PrepareDrawHeights()
        {
            if (RightBiasStrength <= 0)
            {
                Array.Copy(_currentHeights, _drawHeights, BarCount);
                return;
            }

            Array.Copy(_currentHeights, _sortedHeights, BarCount);
            for (int i = 0; i < BarCount - 1; i++)
            {
                for (int j = i + 1; j < BarCount; j++)
                {
                    if (_sortedHeights[j] > _sortedHeights[i])
                    {
                        double temp = _sortedHeights[i];
                        _sortedHeights[i] = _sortedHeights[j];
                        _sortedHeights[j] = temp;
                    }
                }
            }

            double max = _sortedHeights[0];
            double min = _sortedHeights[BarCount - 1];
            double spread = Math.Clamp(max - min, 0.0, 1.0);
            double bias = RightBiasStrength;
            if (spread < RightBiasDeadzone)
            {
                bias *= (spread / RightBiasDeadzone);
            }

            for (int i = 0; i < BarCount; i++)
            {
                _drawHeights[i] = (_currentHeights[i] * (1.0 - bias)) + (_sortedHeights[i] * bias);
            }
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

            PrepareDrawHeights();

            for (int i = 0; i < BarCount; i++)
            {
                double barHeight = _drawHeights[i] * height;
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
        private const int FftLength = 512;
        private const int FftM = 9; 
        private const double MinDb = -72.0;
        private const double MaxDb = 0.0;
        private const double CompressionPower = 0.6;
        private const double SpectralPreGain = 12.0;
        private const double RmsWindowSeconds = 0.020;
        private const int FreshAudioTimeoutMs = 800;
        private const double AgcFloor = 0.14;
        private const double AgcRelease = 0.985;
        private const double KickTransientThreshold = 0.05;
        private const double SnareTransientThreshold = 0.04;
        private const double KickTransientGain = 4.4;
        private const double SnareTransientGain = 5.8;
        private const double KickAccentDecay = 0.92;
        private const double SnareAccentDecay = 0.91;
        private const double BeatTransientThreshold = 0.040;
        private const double BeatTransientGain = 5.0;
        private const double BeatAccentDecay = 0.92;
        private const double BeatRmsDeltaThreshold = 0.016;
        private const double BassDominanceStart = 1.10;
        private const double BassDominanceSpan = 1.00;
        private const double MaxLowAttenuation = 0.34;
        private const double MaxKickAttenuation = 0.42;
        private const double SpectralContrastStart = 0.06;
        private const double SpectralContrastSpan = 0.40;
        private const double SpectralContrastStrength = 1.25;
        private const double SpectralDominantBoost = 0.24;
        private const double SpectralSubtractiveCut = 0.20;
        private const double DynamicRangeExpansionPower = 1.12;
        private const double DynamicRangeExpansionBlend = 0.22;
        private const double BarContrastStart = 0.10;
        private const double BarContrastSpan = 0.45;
        private const double BarContrastStrength = 0.65;
        private const double BarContrastBoost = 0.16;
        private const double BarContrastCut = 0.12;

        private static readonly float[] _fftInputBuffer = new float[FftLength];
        private static int _fftInputPos = 0;
        private static readonly Complex[] _fftData = new Complex[FftLength];
        private static readonly float[] _displayTargets = new float[BarCount];
        private static double _rmsSumSquares;
        private static int _rmsSampleCount;
        private static int _rmsWindowSamples = 882; 
        private static float _latestRmsNormalized;
        private static int _sampleRate = 44100;
        private static long _lastAudioFrameUtcTicks;
        private static double _agcPeak = 0.35;
        private static double _prevKickEnergy;
        private static double _prevSnareEnergy;
        private static double _kickAccent;
        private static double _snareAccent;
        private static double _prevRmsForBeat;
        private static double _beatAccent;
        private static float _latestBeatAccent;

        private static void StartAudioCapture()
        {
            lock (_lockObj)
            {
                if (_capture != null) return;

                try
                {
                    _capture = new WasapiLoopbackCapture();
                    _sampleRate = _capture.WaveFormat.SampleRate;
                    _rmsWindowSamples = Math.Max(64, (int)(_sampleRate * RmsWindowSeconds));
                    _capture.DataAvailable += OnAudioDataAvailable;
                    _capture.RecordingStopped += OnCaptureStopped;
                    _capture.StartRecording();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to initialize audio capture: " + ex.Message);
                    _capture = null;
                }
            }
        }

        private static void StopAudioCapture()
        {
            WasapiLoopbackCapture? captureToDispose = null;
            lock (_lockObj)
            {
                if (_capture == null) return;
                captureToDispose = _capture;
                _capture = null;
                ResetAudioState();
            }

            try
            {
                captureToDispose.DataAvailable -= OnAudioDataAvailable;
                captureToDispose.RecordingStopped -= OnCaptureStopped;
                captureToDispose.StopRecording();
            }
            catch
            {
                
            }
            finally
            {
                captureToDispose.Dispose();
            }
        }

        private static void OnCaptureStopped(object? sender, StoppedEventArgs e)
        {
            lock (_lockObj)
            {
                if (sender != null && ReferenceEquals(_capture, sender))
                {
                    _capture.DataAvailable -= OnAudioDataAvailable;
                    _capture.RecordingStopped -= OnCaptureStopped;
                    _capture.Dispose();
                    _capture = null;
                    ResetAudioState();
                }
            }
        }

        private static void ResetAudioState()
        {
            Array.Clear(_fftInputBuffer, 0, _fftInputBuffer.Length);
            Array.Clear(_displayTargets, 0, _displayTargets.Length);
            _fftInputPos = 0;
            _rmsSumSquares = 0;
            _rmsSampleCount = 0;
            _latestRmsNormalized = 0;
            _lastAudioFrameUtcTicks = 0;
            _agcPeak = 0.35;
            _prevKickEnergy = 0;
            _prevSnareEnergy = 0;
            _kickAccent = 0;
            _snareAccent = 0;
            _prevRmsForBeat = 0;
            _beatAccent = 0;
            _latestBeatAccent = 0;
        }

        private static void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_capture == null) return;

            var waveFormat = _capture.WaveFormat;
            int bytesPerSample = waveFormat.BitsPerSample / 8;
            if (bytesPerSample <= 0)
            {
                bytesPerSample = waveFormat.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible
                    ? 4
                    : 2;
            }
            int channels = Math.Max(1, waveFormat.Channels);
            int bytesPerFrame = bytesPerSample * channels;
            if (bytesPerFrame <= 0) return;

            int framesRecorded = e.BytesRecorded / bytesPerFrame;
            if (framesRecorded <= 0) return;

            lock (_lockObj)
            {
                for (int frame = 0; frame < framesRecorded; frame++)
                {
                    int frameOffset = frame * bytesPerFrame;
                    double mixed = 0;

                    for (int ch = 0; ch < channels; ch++)
                    {
                        int sampleOffset = frameOffset + (ch * bytesPerSample);
                        mixed += ReadSampleAsFloat(e.Buffer, sampleOffset, waveFormat);
                    }

                    PushSample((float)(mixed / channels));
                }

                _lastAudioFrameUtcTicks = DateTime.UtcNow.Ticks;
            }
        }

        private static void PushSample(float sample)
        {
            _fftInputBuffer[_fftInputPos++] = sample;
            _rmsSumSquares += sample * sample;
            _rmsSampleCount++;

            if (_rmsSampleCount >= _rmsWindowSamples)
            {
                double rms = Math.Sqrt(_rmsSumSquares / Math.Max(1, _rmsSampleCount));
                _latestRmsNormalized = (float)NormalizeAmplitude(rms);
                _rmsSumSquares = 0;
                _rmsSampleCount = 0;
            }

            if (_fftInputPos >= FftLength)
            {
                ComputeDisplayTargets();
                _fftInputPos = 0;
            }
        }

        private static void ComputeDisplayTargets()
        {
            for (int i = 0; i < FftLength; i++)
            {
                
                double window = 0.54 - 0.46 * Math.Cos((2 * Math.PI * i) / (FftLength - 1));
                _fftData[i].X = (float)(_fftInputBuffer[i] * window);
                _fftData[i].Y = 0;
            }

            FastFourierTransform.FFT(true, FftM, _fftData);

            // Phân tách âm sắc rõ ràng hơn - mỗi thanh đại diện cho 1 dải tần riêng biệt
            double subBass = NormalizeAmplitude(ComputeBandEnergy(20, 60) * (SpectralPreGain * 1.5));    // Sub-bass
            double bass = NormalizeAmplitude(ComputeBandEnergy(60, 250) * (SpectralPreGain * 1.3));      // Bass
            double lowMid = NormalizeAmplitude(ComputeBandEnergy(250, 500) * SpectralPreGain);           // Low-mid
            double mid = NormalizeAmplitude(ComputeBandEnergy(500, 2000) * SpectralPreGain);             // Mid
            double highMid = NormalizeAmplitude(ComputeBandEnergy(2000, 4000) * SpectralPreGain);        // High-mid
            double high = NormalizeAmplitude(ComputeBandEnergy(4000, 8000) * SpectralPreGain);           // High
            double kick = NormalizeAmplitude(ComputeBandEnergy(45, 130) * (SpectralPreGain * 1.35));
            double snare = NormalizeAmplitude(ComputeBandEnergy(1400, 5000) * (SpectralPreGain * 1.25));
            double rms = _latestRmsNormalized;

            double peak = Math.Max(
                Math.Max(Math.Max(Math.Max(subBass, bass), Math.Max(lowMid, mid)), Math.Max(highMid, high)),
                Math.Max(Math.Max(kick, snare), rms));
            _agcPeak = Math.Max(peak, _agcPeak * AgcRelease);
            double agcScale = 1.0 / Math.Max(AgcFloor, _agcPeak);

            subBass = Math.Clamp(subBass * agcScale, 0.0, 1.0);
            bass = Math.Clamp(bass * agcScale, 0.0, 1.0);
            lowMid = Math.Clamp(lowMid * agcScale, 0.0, 1.0);
            mid = Math.Clamp(mid * agcScale, 0.0, 1.0);
            highMid = Math.Clamp(highMid * agcScale, 0.0, 1.0);
            high = Math.Clamp(high * agcScale, 0.0, 1.0);
            kick = Math.Clamp(kick * agcScale, 0.0, 1.0);
            snare = Math.Clamp(snare * agcScale, 0.0, 1.0);
            rms = Math.Clamp(rms * agcScale, 0.0, 1.0);

            
            double nonBass = (mid * 0.7) + (high * 0.6) + 1e-5;
            double bassDominance = (subBass + bass) / nonBass;
            double bassExcess = Math.Clamp((bassDominance - BassDominanceStart) / BassDominanceSpan, 0.0, 1.0);
            double bassScale = 1.0 - (bassExcess * MaxLowAttenuation);
            double kickScale = 1.0 - (bassExcess * MaxKickAttenuation);
            subBass *= bassScale;
            bass *= bassScale;
            kick *= kickScale;

            
            double kickDelta = Math.Max(0.0, kick - _prevKickEnergy);
            double snareDelta = Math.Max(0.0, snare - _prevSnareEnergy);
            double rmsDelta = Math.Max(0.0, rms - _prevRmsForBeat);
            _prevKickEnergy = kick;
            _prevSnareEnergy = snare;
            _prevRmsForBeat = rms;

            double kickHit = Math.Clamp((kickDelta - KickTransientThreshold) * KickTransientGain, 0.0, 1.0);
            double snareHit = Math.Clamp((snareDelta - SnareTransientThreshold) * SnareTransientGain, 0.0, 1.0);
            double rmsHit = Math.Clamp((rmsDelta - BeatRmsDeltaThreshold) * 18.0, 0.0, 1.0);
            _kickAccent = Math.Max(kickHit, _kickAccent * KickAccentDecay);
            _snareAccent = Math.Max(snareHit, _snareAccent * SnareAccentDecay);
            double beatDriver = (_kickAccent * 0.55) + (_snareAccent * 0.30) + (rmsHit * 0.35);
            double beatHit = Math.Clamp((beatDriver - BeatTransientThreshold) * BeatTransientGain, 0.0, 1.0);
            _beatAccent = Math.Max(beatHit, _beatAccent * BeatAccentDecay);
            _latestBeatAccent = (float)_beatAccent;

            ApplySpectralContrast(ref subBass, ref bass, ref lowMid, ref mid, ref highMid, ref high, ref kick, ref snare, ref rms);

            // Mỗi thanh đại diện cho 1 dải tần riêng biệt
            // Bar 0: Sub-bass + Kick (20-130Hz) - Âm trầm sâu nhất
            _displayTargets[0] = (float)Math.Clamp((subBass * 0.70) + (kick * 0.30) + (_kickAccent * 0.20), 0.0, 1.0);
            
            // Bar 1: Bass (60-250Hz) - Âm trầm
            _displayTargets[1] = (float)Math.Clamp((bass * 0.80) + (subBass * 0.20) + (_kickAccent * 0.10), 0.0, 1.0);
            
            // Bar 2: Low-Mid (250-500Hz) + Mid (500-2000Hz) - Âm trung
            _displayTargets[2] = (float)Math.Clamp((lowMid * 0.50) + (mid * 0.40) + (rms * 0.10), 0.0, 1.0);
            
            // Bar 3: High-Mid (2000-4000Hz) - Âm cao trung
            _displayTargets[3] = (float)Math.Clamp((highMid * 0.70) + (snare * 0.20) + (_snareAccent * 0.15), 0.0, 1.0);
            
            // Bar 4: High (4000-8000Hz) + Snare - Âm cao
            _displayTargets[4] = (float)Math.Clamp((high * 0.70) + (snare * 0.20) + (_snareAccent * 0.20), 0.0, 1.0);

            ApplyBarContrast(_displayTargets);
        }

        private static double ComputeBandEnergy(int fromHz, int toHz)
        {
            int maxBin = (FftLength / 2) - 1;
            if (_sampleRate <= 0 || maxBin <= 1) return 0;

            int start = FrequencyToBin(fromHz);
            int end = FrequencyToBin(toHz);
            if (end < start) (start, end) = (end, start);

            start = Math.Clamp(start, 1, maxBin);
            end = Math.Clamp(end, start, maxBin);

            double sumSquares = 0;
            int count = 0;
            for (int i = start; i <= end; i++)
            {
                double mag = Math.Sqrt((_fftData[i].X * _fftData[i].X) + (_fftData[i].Y * _fftData[i].Y));
                double normalizedMag = mag / (FftLength * 0.5);
                sumSquares += normalizedMag * normalizedMag;
                count++;
            }

            return count > 0 ? Math.Sqrt(sumSquares / count) : 0;
        }

        private static int FrequencyToBin(int frequencyHz)
        {
            if (_sampleRate <= 0) return 1;
            return (int)Math.Round((frequencyHz / (double)_sampleRate) * FftLength);
        }

        private static double NormalizeAmplitude(double amplitude)
        {
            double db = 20 * Math.Log10(Math.Max(amplitude, 1e-9));
            double normalized = (db - MinDb) / (MaxDb - MinDb);
            normalized = Math.Clamp(normalized, 0.0, 1.0);
            return Math.Pow(normalized, CompressionPower);
        }

        private static void ApplySpectralContrast(ref double subBass, ref double bass, ref double lowMid, ref double mid, ref double highMid, ref double high, ref double kick, ref double snare, ref double rms)
        {
            double max = Math.Max(Math.Max(Math.Max(subBass, bass), Math.Max(lowMid, mid)), Math.Max(highMid, high));
            double min = Math.Min(Math.Min(Math.Min(subBass, bass), Math.Min(lowMid, mid)), Math.Min(highMid, high));
            double spread = Math.Clamp(max - min, 0.0, 1.0);
            double contrast = Math.Clamp((spread - SpectralContrastStart) / SpectralContrastSpan, 0.0, 1.0);

            if (contrast <= 0.0001)
            {
                subBass = ExpandDynamicRange(subBass);
                bass = ExpandDynamicRange(bass);
                lowMid = ExpandDynamicRange(lowMid);
                mid = ExpandDynamicRange(mid);
                highMid = ExpandDynamicRange(highMid);
                high = ExpandDynamicRange(high);
                kick = ExpandDynamicRange(kick);
                snare = ExpandDynamicRange(snare);
                rms = ExpandDynamicRange(rms);
                return;
            }

            double avg = (subBass + bass + lowMid + mid + highMid + high) / 6.0;

            subBass = EnhanceBand(subBass, avg, contrast);
            bass = EnhanceBand(bass, avg, contrast);
            lowMid = EnhanceBand(lowMid, avg, contrast);
            mid = EnhanceBand(mid, avg, contrast);
            highMid = EnhanceBand(highMid, avg, contrast);
            high = EnhanceBand(high, avg, contrast);

            double bassDelta = (subBass + bass) / 2.0 - avg;
            double highDelta = (highMid + high) / 2.0 - avg;

            kick = Math.Clamp(kick * (1.0 + (bassDelta * 0.70 * contrast)), 0.0, 1.0);
            snare = Math.Clamp(snare * (1.0 + (highDelta * 0.70 * contrast)), 0.0, 1.0);

            subBass = ExpandDynamicRange(subBass);
            bass = ExpandDynamicRange(bass);
            lowMid = ExpandDynamicRange(lowMid);
            mid = ExpandDynamicRange(mid);
            highMid = ExpandDynamicRange(highMid);
            high = ExpandDynamicRange(high);
            kick = ExpandDynamicRange(kick);
            snare = ExpandDynamicRange(snare);
            rms = ExpandDynamicRange(rms);
        }

        private static double EnhanceBand(double band, double avg, double contrast)
        {
            double delta = band - avg;
            double enhanced = band + (delta * (SpectralContrastStrength * contrast));
            if (delta >= 0)
            {
                enhanced *= 1.0 + (SpectralDominantBoost * contrast);
            }
            else
            {
                enhanced *= 1.0 - (SpectralSubtractiveCut * contrast);
            }
            return Math.Clamp(enhanced, 0.0, 1.0);
        }

        private static double ExpandDynamicRange(double value)
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (clamped <= 0) return 0;
            double expanded = Math.Pow(clamped, DynamicRangeExpansionPower);
            return (expanded * (1.0 - DynamicRangeExpansionBlend)) + (clamped * DynamicRangeExpansionBlend);
        }

        private static void ApplyBarContrast(float[] targets)
        {
            double max = 0.0;
            double min = 1.0;
            double sum = 0.0;

            for (int i = 0; i < BarCount; i++)
            {
                double v = Math.Clamp(targets[i], 0.0, 1.0);
                if (v > max) max = v;
                if (v < min) min = v;
                sum += v;
            }

            double spread = Math.Clamp(max - min, 0.0, 1.0);
            double contrast = Math.Clamp((spread - BarContrastStart) / BarContrastSpan, 0.0, 1.0);
            if (contrast <= 0.0001) return;

            double avg = sum / BarCount;

            for (int i = 0; i < BarCount; i++)
            {
                double v = Math.Clamp(targets[i], 0.0, 1.0);
                double delta = v - avg;
                double enhanced = v + (delta * (BarContrastStrength * contrast));
                if (delta >= 0)
                {
                    enhanced *= 1.0 + (BarContrastBoost * contrast);
                }
                else
                {
                    enhanced *= 1.0 - (BarContrastCut * contrast);
                }
                targets[i] = (float)Math.Clamp(enhanced, 0.0, 1.0);
            }
        }

        private static float ReadSampleAsFloat(byte[] buffer, int offset, WaveFormat waveFormat)
        {
            bool isFloatLike = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

            if (waveFormat.Encoding == WaveFormatEncoding.Extensible &&
                waveFormat is WaveFormatExtensible extensible)
            {
                isFloatLike = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
            }

            if (isFloatLike)
            {
                if (waveFormat.BitsPerSample == 64)
                {
                    return (float)BitConverter.ToDouble(buffer, offset);
                }
                return BitConverter.ToSingle(buffer, offset);
            }

            return waveFormat.BitsPerSample switch
            {
                8 => (buffer[offset] - 128) / 128f,
                16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                24 => Read24BitSample(buffer, offset) / 8388608f,
                32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => 0f
            };
        }

        private static int Read24BitSample(byte[] buffer, int offset)
        {
            int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            return sample;
        }

        private static float[] GetLatestDisplayLevels(out bool hasFreshAudio, out double beatAccent)
        {
            lock (_lockObj)
            {
                long ticks = _lastAudioFrameUtcTicks;
                hasFreshAudio = ticks != 0 &&
                    (DateTime.UtcNow.Ticks - ticks) <= TimeSpan.FromMilliseconds(FreshAudioTimeoutMs).Ticks;
                beatAccent = _latestBeatAccent;

                return new[]
                {
                    _displayTargets[0],
                    _displayTargets[1],
                    _displayTargets[2],
                    _displayTargets[3],
                    _displayTargets[4]
                };
            }
        }

        #endregion
    }
}
