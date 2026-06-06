namespace VNotch.Models;

/// <summary>
/// Snapshot of the current weather for the user's detected location.
/// Temperatures are already expressed in the unit requested from the service (Celsius by default).
/// </summary>
public sealed class WeatherInfo
{
    /// <summary>Display name of the detected location (e.g. "Hanoi").</summary>
    public string City { get; init; } = string.Empty;

    /// <summary>Current temperature, rounded to the nearest degree.</summary>
    public int Temperature { get; init; }

    /// <summary>Forecasted daily high, rounded to the nearest degree.</summary>
    public int High { get; init; }

    /// <summary>Forecasted daily low, rounded to the nearest degree.</summary>
    public int Low { get; init; }

    /// <summary>Human readable condition text (e.g. "Clear", "Partly cloudy").</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>WMO weather interpretation code returned by Open-Meteo.</summary>
    public int WeatherCode { get; init; }

    /// <summary>True when the reading is during daytime (drives sun vs. moon glyph).</summary>
    public bool IsDay { get; init; }
}
