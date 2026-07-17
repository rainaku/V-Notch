using CommunityToolkit.Mvvm.ComponentModel;

namespace VNotch.ViewModels;

public partial class TimerViewModel : ObservableObject
{
    private static readonly TimeSpan Minimum = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Maximum = TimeSpan.FromDays(7);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private TimeSpan _duration = TimeSpan.FromMinutes(25);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(Progress))]
    private TimeSpan _remaining = TimeSpan.FromMinutes(25);

    [ObservableProperty]
    private bool _isRunning;

    public string DisplayText => Format(Remaining);
    public double Progress => Duration <= TimeSpan.Zero ? 0 :
        1 - Math.Clamp(Remaining.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);

    public void Start()
    {
        if (Remaining <= TimeSpan.Zero) Remaining = Duration;
        IsRunning = true;
    }

    public void Pause() => IsRunning = false;

    public void Reset()
    {
        IsRunning = false;
        Remaining = Duration;
    }

    public bool Tick(TimeSpan elapsed)
    {
        if (!IsRunning || elapsed <= TimeSpan.Zero) return false;
        Remaining -= elapsed;
        if (Remaining > TimeSpan.Zero) return false;
        Remaining = TimeSpan.Zero;
        IsRunning = false;
        return true;
    }

    public bool Adjust(int direction)
    {
        if (IsRunning || direction == 0) return false;
        var step = Duration.TotalDays >= 1 ? TimeSpan.FromHours(1) :
            Duration.TotalHours >= 1 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1);
        var next = Duration + (direction > 0 ? step : -step);
        next = TimeSpan.FromTicks(Math.Clamp(next.Ticks, Minimum.Ticks, Maximum.Ticks));
        if (next == Duration) return false;
        Duration = next;
        Remaining = next;
        return true;
    }

    private static string Format(TimeSpan value)
    {
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}d {value.Hours:D2}h";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
        return $"{(int)value.TotalMinutes:D2}:{value.Seconds:D2}";
    }
}
