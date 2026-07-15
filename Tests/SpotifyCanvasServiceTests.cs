using System.Net;
using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyCanvasServiceTests
{
    private const string TrackId = "3OHfY25tqY28d16oZczHc8";
    private const string CanvasUrl = "https://canvaz.scdn.co/upload/licensor/video/test.cnvs.mp4";

    [Fact]
    public async Task FetchCanvasAsync_ResolvesTrackAndParsesRepositoryResponse()
    {
        var requests = new List<(Uri Uri, string? Authorization)>();
        var handler = new StubHandler(request =>
        {
            requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            string json = request.RequestUri!.AbsolutePath.EndsWith("/spotify/search", StringComparison.Ordinal)
                ? "{\"data\":{\"tracks\":{\"items\":[{\"id\":\"" + TrackId +
                  "\",\"name\":\"Kill Bill\",\"artists\":[{\"name\":\"SZA\"}],\"duration_ms\":153000}]}}}"
                : "{\"data\":{\"canvasesList\":[{\"canvasUrl\":\"" + CanvasUrl +
                  "\",\"trackUri\":\"spotify:track:" + TrackId + "\"}]}}";
            return JsonResponse(json);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler), new Uri("https://api.example.test/"));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "secret-key");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Equal(2, requests.Count);
        Assert.Equal("/spotify/search", requests[0].Uri.AbsolutePath);
        Assert.Contains("Kill%20Bill%20SZA", requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Equal($"/spotify/canvas", requests[1].Uri.AbsolutePath);
        Assert.Contains($"id={TrackId}", requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.All(requests, request => Assert.Equal("Bearer secret-key", request.Authorization));
    }

    [Fact]
    public async Task FetchCanvasAsync_MismatchedSearchResult_FallsBackWithoutCanvasRequest()
    {
        var handler = new StubHandler(_ => JsonResponse(
            "{\"tracks\":[{\"id\":\"" + TrackId +
            "\",\"name\":\"Another Song\",\"artist\":\"Someone Else\"}]}"));
        using var service = new SpotifyCanvasService(new HttpClient(handler), new Uri("https://api.example.test/"));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "secret-key");

        Assert.Null(result);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task FetchCanvasAsync_UntrustedVideoUrl_IsRejected()
    {
        var handler = new StubHandler(request =>
        {
            string json = request.RequestUri!.AbsolutePath.EndsWith("/spotify/search", StringComparison.Ordinal)
                ? "{\"tracks\":[{\"id\":\"" + TrackId +
                  "\",\"name\":\"Kill Bill\",\"artist\":\"SZA\"}]}"
                : "{\"canvasUrl\":\"https://example.com/untrusted.mp4\"}";
            return JsonResponse(json);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler), new Uri("https://api.example.test/"));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "secret-key");

        Assert.Null(result);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task FetchCanvasAsync_HttpError_ReturnsNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var service = new SpotifyCanvasService(new HttpClient(handler), new Uri("https://api.example.test/"));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "bad-key");

        Assert.Null(result);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void ParseCanvasUri_AcceptsSnakeCaseCanvasUrl()
    {
        Uri? result = SpotifyCanvasService.ParseCanvasUri(
            "{\"canvases\":[{\"canvas_url\":\"" + CanvasUrl + "\"}]}");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(reply(request));
        }
    }
}
