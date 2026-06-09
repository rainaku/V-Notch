namespace VNotch.Models;

public sealed class WeatherInfo
{
    public string City { get; init; } = string.Empty;

    public int Temperature { get; init; }

    public int High { get; init; }

    public int Low { get; init; }

    public string Condition { get; init; } = string.Empty;

    public int WeatherCode { get; init; }

    public bool IsDay { get; init; }
}
