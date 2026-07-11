using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class UpdateServiceSecurityTests
{
    [Fact]
    public void SelectReleaseAssets_PrefersSelfContained_AndNeverSelectsOtherExe()
    {
        using var doc = JsonDocument.Parse("{\"assets\":[{\"name\":\"malware.exe\",\"browser_download_url\":\"https://example.test/malware.exe\"},{\"name\":\"V-Notch-Setup.exe\",\"browser_download_url\":\"https://example.test/setup\"},{\"name\":\"V-Notch-Setup.exe.sha256\",\"browser_download_url\":\"https://example.test/setup.sha\"},{\"name\":\"V-Notch-Setup-SelfContained.exe\",\"browser_download_url\":\"https://example.test/sc\"},{\"name\":\"V-Notch-Setup-SelfContained.exe.sha256\",\"browser_download_url\":\"https://example.test/sc.sha\"}]}");
        var selected = UpdateService.SelectReleaseAssets(doc.RootElement);
        Assert.Equal("V-Notch-Setup-SelfContained.exe", selected.Installer?.Name);
        Assert.Equal("V-Notch-Setup-SelfContained.exe.sha256", selected.Checksum?.Name);
    }

    [Fact]
    public void SelectReleaseAssets_ReturnsNoInstaller_WhenExactNameMissing()
    {
        using var doc = JsonDocument.Parse("{\"assets\":[{\"name\":\"V-Notch-Setup-evil.exe\",\"browser_download_url\":\"https://example.test/a\"}]}");
        Assert.Null(UpdateService.SelectReleaseAssets(doc.RootElement).Installer);
    }

    [Fact]
    public void HashesMatch_AcceptsExactHash_AndRejectsOneByteChange()
    {
        const string good = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string changed = "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.True(UpdateService.HashesMatch(good, good.ToLowerInvariant()));
        Assert.False(UpdateService.HashesMatch(good, changed));
    }

    [Fact]
    public async Task DownloadInstaller_RejectsHttpRedirect()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://evil.test/setup") } });
        await using var temp = new TempFile();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync("https://example.test/setup", temp.Path, null, default));
    }

    [Fact]
    public async Task DownloadInstaller_RejectsContentOverLimit()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = UpdateSecurityPolicy.MaximumInstallerBytes + 1;
        var service = CreateService(_ => response);
        await using var temp = new TempFile();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync("https://example.test/setup", temp.Path, null, default));
    }

    [Fact]
    public async Task DownloadInstaller_HonorsCancellation()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await using var temp = new TempFile();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadInstallerAsync("https://example.test/setup", temp.Path, null, cts.Token));
    }

    private static UpdateService CreateService(Func<HttpRequestMessage, HttpResponseMessage> reply) =>
        new(new HttpClient(new StubHandler(reply)), new UpdateSecurityPolicy(), _ => (true, string.Empty));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
    private sealed class BlockingStream : MemoryStream
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromCanceled<int>(token); }
    private sealed class TempFile : IAsyncDisposable
    { public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")); public ValueTask DisposeAsync() { if (File.Exists(Path)) File.Delete(Path); return ValueTask.CompletedTask; } }
}
