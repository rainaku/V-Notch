using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;

namespace VNotch.Presenters;

public sealed record SystemMonitorViewRefs(
    TextBlock CpuValueText,
    Border CpuBar,
    TextBlock RamValueText,
    Border RamBar,
    TextBlock NetDownText,
    TextBlock NetUpText);

public sealed class SystemMonitorPresenter : IDisposable
{
    private readonly SystemMonitorModule _module;
    private readonly IDispatcherService _dispatcher;
    private readonly SystemMonitorViewRefs _refs;
    private bool _disposed;

    public SystemMonitorPresenter(SystemMonitorModule module, IDispatcherService dispatcher, SystemMonitorViewRefs refs)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));

        _module.StatsUpdated += OnStatsUpdated;
    }

    private void OnStatsUpdated(object? sender, SystemMonitorInfo e)
    {
        if (_dispatcher.CheckAccess())
        {
            UpdateSystemMonitorUI(e);
        }
        else
        {
            _dispatcher.Invoke(() => UpdateSystemMonitorUI(e));
        }
    }

    private void UpdateSystemMonitorUI(SystemMonitorInfo stats)
    {
        if (stats == null) return;
        if (_refs.CpuValueText == null) return;

        _refs.CpuValueText.Text = $"{Math.Round(stats.CpuPercent)}%";
        SetUsageBar(_refs.CpuBar, stats.CpuPercent);

        if (stats.RamTotalBytes > 0)
        {
            _refs.RamValueText.Text =
                $"{FormatGb(stats.RamUsedBytes)} / {FormatGb(stats.RamTotalBytes)} GB";
        }
        else
        {
            _refs.RamValueText.Text = "—";
        }
        SetUsageBar(_refs.RamBar, stats.RamPercent);

        _refs.NetDownText.Text = FormatRate(stats.NetDownBytesPerSec);
        _refs.NetUpText.Text = FormatRate(stats.NetUpBytesPerSec);
    }

    private static void SetUsageBar(FrameworkElement? bar, double percent)
    {
        if (bar?.Parent is not FrameworkElement track) return;

        double trackWidth = track.ActualWidth;
        if (double.IsNaN(trackWidth) || trackWidth <= 0) return;

        double clamped = Math.Clamp(percent, 0, 100);
        double target = trackWidth * (clamped / 100.0);

        double current = double.IsNaN(bar.Width) ? bar.ActualWidth : bar.Width;

        if (Math.Abs(target - current) < 0.5)
        {
            bar.BeginAnimation(FrameworkElement.WidthProperty, null);
            bar.Width = target;
            return;
        }

        var anim = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(550),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        bar.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    private static string FormatGb(ulong bytes) =>
        (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0");

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 0) bytesPerSec = 0;

        const double kb = 1024.0;
        const double mb = kb * 1024.0;

        if (bytesPerSec >= mb)
            return $"{bytesPerSec / mb:0.0} MB/s";
        if (bytesPerSec >= kb)
            return $"{bytesPerSec / kb:0.0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _module.StatsUpdated -= OnStatsUpdated;
    }
}
