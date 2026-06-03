using System;

namespace VNotch.Controllers;

public sealed class TimerWidgetController
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(7);

    public TimeSpan Duration { get; private set; } = DefaultDuration;
    public TimeSpan Remaining { get; private set; } = DefaultDuration;
    public bool IsRunning { get; private set; }
    public bool IsComplete => Remaining <= TimeSpan.Zero;
    public double Progress
    {
        get
        {
            double totalMs = Math.Max(1.0, Duration.TotalMilliseconds);
            double remainingMs = Math.Clamp(Remaining.TotalMilliseconds, 0.0, totalMs);
            return 1.0 - (remainingMs / totalMs);
        }
    }

    public bool Tick(TimeSpan elapsed)
    {
        if (!IsRunning) return false;
        if (elapsed <= TimeSpan.Zero) return false;

        Remaining = Remaining.Subtract(elapsed);
        if (Remaining > TimeSpan.Zero) return false;

        Remaining = TimeSpan.Zero;
        IsRunning = false;
        return true;
    }

    public void Start()
    {
        if (Remaining <= TimeSpan.Zero)
        {
            Remaining = Duration;
        }
        IsRunning = true;
    }

    public void Pause()
    {
        IsRunning = false;
    }

    public bool ToggleRunning()
    {
        if (IsRunning)
        {
            Pause();
        }
        else
        {
            Start();
        }

        return IsRunning;
    }

    public void ResetToDuration()
    {
        IsRunning = false;
        Remaining = Duration;
    }

    public bool ApplyStep(int direction)
    {
        if (IsRunning) return false;

        TimeSpan step = Duration.TotalDays >= 1
            ? TimeSpan.FromHours(1)
            : Duration.TotalHours >= 1
                ? TimeSpan.FromMinutes(5)
                : TimeSpan.FromMinutes(1);

        TimeSpan next = direction > 0
            ? Duration.Add(step)
            : direction < 0
                ? Duration.Subtract(step)
                : Duration;

        if (next < MinDuration) next = MinDuration;
        if (next > MaxDuration) next = MaxDuration;
        if (next == Duration) return false;

        Duration = next;
        Remaining = Duration;
        return true;
    }

    public string FormatRemaining()
    {
        var total = Remaining;
        if (total.TotalDays >= 1)
        {
            int days = (int)total.TotalDays;
            int hours = total.Hours;
            return $"{days}d {hours:D2}h";
        }

        if (total.TotalHours >= 1)
        {
            int hours = (int)total.TotalHours;
            int minutes = total.Minutes;
            return $"{hours:D2}:{minutes:D2}:{total.Seconds:D2}";
        }

        int mins = (int)total.TotalMinutes;
        int seconds = total.Seconds;
        return $"{mins:D2}:{seconds:D2}";
    }
}
