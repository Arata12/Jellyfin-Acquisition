using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Configuration;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class AdapterTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    [Fact]
    public async Task SonarrClient_PreservesPathPrefixAndDoesNotMutateBaseAddress()
    {
        HttpRequestMessage? capturedRequest = null;

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler);
        Assert.Null(httpClient.BaseAddress);

        var mockLogger = new Mock<ILogger<SonarrClient>>();
        var client = new SonarrClient(httpClient, mockLogger.Object);

        await client.LookupSeriesAsync("Breaking Bad");

        Assert.NotNull(capturedRequest);
        Assert.Null(httpClient.BaseAddress); // BaseAddress must never be mutated
        Assert.StartsWith("http://localhost:8989/api/v3/series/lookup", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task ProwlarrClient_FetchTorrent_DoesNotLeakApiKeyOnRedirectToExternalDomain()
    {
        var requests = new List<HttpRequestMessage>();

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            requests.Add(req);

            if (req.RequestUri!.Host == "prowlarr")
            {
                var redirectResp = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirectResp.Headers.Location = new Uri("https://external-tracker.org/download.torrent");
                return Task.FromResult(redirectResp);
            }
            else
            {
                var fileResp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x64, 0x38, 0x3a, 0x61, 0x6e, 0x6e, 0x6f, 0x75, 0x6e, 0x63, 0x65]) // sample torrent bencode
                };
                return Task.FromResult(fileResp);
            }
        });

        var httpClient = new HttpClient(handler);
        var mockLogger = new Mock<ILogger<ProwlarrClient>>();
        var client = new ProwlarrClient(httpClient, mockLogger.Object);

        var result = await client.FetchTorrentAsync("http://prowlarr/download");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests.Count);

        // Second request was to external-tracker.org; it must NOT have X-Api-Key header (R14)
        var secondRequest = requests[1];
        Assert.Equal("external-tracker.org", secondRequest.RequestUri?.Host);
        Assert.False(secondRequest.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task ProwlarrClient_FetchTorrent_Handles429RateLimitWithRetryAfter()
    {
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(resp);
        });

        var httpClient = new HttpClient(handler);
        var mockLogger = new Mock<ILogger<ProwlarrClient>>();
        var client = new ProwlarrClient(httpClient, mockLogger.Object);

        var result = await client.FetchTorrentAsync("http://prowlarr/download");

        Assert.False(result.IsSuccess);
        Assert.Contains("429", result.ErrorMessage);
        Assert.Contains("60", result.ErrorMessage);
    }

    [Fact]
    public async Task ProwlarrClient_FetchTorrent_NormalizesBase32InfoHashToHex()
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var mockLogger = new Mock<ILogger<ProwlarrClient>>();
        var client = new ProwlarrClient(httpClient, mockLogger.Object);

        // 32-character base32 hash
        // "MFRGGZDFMY======" or standard 32 char: "4be4y2g7tq6a7n7vsl3k3t7t5s6d2qra"
        // Let's use a 32-char Base32 string: "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP"
        var base32Hash = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        var magnetUri = $"magnet:?xt=urn:btih:{base32Hash}&dn=TestMovie";

        var result = await client.FetchTorrentAsync(magnetUri);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.InfoHash);
        // Normalized infohash must be 40-character hexadecimal string
        Assert.Equal(40, result.InfoHash.Length);
        Assert.Matches("^[0-9a-f]{40}$", result.InfoHash);
    }
}
