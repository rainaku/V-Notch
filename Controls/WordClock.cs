using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VNotch.Controls;

public class WordClock : TextBlock
{
    private static readonly string[] Ones =
    {
        "Twelve", "One", "Two", "Three", "Four", "Five", "Six",
        "Seven", "Eight", "Nine", "Ten", "Eleven"
    };

    private static readonly string[] Teens =
    {
        "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen",
        "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    };

    private static readonly string[] Tens =
    {
        "", "", "Twenty", "Thirty", "Forty", "Fifty"
    };

    private readonly DispatcherTimer _timer;
    private bool _isRunning;
    private int _lastRenderedMinuteKey = -1;

    public WordClock()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => UpdateText();

        Loaded += (_, _) => UpdateRunningState();
        Unloaded += (_, _) => StopTimer();
        IsVisibleChanged += (_, _) => UpdateRunningState();
    }

    private void UpdateRunningState()
    {
        if (IsVisible)
            StartTimer();
        else
            StopTimer();
    }

    private void StartTimer()
    {
        if (_isRunning) return;
        _isRunning = true;
        _timer.Start();
        _lastRenderedMinuteKey = -1;
        UpdateText();
    }

    private void StopTimer()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _timer.Stop();
    }

    private void UpdateText()
    {
        DateTime now = DateTime.Now;
        int minuteKey = now.Hour * 60 + now.Minute;
        if (minuteKey == _lastRenderedMinuteKey) return;
        _lastRenderedMinuteKey = minuteKey;

        Text = $"It's\n{SpellHour(now.Hour)}\n{SpellMinute(now.Minute)}";
    }

    private static string SpellHour(int hour24)
    {
        int h = hour24 % 12;
        return Ones[h];
    }

    private static string SpellMinute(int minute)
    {
        if (minute == 0) return "O'Clock";
        if (minute < 10) return "Oh" + Ones[minute];
        if (minute < 20) return Teens[minute - 10];

        string tens = Tens[minute / 10];
        int unit = minute % 10;
        return unit == 0 ? tens : tens + Ones[unit].ToLowerInvariant();
    }
}
