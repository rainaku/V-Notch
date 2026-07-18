namespace VNotch.Services;

public static class WeatherConditionFormatter
{
    public static string Format(int weatherCode) => Loc.Get(GetLocalizationKey(weatherCode));

    internal static string GetLocalizationKey(int weatherCode) => weatherCode switch
    {
        0 => "weather.condition.clear",
        1 => "weather.condition.mainlyClear",
        2 => "weather.condition.partlyCloudy",
        3 => "weather.condition.cloudy",
        45 or 48 => "weather.condition.fog",
        51 or 53 or 55 => "weather.condition.drizzle",
        56 or 57 => "weather.condition.freezingDrizzle",
        61 or 63 or 65 => "weather.condition.rain",
        66 or 67 => "weather.condition.freezingRain",
        71 or 73 or 75 => "weather.condition.snow",
        77 => "weather.condition.snowGrains",
        80 or 81 or 82 => "weather.condition.rainShowers",
        85 or 86 => "weather.condition.snowShowers",
        95 or 96 or 99 => "weather.condition.thunderstorm",
        _ => "weather.condition.unknown"
    };
}
