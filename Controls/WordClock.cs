using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VNotch.Controls;

/// <summary>
/// A compact "word clock" complication that spells the current local time out in
/// English, e.g. 9:25 → "It's Nine Twentyfive", 15:00 → "It's Three O'Clock".
/// The phrase is laid out across three lines (It's / hour / minute) to mirror the
/// iOS-style stacked text widget. Always uses the local system time zone
/// (<see cref="DateTime.Now"/>) and self-updates once the minute rolls over.
/// </summary>
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
        // Tick every few seconds; the displayed text only changes once a minute, so we
        // skip the rebuild unless the hour/minute actually rolled over.
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
        // Force a refresh on (re)appearing so the text is correct immediately.
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
        DateTime now = DateTime.Now; // local system time zone (PC default)
        int minuteKey = now.Hour * 60 + now.Minute;
        if (minuteKey == _lastRenderedMinuteKey) return;
        _lastRenderedMinuteKey = minuteKey;

        Text = $"It's\n{SpellHour(now.Hour)}\n{SpellMinute(now.Minute)}";
    }

    /// <summary>Spells the hour using a 12-hour clock (0/12/24 → "Twelve").</summary>
    private static string SpellHour(int hour24)
    {
        int h = hour24 % 12; // 0..11, where 0 means 12 o'clock
        return Ones[h];
    }

    /// <summary>
    /// Spells the minute as a single concatenated word: 0 → "O'Clock", 1-9 → "OhFive"
    /// style, 10-19 → "Ten".."Nineteen", 20+ → "Twenty" / "Twentyfive" style.
    /// </summary>
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
