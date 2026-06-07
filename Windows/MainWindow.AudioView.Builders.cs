using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private IVolumeService? _masterVolumeCached;
    private IVolumeService MasterVolume =>
        _masterVolumeCached ??= (IVolumeService)App.Services.GetService(typeof(IVolumeService))!;

    private FontFamily? _audioFontCached;
    // MainSystemFont is an app resource; resolve once and reuse for every row/cell to avoid
    // repeated FindResource walks while building the (potentially long) audio table.
    private FontFamily AudioFont => _audioFontCached ??= (FontFamily)FindResource("MainSystemFont");

    // Symbol font is allocated once instead of per glyph TextBlock (was new'd dozens of times per build).
    private static readonly FontFamily SegoeSymbolFont = new FontFamily("Segoe MDL2 Assets");

    // Column metrics shared by every row + the section headers so the table aligns.
    private const double ColName = 196;
    private const double ColPercent = 50;
    private const double ColDevice = 206;

    private static readonly Brush AudioGreen = Frozen("#3FD15B");
    private static readonly Brush AudioTrack = Frozen("#4D4D4D");
    private static readonly Brush AudioMuted = Frozen("#8C8C8C");
    private static readonly Brush AudioComboBg = Frozen("#33000000");
    private static readonly Brush AudioComboBorder = Frozen("#26FFFFFF");
    private static readonly Brush AudioComboHover = Frozen("#1FFFFFFF");
    private static readonly Brush AudioHeaderText = Frozen("#8A8A8A");

    // Headphones icon (ionicons) used for the Output row, drawn as a vector path.
    private static readonly Geometry OutputIconGeometry = MakeFrozenGeometry(
        "M411.16,97.46C368.43,55.86,311.88,32,256,32S143.57,55.86,100.84,97.46C56.45,140.67,32,197,32,256c0,26.67,8.75,61.09,32.88,125.55S137,473,157.27,477.41c5.81,1.27,12.62,2.59,18.73,2.59a60.06,60.06,0,0,0,30-8l14-8c15.07-8.82,19.47-28.13,10.8-43.35L143.88,268.08a31.73,31.73,0,0,0-43.57-11.76l-13.69,8a56.49,56.49,0,0,0-14,11.59,4,4,0,0,1-7-2A114.68,114.68,0,0,1,64,256c0-50.31,21-98.48,59.16-135.61C160,84.55,208.39,64,256,64s96,20.55,132.84,56.39C427,157.52,448,205.69,448,256a114.68,114.68,0,0,1-1.68,17.91,4,4,0,0,1-7,2,56.49,56.49,0,0,0-14-11.59l-13.69-8a31.73,31.73,0,0,0-43.57,11.76L281.2,420.65c-8.67,15.22-4.27,34.53,10.8,43.35l14,8a60.06,60.06,0,0,0,30,8c6.11,0,12.92-1.32,18.73-2.59C375,473,423,446,447.12,381.55S480,282.67,480,256C480,197,455.55,140.67,411.16,97.46Z");

    private static Geometry MakeFrozenGeometry(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    // Microphone icon used for the Input row, drawn as a vector path.
    private static readonly Geometry InputIconGeometry = MakeFrozenGeometry(
        "M11.665 7.915v1.31a5.257 5.257 0 0 1-1.514 3.694 5.174 5.174 0 0 1-1.641 1.126 5.04 5.04 0 0 1-1.456.384v1.899h2.312a.554.554 0 0 1 0 1.108H3.634a.554.554 0 0 1 0-1.108h2.312v-1.899a5.045 5.045 0 0 1-1.456-.384 5.174 5.174 0 0 1-1.641-1.126 5.257 5.257 0 0 1-1.514-3.695v-1.31a.554.554 0 1 1 1.109 0v1.31a4.131 4.131 0 0 0 1.195 2.917 3.989 3.989 0 0 0 5.722 0 4.133 4.133 0 0 0 1.195-2.917v-1.31a.554.554 0 1 1 1.109 0zM3.77 10.37a2.875 2.875 0 0 1-.233-1.146V4.738A2.905 2.905 0 0 1 3.77 3.58a3 3 0 0 1 1.59-1.59 2.902 2.902 0 0 1 1.158-.233 2.865 2.865 0 0 1 1.152.233 2.977 2.977 0 0 1 1.793 2.748l-.012 4.487a2.958 2.958 0 0 1-.856 2.09 3.025 3.025 0 0 1-.937.634 2.865 2.865 0 0 1-1.152.233 2.905 2.905 0 0 1-1.158-.233A2.957 2.957 0 0 1 3.77 10.37z");

    private bool _audioSystemExpanded = true;
    private bool _audioAppsExpanded = true;
    private int _audioPopulateToken;

    // Live row handles so a refresh can update values IN PLACE (no rebuild → no snap).
    private Action<double>? _outputSetVol;
    private TextBlock? _outputDeviceLabel;
    private Action<double>? _inputSetVol;
    private TextBlock? _inputDeviceLabel;
    private readonly Dictionary<uint, (Action<double> SetVol, Border IconHost, TextBlock NameLabel)> _appRows = new();
    private string _audioStructureKey = "";

    // Last fully-enriched snapshot (with icons). Lets a re-open render instantly while
    // fresh data is gathered in the background.
    private AudioSnapshot? _lastAudioSnapshot;

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private sealed class AudioSnapshot
    {
        public List<AudioDeviceInfo> Output = new();
        public List<AudioDeviceInfo> Input = new();
        public List<AudioSessionInfo> Sessions = new();
        public float Capture = 0.5f;
    }

    /// <summary>
    /// Gathers all audio data (with icons) on a background thread, then on the dispatcher
    /// updates the table IN PLACE when the row set is unchanged (no rebuild → no snap/flicker),
    /// or rebuilds only when the structure actually changed.
    /// </summary>
    private void RefreshAudioData(Action? afterBuild = null)
    {
        int token = ++_audioPopulateToken;

        System.Threading.Tasks.Task.Run(() =>
        {
            var snap = new AudioSnapshot
            {
                Output = SafeCall(() => AudioMixer.GetOutputDevices()) ?? new(),
                Input = SafeCall(() => AudioMixer.GetInputDevices()) ?? new(),
                Sessions = SafeCall(() => AudioMixer.GetSessions(includeIcons: true)) ?? new(),
                Capture = SafeCall(() => AudioMixer.GetCaptureVolume())
            };

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (token != _audioPopulateToken) return;

                if (_appRows.Count > 0 && StructureKey(snap) == _audioStructureKey)
                    PatchInPlace(snap);
                else
                    BuildAudioUI(snap);

                _lastAudioSnapshot = snap;
                afterBuild?.Invoke();
            }), System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private static string StructureKey(AudioSnapshot s)
    {
        var apps = string.Join(",", s.Sessions.Where(x => !x.IsSystemSounds).Select(x => x.ProcessId).OrderBy(x => x));
        string outDef = s.Output.Find(d => d.IsDefault)?.Id ?? "";
        string inDef = s.Input.Find(d => d.IsDefault)?.Id ?? "";
        return $"{apps}|{outDef}|{inDef}|{s.Output.Count}|{s.Input.Count}";
    }

    private void CacheSessionVolume(uint pid, float v)
    {
        if (_lastAudioSnapshot == null) return;
        var c = _lastAudioSnapshot.Sessions.Find(x => x.ProcessId == pid);
        if (c != null) { c.Volume = v; if (v > 0.0001f) c.IsMuted = false; }
    }

    // Lightweight live poll: refresh only volume positions (no icons/device lists) so the
    // One-shot, high-priority volume sync used right when the mixer opens. Unlike the
    // periodic PollAudioVolumes it is NOT gated by _isAnimating and patches at Render
    // priority, so the sliders reflect the current system volume immediately (e.g. after the
    // compact volume bar was scrolled) instead of lagging behind the open animation / the
    // slower icon-enriched refresh.
    private void SyncAudioVolumesImmediate()
    {
        if (!_isAudioView) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            var sessions = SafeCall(() => AudioMixer.GetSessions(includeIcons: false)) ?? new();
            float capture = SafeCall(() => AudioMixer.GetCaptureVolume());

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isAudioView) return;

                if (_outputSetVol != null && MasterVolume.IsAvailable) _outputSetVol(MasterVolume.GetVolume());
                if (_inputSetVol != null) _inputSetVol(capture);

                foreach (var s in sessions)
                {
                    if (s.IsSystemSounds) continue;
                    if (_appRows.TryGetValue(s.ProcessId, out var h))
                        h.SetVol(s.IsMuted ? 0 : s.Volume);

                    if (_lastAudioSnapshot != null)
                    {
                        var c = _lastAudioSnapshot.Sessions.Find(x => x.ProcessId == s.ProcessId);
                        if (c != null) { c.Volume = s.Volume; c.IsMuted = s.IsMuted; }
                    }
                }
                if (_lastAudioSnapshot != null) _lastAudioSnapshot.Capture = capture;
            }), System.Windows.Threading.DispatcherPriority.Render);
        });
    }

    // bars track external changes quickly, without rebuilding or re-enumerating icons.
    private void PollAudioVolumes()
    {
        if (!_isAudioView || _isAnimating) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            var sessions = SafeCall(() => AudioMixer.GetSessions(includeIcons: false)) ?? new();
            float capture = SafeCall(() => AudioMixer.GetCaptureVolume());

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isAudioView || _isAnimating) return;

                var pids = new HashSet<uint>(sessions.Where(x => !x.IsSystemSounds).Select(x => x.ProcessId));
                bool sameSet = _appRows.Count == pids.Count && pids.All(_appRows.ContainsKey);

                if (sameSet)
                {
                    if (_outputSetVol != null && MasterVolume.IsAvailable) _outputSetVol(MasterVolume.GetVolume());
                    if (_inputSetVol != null) _inputSetVol(capture);
                    foreach (var s in sessions)
                    {
                        if (s.IsSystemSounds) continue;
                        if (_appRows.TryGetValue(s.ProcessId, out var h))
                            h.SetVol(s.IsMuted ? 0 : s.Volume);
                    }

                    // Keep the cache current so a re-open shows the right levels (no jump).
                    if (_lastAudioSnapshot != null)
                    {
                        _lastAudioSnapshot.Capture = capture;
                        foreach (var s in sessions)
                        {
                            if (s.IsSystemSounds) continue;
                            var c = _lastAudioSnapshot.Sessions.Find(x => x.ProcessId == s.ProcessId);
                            if (c != null) { c.Volume = s.Volume; c.IsMuted = s.IsMuted; }
                        }
                    }
                }
                else
                {
                    // App started/stopped audio — full refresh so rows are added/removed.
                    RefreshAudioData();
                }
            }), System.Windows.Threading.DispatcherPriority.Render);
        });
    }

    private void PatchInPlace(AudioSnapshot snap)
    {
        if (_outputSetVol != null && MasterVolume.IsAvailable) _outputSetVol(MasterVolume.GetVolume());
        if (_inputSetVol != null) _inputSetVol(snap.Capture);
        if (_outputDeviceLabel != null) _outputDeviceLabel.Text = PickName(snap.Output, "Speakers");
        if (_inputDeviceLabel != null) _inputDeviceLabel.Text = PickName(snap.Input, "Microphone");

        foreach (var session in snap.Sessions)
        {
            if (session.IsSystemSounds) continue;
            if (!_appRows.TryGetValue(session.ProcessId, out var h)) continue;

            h.SetVol(session.IsMuted ? 0 : session.Volume);
            if (session.Icon != null)
            {
                h.IconHost.Child = new Image
                {
                    Source = session.Icon,
                    Width = 18,
                    Height = 18,
                    SnapsToDevicePixels = true
                };
            }
            if (!string.IsNullOrWhiteSpace(session.DisplayName))
                h.NameLabel.Text = session.DisplayName;
        }
    }

    /// <summary>
    /// Warms the snapshot cache in the background (off the UI thread) so the FIRST open of
    /// the audio view renders instantly with content instead of an empty panel.
    /// </summary>
    public void PrewarmAudioSnapshot()
    {
        if (_lastAudioSnapshot != null) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            var snap = new AudioSnapshot
            {
                Output = SafeCall(() => AudioMixer.GetOutputDevices()) ?? new(),
                Input = SafeCall(() => AudioMixer.GetInputDevices()) ?? new(),
                Sessions = SafeCall(() => AudioMixer.GetSessions(includeIcons: true)) ?? new(),
                Capture = SafeCall(() => AudioMixer.GetCaptureVolume())
            };
            Dispatcher.BeginInvoke(new Action(() => _lastAudioSnapshot ??= snap),
                System.Windows.Threading.DispatcherPriority.Background);
        });
    }

    private void BuildAudioUI(AudioSnapshot snap)
    {
        try
        {
            CloseDeviceMenu();
            _appRows.Clear();
            _outputSetVol = null;
            _inputSetVol = null;
            _outputDeviceLabel = null;
            _inputDeviceLabel = null;
            _audioStructureKey = StructureKey(snap);
            AudioRoot.Children.Clear();

            // Pin the content WIDTH so it does NOT re-layout horizontally while the notch
            // border animates (keeps the BitmapCache valid and the open animation smooth). The
            // HEIGHT is set at the end of the build, once the content is measured, so the panel
            // can fit itself to the rows it actually shows.
            if (AudioScrollViewer != null)
            {
                AudioScrollViewer.Width = _audioViewWidth - 38;
                AudioScrollViewer.HorizontalAlignment = HorizontalAlignment.Left;
                AudioScrollViewer.VerticalAlignment = VerticalAlignment.Top;
            }

            string outName = PickName(snap.Output, "Speakers");
            string inName = PickName(snap.Input, "Microphone");

            // ── System section ──
            var systemRows = new StackPanel { ClipToBounds = true, Visibility = _audioSystemExpanded ? Visibility.Visible : Visibility.Collapsed };

            float master = MasterVolume.IsAvailable ? MasterVolume.GetVolume() : 0.5f;
            systemRows.Children.Add(BuildSystemRow("\uE7F5", OutputIconGeometry, "Output", master,
                r => { if (MasterVolume.IsAvailable) MasterVolume.SetVolume((float)r); },
                deviceGlyph: "\uE7F5", deviceText: outName, devices: snap.Output,
                onDevice: id => { if (AudioMixer.SetDefaultOutputDevice(id)) RefreshAudioData(); },
                out _outputSetVol, out _outputDeviceLabel));

            systemRows.Children.Add(BuildSystemRow("\uE720", InputIconGeometry, "Input", snap.Capture,
                r => { AudioMixer.SetCaptureVolume((float)r); if (_lastAudioSnapshot != null) _lastAudioSnapshot.Capture = (float)r; },
                deviceGlyph: "\uE720", deviceText: inName, devices: snap.Input,
                onDevice: id => { if (AudioMixer.SetDefaultInputDevice(id)) RefreshAudioData(); },
                out _inputSetVol, out _inputDeviceLabel));

            AudioRoot.Children.Add(BuildSectionHeader("System", _audioSystemExpanded,
                showVolumeLabel: true, deviceLabel: "Device", out var sysChevron, out var sysClick, topMargin: 0));
            AudioRoot.Children.Add(systemRows);
            WireSectionToggle(sysClick, sysChevron, () => _audioSystemExpanded, v => _audioSystemExpanded = v, systemRows);

            // ── Applications section ──
            var appRows = new StackPanel { ClipToBounds = true, Visibility = _audioAppsExpanded ? Visibility.Visible : Visibility.Collapsed };
            foreach (var session in snap.Sessions)
            {
                if (session.IsSystemSounds) continue;
                uint pid = session.ProcessId;
                appRows.Children.Add(BuildAppRow(session, r =>
                {
                    AudioMixer.SetSessionVolume(pid, (float)r);
                    CacheSessionVolume(pid, (float)r);
                }));
            }

            AudioRoot.Children.Add(BuildSectionHeader("Applications", _audioAppsExpanded,
                showVolumeLabel: false, deviceLabel: "Redirect Audio To", out var appChevron, out var appClick, topMargin: 14));
            AudioRoot.Children.Add(appRows);
            WireSectionToggle(appClick, appChevron, () => _audioAppsExpanded, v => _audioAppsExpanded = v, appRows);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOVIEW", $"Build error: {ex.Message}");
        }

        // Fit the notch to the content just built (clamped to the max; the list scrolls beyond
        // that). The scroll viewport gets the matching height so there's no dead space below the
        // last row when only a few apps are present.
        _audioViewHeight = MeasureAudioFitHeight();
        if (AudioScrollViewer != null)
            AudioScrollViewer.Height = _audioViewHeight - _audioViewChrome;

        if (AudioScrollViewer != null)
            AudioScrollViewer.ScrollToTop();
    }

    /// <summary>
    /// Measures the natural height of the mixer content (for the current expand/collapse state)
    /// and returns the notch height that fits it, clamped to [min, max].
    /// </summary>
    private double MeasureAudioFitHeight()
    {
        if (AudioRoot == null) return _audioViewMaxHeight;
        double contentWidth = _audioViewWidth - 38 - 7; // scroll width minus the right padding
        // Force a fresh measure. WPF returns the cached DesiredSize when IsMeasureValid is true
        // and the constraint is unchanged; toggling a section only dirties the inner rows panel,
        // not AudioRoot, so without this we'd read the PRE-toggle height — making newFit ≈ the
        // current height and the notch resize snap/skip instead of animating.
        AudioRoot.InvalidateMeasure();
        AudioRoot.Measure(new Size(contentWidth, double.PositiveInfinity));
        double desired = AudioRoot.DesiredSize.Height;
        return Math.Clamp(desired + _audioViewChrome, _audioViewMinHeight, _audioViewMaxHeight);
    }

    // Collapse/expand a section with a height + fade animation and a rotating chevron.
    private void WireSectionToggle(FrameworkElement clickTarget, RotateTransform chevron, Func<bool> get, Action<bool> set, StackPanel rows)
    {
        clickTarget.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            bool next = !get();
            set(next);
            AnimateSectionToggle(rows, chevron, next);
        };
    }

    private void AnimateSectionToggle(StackPanel rows, RotateTransform chevron, bool expand)
    {
        int fps = AnimationConfig.TargetFps;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(260));

        // Chevron rotation (pointing down when expanded, right when collapsed).
        var rotAnim = new DoubleAnimation(chevron.Angle, expand ? 0 : -90, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(rotAnim, fps);
        chevron.BeginAnimation(RotateTransform.AngleProperty, rotAnim);

        rows.BeginAnimation(HeightProperty, null);
        rows.BeginAnimation(OpacityProperty, null);

        double newFit;

        if (expand)
        {
            rows.Visibility = Visibility.Visible;

            // Measure the natural height to animate toward.
            double availableWidth = rows.ActualWidth > 0
                ? rows.ActualWidth
                : (AudioScrollViewer?.ActualWidth ?? (_audioViewWidth - 38));
            rows.Height = double.NaN;
            rows.Measure(new Size(availableWidth, double.PositiveInfinity));
            double target = rows.DesiredSize.Height;

            // With this section now full-height, measure the whole panel to fit the notch to it.
            newFit = MeasureAudioFitHeight();

            rows.Height = 0;
            rows.Opacity = 0;

            var hAnim = new DoubleAnimation(0, target, dur) { EasingFunction = ease };
            var oAnim = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(hAnim, fps);
            Timeline.SetDesiredFrameRate(oAnim, fps);
            hAnim.Completed += (_, _) =>
            {
                rows.BeginAnimation(HeightProperty, null);
                rows.Height = double.NaN; // back to auto so it can reflow naturally
                // Commit opacity back to its base value, otherwise the next collapse clears
                // this holding animation and the rows snap to the base opacity (0) — which
                // made the collapse fade appear "missing".
                rows.BeginAnimation(OpacityProperty, null);
                rows.Opacity = 1;
            };
            rows.BeginAnimation(HeightProperty, hAnim);
            rows.BeginAnimation(OpacityProperty, oAnim);
        }
        else
        {
            double from = rows.ActualHeight;

            // Measure the panel with this section collapsed (height 0) to fit the notch to it,
            // then restore the start height for the shrink animation.
            rows.Height = 0;
            newFit = MeasureAudioFitHeight();
            rows.Height = from;

            var hAnim = new DoubleAnimation(from, 0, dur) { EasingFunction = ease };
            var oAnim = new DoubleAnimation(rows.Opacity, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(hAnim, fps);
            Timeline.SetDesiredFrameRate(oAnim, fps);
            hAnim.Completed += (_, _) =>
            {
                rows.Visibility = Visibility.Collapsed;
                rows.BeginAnimation(HeightProperty, null);
                rows.Height = double.NaN;
                rows.BeginAnimation(OpacityProperty, null);
                rows.Opacity = 1;
            };
            rows.BeginAnimation(HeightProperty, hAnim);
            rows.BeginAnimation(OpacityProperty, oAnim);
        }

        // Grow/shrink the notch alongside the section so the panel keeps hugging its content.
        _audioViewHeight = newFit;
        AnimateAudioNotchHeight(newFit, dur, ease);
    }

    private static string PickName(List<AudioDeviceInfo> devices, string fallback)
    {
        var d = devices.Find(x => x.IsDefault);
        return d?.FriendlyName ?? (devices.Count > 0 ? devices[0].FriendlyName : fallback);
    }

    private T? SafeCall<T>(Func<T> fn)
    {
        try { return fn(); } catch { return default; }
    }

    // ─── Shared column grid ───

    private static Grid NewRowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColName) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColPercent) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColDevice) });
        return g;
    }

    // ─── Section header ───

    private FrameworkElement BuildSectionHeader(string title, bool expanded,
        bool showVolumeLabel, string deviceLabel, out RotateTransform chevronTransform,
        out FrameworkElement clickTarget, double topMargin = 0)
    {
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, topMargin, 0, 6);

        // Chevron + title
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        clickTarget = titlePanel;
        var rotate = new RotateTransform(expanded ? 0 : -90);
        chevronTransform = rotate;
        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Fill = AudioHeaderText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 8, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotate
        };
        titlePanel.Children.Add(chevron);
        titlePanel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titlePanel, 0);
        grid.Children.Add(titlePanel);

        if (showVolumeLabel)
        {
            var vol = ColumnLabel("Volume");
            Grid.SetColumn(vol, 1);
            grid.Children.Add(vol);
        }

        var dev = ColumnLabel(deviceLabel);
        Grid.SetColumn(dev, 3);
        grid.Children.Add(dev);

        return grid;
    }

    private TextBlock ColumnLabel(string text) => new TextBlock
    {
        Text = text,
        Foreground = AudioHeaderText,
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        FontFamily = AudioFont,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ─── System row ───

    private FrameworkElement BuildSystemRow(string glyph, Geometry? iconGeometry, string label, double ratio,
        Action<double> onVol, string deviceGlyph, string deviceText,
        List<AudioDeviceInfo>? devices, Action<string>? onDevice,
        out Action<double> setVol, out TextBlock deviceLabel)
    {
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, 4, 0, 4);
        grid.Height = 34;

        // Name cell: icon (vector path or glyph) + label
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (iconGeometry != null)
        {
            namePanel.Children.Add(new Viewbox
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new System.Windows.Shapes.Path { Data = iconGeometry, Fill = Brushes.White, Stretch = Stretch.Uniform }
            });
        }
        else
        {
            namePanel.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = SegoeSymbolFont,
                FontSize = 16,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
        }
        namePanel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        setVol = AddVolumeColumns(grid, ratio, onVol);

        var combo = CreateDeviceCombo(deviceGlyph, iconGeometry, deviceText, devices, onDevice, out deviceLabel);
        Grid.SetColumn(combo, 3);
        grid.Children.Add(combo);

        return grid;
    }

    // ─── Application row ───

    private FrameworkElement BuildAppRow(AudioSessionInfo session, Action<double> onVol)
    {
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, 4, 0, 4);
        grid.Height = 34;

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Icon host (Border) so the async enrichment pass can swap in the real icon.
        var iconHost = new Border
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Child = session.Icon != null
                ? new Image { Source = session.Icon, Width = 18, Height = 18, SnapsToDevicePixels = true }
                : new TextBlock
                {
                    Text = "\uE71D",
                    FontFamily = SegoeSymbolFont,
                    FontSize = 15,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
        };
        namePanel.Children.Add(iconHost);

        var nameLabel = new TextBlock
        {
            Text = session.DisplayName,
            Foreground = Brushes.White,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 130
        };
        namePanel.Children.Add(nameLabel);
        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        var setVol = AddVolumeColumns(grid, session.IsMuted ? 0 : session.Volume, onVol);
        _appRows[session.ProcessId] = (setVol, iconHost, nameLabel);

        var combo = CreateDeviceCombo("\uE898", null, "No Redirect", null, null, out _);
        Grid.SetColumn(combo, 3);
        grid.Children.Add(combo);

        return grid;
    }

    // ─── Volume slider + percent (columns 1 & 2) ───

    private Action<double> AddVolumeColumns(Grid grid, double ratio, Action<double> onVol)
    {
        var percent = new TextBlock
        {
            Foreground = AudioMuted,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(percent, 2);
        grid.Children.Add(percent);

        var volCell = CreateVolumeCell(ratio, onVol, percent, out var setVisual);
        Grid.SetColumn(volCell, 1);
        grid.Children.Add(volCell);
        return setVisual;
    }

    private FrameworkElement CreateVolumeCell(double ratio, Action<double> onChanged, TextBlock percentLabel, out Action<double> setVisual)
    {
        double currentRatio = Math.Clamp(ratio, 0, 1);
        const double thumbSize = 15;

        var cell = new Grid();
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var speaker = new TextBlock
        {
            Text = "\uE767",
            FontFamily = SegoeSymbolFont,
            FontSize = 14,
            Foreground = AudioMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(speaker, 0);
        cell.Children.Add(speaker);

        var area = new Grid
        {
            Height = 30,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 110
        };

        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = AudioTrack,
            VerticalAlignment = VerticalAlignment.Center
        };
        area.Children.Add(track);

        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = AudioGreen,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 0,
            RenderTransformOrigin = new Point(0, 0.5)
        };
        // Fill width tracks the value via a horizontal ScaleTransform rather than the Width
        // layout-property, so dragging only re-renders (no Arrange pass per MouseMove). The
        // layout Width is set to the full track width on resize; ScaleX selects the fraction.
        var fillScale = new ScaleTransform(0, 1);
        fill.RenderTransform = fillScale;
        area.Children.Add(fill);

        var thumb = new Ellipse
        {
            Width = thumbSize,
            Height = thumbSize,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        // Position via TranslateTransform (render-only) instead of Margin (layout) so the
        // thumb glides during drag without triggering layout.
        var thumbTranslate = new TranslateTransform(0, 0);
        thumb.RenderTransform = thumbTranslate;
        area.Children.Add(thumb);

        Grid.SetColumn(area, 1);
        cell.Children.Add(area);

        // Value update: render-transform only, safe to call on every MouseMove.
        void UpdateVisual(double r)
        {
            double w = area.ActualWidth;
            if (w <= 0) return;
            double usable = Math.Max(0, w - thumbSize);
            fill.Width = w; // full track width; ScaleX below picks the visible fraction
            fillScale.ScaleX = Math.Clamp(r, 0, 1);
            thumbTranslate.X = r * usable;
            percentLabel.Text = ((int)Math.Round(r * 100)) + "%";
        }

        area.Loaded += (_, _) => UpdateVisual(currentRatio);
        area.SizeChanged += (_, _) => UpdateVisual(currentRatio);
        percentLabel.Text = ((int)Math.Round(currentRatio * 100)) + "%";

        bool dragging = false;
        DateTime suppressUntil = DateTime.MinValue;
        void SetFromX(double x)
        {
            double w = area.ActualWidth;
            if (w <= 0) return;
            currentRatio = Math.Clamp(x / w, 0, 1);
            UpdateVisual(currentRatio);
            try { onChanged(currentRatio); } catch { }
        }

        // External value updates (refresh/poll): move the slider unless the user is dragging
        // it or just released it (brief grace window stops a stale poll read snapping it back).
        setVisual = r =>
        {
            if (dragging || DateTime.UtcNow < suppressUntil) return;
            currentRatio = Math.Clamp(r, 0, 1);
            UpdateVisual(currentRatio);
        };

        area.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            area.CaptureMouse();
            SetFromX(e.GetPosition(area).X);
            e.Handled = true;
        };
        area.MouseMove += (_, e) => { if (dragging) SetFromX(e.GetPosition(area).X); };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            area.ReleaseMouseCapture();
            // Commit the final value immediately and hold off external overrides briefly so
            // the bar doesn't jump back to a stale polled reading.
            try { onChanged(currentRatio); } catch { }
            suppressUntil = DateTime.UtcNow.AddMilliseconds(600);
            e.Handled = true;
        };

        return cell;
    }

    // ─── Device "combo" pill ───

    private FrameworkElement CreateDeviceCombo(string glyph, Geometry? iconGeometry, string text,
        List<AudioDeviceInfo>? devices, Action<string>? onSelect, out TextBlock labelOut)
    {
        bool interactive = devices != null && devices.Count > 0 && onSelect != null;

        var border = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = AudioComboBg,
            BorderBrush = AudioComboBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = ColDevice,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = interactive ? Cursors.Hand : Cursors.Arrow
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = SegoeSymbolFont,
            FontSize = 13,
            Foreground = AudioMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(icon, 0);
        if (iconGeometry != null)
        {
            var vb = new Viewbox
            {
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Child = new System.Windows.Shapes.Path { Data = iconGeometry, Fill = AudioMuted, Stretch = Stretch.Uniform }
            };
            Grid.SetColumn(vb, 0);
            grid.Children.Add(vb);
        }
        else
        {
            grid.Children.Add(icon);
        }

        var label = new TextBlock
        {
            Text = text,
            Foreground = interactive ? Brushes.White : AudioMuted,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        labelOut = label;

        if (interactive)
        {
            var caret = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L4,4 L8,0 M0,6 L4,2 L8,6"),
                Stroke = AudioMuted,
                StrokeThickness = 1.3,
                Width = 8,
                Height = 8,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(caret, 2);
            grid.Children.Add(caret);
        }

        border.Child = grid;

        if (interactive)
        {
            border.MouseEnter += (_, _) => border.Background = AudioComboHover;
            border.MouseLeave += (_, _) => border.Background = AudioComboBg;
            border.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ShowDeviceMenu(border, devices!, text, onSelect!);
            };
        }

        return border;
    }

    private void CloseDeviceMenu()
    {
        if (AudioOverlay == null) return;
        AudioOverlay.Children.Clear();
        AudioOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowDeviceMenu(Border anchor, List<AudioDeviceInfo> devices, string currentText, Action<string> onSelect)
    {
        if (AudioOverlay == null) return;
        CloseDeviceMenu();

        var list = new StackPanel();
        var container = new Border
        {
            Background = Frozen("#F21C1C1C"),
            BorderBrush = AudioComboBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(4),
            Width = anchor.ActualWidth > 0 ? anchor.ActualWidth : 206,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = list,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.5,
                Color = Colors.Black,
                Direction = 270,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            }
        };
        // Clicks inside the dropdown shouldn't reach the overlay (which closes on click).
        container.MouseLeftButtonDown += (_, e) => e.Handled = true;

        foreach (var device in devices)
        {
            bool selected = device.IsDefault ||
                string.Equals(device.FriendlyName, currentText, StringComparison.OrdinalIgnoreCase);

            var item = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Background = selected ? AudioComboHover : Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = device.FriendlyName,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                FontFamily = AudioFont,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 0);
            g.Children.Add(name);

            if (selected)
            {
                var check = new TextBlock
                {
                    Text = "\uE73E",
                    FontFamily = SegoeSymbolFont,
                    FontSize = 12,
                    Foreground = AudioGreen,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(check, 1);
                g.Children.Add(check);
            }

            item.Child = g;
            item.MouseEnter += (_, _) => { if (!selected) item.Background = AudioComboHover; };
            item.MouseLeave += (_, _) => { if (!selected) item.Background = Brushes.Transparent; };

            string id = device.Id;
            item.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                CloseDeviceMenu();
                onSelect(id);
            };
            list.Children.Add(item);
        }

        // Position the dropdown right below the combo, in AudioContent coordinates.
        try
        {
            var pos = anchor.TransformToVisual(AudioContent).Transform(new Point(0, 0));
            container.Margin = new Thickness(pos.X, pos.Y + anchor.ActualHeight + 4, 0, 0);
        }
        catch { }

        AudioOverlay.Children.Add(container);
        AudioOverlay.Visibility = Visibility.Visible;
        AudioOverlay.MouseLeftButtonDown -= AudioOverlay_OutsideClick;
        AudioOverlay.MouseLeftButtonDown += AudioOverlay_OutsideClick;

        // Slide-down + fade reveal.
        var slide = new TranslateTransform(0, -8);
        container.RenderTransform = slide;
        int fps = AnimationConfig.TargetFps;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(160));
        var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        var drop = new DoubleAnimation(-8, 0, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(drop, fps);
        container.BeginAnimation(OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, drop);
    }

    private void AudioOverlay_OutsideClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Reaches here only when the click was on the overlay backdrop (the dropdown
        // marks its own clicks handled), so close the menu.
        e.Handled = true;
        CloseDeviceMenu();
    }
}
