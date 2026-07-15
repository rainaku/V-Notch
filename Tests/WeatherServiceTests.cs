using System.Net;
using System.Net.Http;
using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class WeatherServiceTests
{
    [Fact]
    public async Task CancelledBeforeRequest_ReturnsNull()
    {
        var handler = new StubHandler(_ => throw new Xunit.Sdk.XunitException("HTTP request was sent"));
        var service = new WeatherService(new HttpClient(handler));

        Assert.Null(await service.GetCurrentWeatherAsync("   ", new CancellationToken(true)));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ManualCity_UsesHttpsOnly_AndReturnsNullForInvalidLocation()
    {
        var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json")
        });
        var service = new WeatherService(new HttpClient(handler));

        Assert.Null(await service.GetCurrentWeatherAsync("Hanoi"));
        Assert.Equal("https", handler.LastUri!.Scheme);
        Assert.Equal("geocoding-api.open-meteo.com", handler.LastUri.Host);
    }

    [Fact]
    public async Task CancelledRequest_ReturnsNull()
    {
        var handler = new StubHandler(_ => throw new OperationCanceledException());
        var service = new WeatherService(new HttpClient(handler));

        Assert.Null(await service.GetCurrentWeatherAsync("Hanoi", new CancellationToken(true)));
    }

    [Fact]
    public async Task ManualCity_ParsesGeocodingAndForecastResponses()
    {
        var handler = new StubHandler(request =>
        {
            string json = request.RequestUri!.Host == "geocoding-api.open-meteo.com"
                ? "{\"results\":[{\"name\":\"Hanoi\",\"latitude\":21.0245,\"longitude\":105.84117}]}"
                : "{\"current\":{\"temperature_2m\":30.6,\"weather_code\":2,\"is_day\":1}," +
                  "\"daily\":{\"temperature_2m_max\":[34.5],\"temperature_2m_min\":[27.6]}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var service = new WeatherService(new HttpClient(handler));

        var weather = await service.GetCurrentWeatherAsync("Hanoi");

        Assert.NotNull(weather);
        Assert.Equal(2, handler.Requests);
        Assert.Equal("Hanoi", weather!.City);
        Assert.Equal(31, weather.Temperature);
        Assert.Equal(35, weather.High);
        Assert.Equal(28, weather.Low);
        Assert.Equal("Partly cloudy", weather.Condition);
        Assert.True(weather.IsDay);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            LastUri = request.RequestUri;
            return Task.FromResult(reply(request));
        }
    }
}
