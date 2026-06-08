using System;
using System.Windows;
using System.Windows.Media.Animation;
using VNotch.Models;

namespace VNotch;

public partial class MainWindow
{
    #region System Monitor Widget

    private void SystemMonitorModule_StatsUpdated(object? sender, SystemMonitorInfo e)
    {
        UpdateSystemMonitorUI(e);
    }

    /// <summary>
    /// Refreshes the system-monitor widget's labels and usage bars. Visibility of the
    /// widget itself is owned by <see cref="ApplyExpandedWidgetMode"/> (it is one of the
    /// selectable expanded-notch widgets), so this only updates the content. Cheap enough
    /// to run on every 1s tick even while collapsed.
    /// </summary>
    private void UpdateSystemMonitorUI(SystemMonitorInfo stats)
    {
        if (stats == null) return;
        if (SysMonCpuValueText == null) return; // template not loaded yet

        SysMonCpuValueText.Text = $"{Math.Round(stats.CpuPercent)}%";
        SetUsageBar(SysMonCpuBar, stats.CpuPercent);

        if (stats.RamTotalBytes > 0)
        {
            SysMonRamValueText.Text =
                $"{FormatGb(stats.RamUsedBytes)} / {FormatGb(stats.RamTotalBytes)} GB";
        }
        else
        {
            SysMonRamValueText.Text = "—";
        }
        SetUsageBar(SysMonRamBar, stats.RamPercent);

        SysMonNetDownText.Text = FormatRate(stats.NetDownBytesPerSec);
        SysMonNetUpText.Text = FormatRate(stats.NetUpBytesPerSec);
    }

    /// <summary>
    /// Drives a 0–100 usage bar by animating its width relative to the track. The bar and
    /// its track share a parent Grid, so we read the track's actual width at runtime, then
    /// glide the fill to the new width so per-second updates move smoothly instead of snapping.
    /// </summary>
    private static void SetUsageBar(System.Windows.FrameworkElement? bar, double percent)
    {
        if (bar?.Parent is not System.Windows.FrameworkElement track) return;

        double trackWidth = track.ActualWidth;
        if (double.IsNaN(trackWidth) || trackWidth <= 0) return;

        double clamped = Math.Clamp(percent, 0, 100);
        double target = trackWidth * (clamped / 100.0);

        // Start from the width currently on screen so the motion is continuous even if a
        // previous animation is still settling.
        double current = double.IsNaN(bar.Width) ? bar.ActualWidth : bar.Width;

        // Skip imperceptible changes to avoid restarting the animation every tick for noise.
        if (Math.Abs(target - current) < 0.5)
        {
            bar.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, null);
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

        bar.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, anim);
    }

    private static string FormatGb(ulong bytes) =>
        (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0");

    /// <summary>
    /// Formats a bytes-per-second rate into a compact human string (B/s, KB/s, MB/s).
    /// </summary>
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

    #endregion
}
