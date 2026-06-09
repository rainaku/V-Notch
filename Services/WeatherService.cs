using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public sealed class WeatherService : IWeatherService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    static WeatherService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("V-Notch/1.7.0 (https://github.com/rainaku/V-Notch)");
    }

    public async Task<WeatherInfo?> GetCurrentWeatherAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var location = await ResolveLocationAsync(cancellationToken).ConfigureAwait(false);
            if (location is null)
            {
                RuntimeLog.Log("WEATHER", "Could not resolve location.");
                return null;
            }

            var (lat, lon, city) = location.Value;

            string url =
                "https://api.open-meteo.com/v1/forecast" +
                $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                "&current=temperature_2m,weather_code,is_day" +
                "&daily=temperature_2m_max,temperature_2m_min" +
                "&timezone=auto&forecast_days=1";

            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log("WEATHER", $"Open-Meteo HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current", out var current))
            {
                RuntimeLog.Log("WEATHER", "Open-Meteo response missing 'current'.");
                return null;
            }

            double temp = current.GetProperty("temperature_2m").GetDouble();
            int weatherCode = current.GetProperty("weather_code").GetInt32();
            bool isDay = current.TryGetProperty("is_day", out var isDayProp) && isDayProp.GetInt32() == 1;

            double high = temp, low = temp;
            if (root.TryGetProperty("daily", out var daily))
            {
                if (daily.TryGetProperty("temperature_2m_max", out var maxArr) && maxArr.GetArrayLength() > 0)
                    high = maxArr[0].GetDouble();
                if (daily.TryGetProperty("temperature_2m_min", out var minArr) && minArr.GetArrayLength() > 0)
                    low = minArr[0].GetDouble();
            }

            var info = new WeatherInfo
            {
                City = city,
                Temperature = (int)Math.Round(temp),
                High = (int)Math.Round(high),
                Low = (int)Math.Round(low),
                WeatherCode = weatherCode,
                Condition = DescribeWeatherCode(weatherCode),
                IsDay = isDay
            };

            RuntimeLog.Log("WEATHER", $"{info.City} {info.Temperature}° {info.Condition} (H:{info.High} L:{info.Low})");
            return info;
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Log("WEATHER", "Request timed out or was cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("WEATHER", $"Error: {ex.Message}");
            return null;
        }
    }

    private static async Task<(double lat, double lon, string city)?> ResolveLocationAsync(CancellationToken token)
    {
        var result = await TryIpWhoIsAsync(token).ConfigureAwait(false);
        if (result is not null) return result;

        return await TryIpApiComAsync(token).ConfigureAwait(false);
    }

    private static async Task<(double, double, string)?> TryIpWhoIsAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync("https://ipwho.is/", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log("WEATHER", $"ipwho.is HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var successProp) &&
                successProp.ValueKind == JsonValueKind.False)
            {
                string reason = root.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? "unknown"
                    : "unknown";
                RuntimeLog.Log("WEATHER", $"ipwho.is error: {reason}");
                return null;
            }

            if (!root.TryGetProperty("latitude", out var latProp) ||
                !root.TryGetProperty("longitude", out var lonProp))
                return null;

            double lat = latProp.GetDouble();
            double lon = lonProp.GetDouble();
            string city = root.TryGetProperty("city", out var cityProp)
                ? cityProp.GetString() ?? string.Empty
                : string.Empty;

            return (lat, lon, city);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("WEATHER", $"ipwho.is failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<(double, double, string)?> TryIpApiComAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http
                .GetAsync("http://ip-api.com/json/?fields=status,city,lat,lon", token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetString() != "success")
                return null;
            if (!root.TryGetProperty("lat", out var latProp) ||
                !root.TryGetProperty("lon", out var lonProp))
                return null;

            double lat = latProp.GetDouble();
            double lon = lonProp.GetDouble();
            string city = root.TryGetProperty("city", out var cityProp)
                ? cityProp.GetString() ?? string.Empty
                : string.Empty;

            return (lat, lon, city);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("WEATHER", $"ip-api.com failed: {ex.Message}");
            return null;
        }
    }

    public static string DescribeWeatherCode(int code) => code switch
    {
        0 => "Clear",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm",
        _ => "—"
    };
}
