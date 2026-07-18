using System;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Services;

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
    private string _lastRenderedLanguage = "";

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
        if (minuteKey == _lastRenderedMinuteKey && _lastRenderedLanguage == Loc.CurrentLanguage) return;
        _lastRenderedMinuteKey = minuteKey;
        _lastRenderedLanguage = Loc.CurrentLanguage;

        Text = FormatLocalizedTime(now);
    }

    public void RefreshLocalization()
    {
        _lastRenderedMinuteKey = -1;
        UpdateText();
    }

    private static string FormatLocalizedTime(DateTime now)
    {
        int hour = now.Hour % 12;
        if (hour == 0) hour = 12;
        int minute = now.Minute;
        string prefix = Loc.Get("wordClock.prefix");

        return Loc.CurrentLanguage switch
        {
            "vi" => $"{prefix}\n{SpellVietnamese(hour)}\n{(minute == 0 ? Loc.Get("wordClock.oclock") : SpellVietnamese(minute))}",
            "es" => $"{prefix}\n{SpellSpanish(hour)}\n{(minute == 0 ? Loc.Get("wordClock.oclock") : SpellSpanish(minute))}",
            "fr" => $"{prefix}\n{SpellFrench(hour)}\n{(minute == 0 ? Loc.Get("wordClock.oclock") : SpellFrench(minute))}",
            "de" => $"{prefix}\n{SpellGerman(hour)}\n{(minute == 0 ? Loc.Get("wordClock.oclock") : SpellGerman(minute))}",
            "ja" => $"{prefix}\n{SpellJapanese(hour)}時\n{(minute == 0 ? Loc.Get("wordClock.oclock") : SpellJapanese(minute) + "分")}",
            "hi" => minute == 0
                ? $"{prefix}\n{SpellHindi(hour)}\n{Loc.Get("wordClock.oclock")}"
                : $"{prefix}\n{SpellHindi(hour)} बजकर\n{SpellHindi(minute)} मिनट",
            _ => $"{prefix}\n{SpellHour(now.Hour)}\n{SpellMinute(minute)}"
        };
    }

    private static string SpellHour(int hour24)
    {
        int h = hour24 % 12;
        return Ones[h];
    }

    private static string SpellMinute(int minute)
    {
        if (minute == 0) return Loc.Get("wordClock.oclock");
        if (minute < 10) return "Oh" + Ones[minute];
        if (minute < 20) return Teens[minute - 10];

        string tens = Tens[minute / 10];
        int unit = minute % 10;
        return unit == 0 ? tens : tens + Ones[unit].ToLowerInvariant();
    }

    private static string SpellVietnamese(int value)
    {
        string[] units = { "không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín" };
        if (value < 10) return units[value];
        if (value < 20) return value == 10 ? "mười" : "mười " + (value == 15 ? "lăm" : units[value - 10]);
        int tens = value / 10;
        int unit = value % 10;
        if (unit == 0) return units[tens] + " mươi";
        string unitText = unit == 1 ? "mốt" : unit == 5 ? "lăm" : units[unit];
        return units[tens] + " mươi " + unitText;
    }

    private static string SpellSpanish(int value)
    {
        string[] small =
        {
            "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve",
            "diez", "once", "doce", "trece", "catorce", "quince", "dieciséis", "diecisiete", "dieciocho", "diecinueve",
            "veinte", "veintiuno", "veintidós", "veintitrés", "veinticuatro", "veinticinco", "veintiséis", "veintisiete", "veintiocho", "veintinueve"
        };
        if (value < small.Length) return small[value];
        string[] tens = { "", "", "veinte", "treinta", "cuarenta", "cincuenta" };
        int unit = value % 10;
        return unit == 0 ? tens[value / 10] : $"{tens[value / 10]} y {small[unit]}";
    }

    private static string SpellFrench(int value)
    {
        string[] small =
        {
            "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf",
            "dix", "onze", "douze", "treize", "quatorze", "quinze", "seize", "dix-sept", "dix-huit", "dix-neuf"
        };
        if (value < small.Length) return small[value];
        string[] tens = { "", "", "vingt", "trente", "quarante", "cinquante" };
        int unit = value % 10;
        if (unit == 0) return tens[value / 10];
        return unit == 1 ? $"{tens[value / 10]}-et-un" : $"{tens[value / 10]}-{small[unit]}";
    }

    private static string SpellGerman(int value)
    {
        string[] small =
        {
            "null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun",
            "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"
        };
        if (value < small.Length) return small[value];
        string[] tens = { "", "", "zwanzig", "dreißig", "vierzig", "fünfzig" };
        int unit = value % 10;
        if (unit == 0) return tens[value / 10];
        string unitText = unit == 1 ? "ein" : small[unit];
        return unitText + "und" + tens[value / 10];
    }

    private static string SpellJapanese(int value)
    {
        string[] digits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        if (value < 10) return digits[value];
        int tens = value / 10;
        int unit = value % 10;
        string result = tens == 1 ? "十" : digits[tens] + "十";
        return unit == 0 ? result : result + digits[unit];
    }

    private static string SpellHindi(int value)
    {
        string[] numbers =
        {
            "शून्य", "एक", "दो", "तीन", "चार", "पाँच", "छह", "सात", "आठ", "नौ",
            "दस", "ग्यारह", "बारह", "तेरह", "चौदह", "पंद्रह", "सोलह", "सत्रह", "अठारह", "उन्नीस",
            "बीस", "इक्कीस", "बाईस", "तेईस", "चौबीस", "पच्चीस", "छब्बीस", "सत्ताईस", "अट्ठाईस", "उनतीस",
            "तीस", "इकतीस", "बत्तीस", "तैंतीस", "चौंतीस", "पैंतीस", "छत्तीस", "सैंतीस", "अड़तीस", "उनतालीस",
            "चालीस", "इकतालीस", "बयालीस", "तैंतालीस", "चवालीस", "पैंतालीस", "छियालीस", "सैंतालीस", "अड़तालीस", "उनचास",
            "पचास", "इक्यावन", "बावन", "तिरपन", "चौवन", "पचपन", "छप्पन", "सत्तावन", "अट्ठावन", "उनसठ"
        };
        return numbers[Math.Clamp(value, 0, numbers.Length - 1)];
    }
}
