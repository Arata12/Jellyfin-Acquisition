using System.Net;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Tests;

/// <summary>
/// The plugin's "acq-…" correlation tags are deleted from qBittorrent once nothing tracks them any more.
/// </summary>
public class TagCleanupTests
{
    private static void UseConfig(PluginConfiguration config)
    {
        var appPaths = new Mock<IApplicationPaths>();
        var dir = Directory.CreateTempSubdirectory("acq-tags-cfg-").FullName;
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(dir);
        appPaths.Setup(p => p.PluginsPath).Returns(dir);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(config);
        _ = new Plugin(appPaths.Object, xml.Object);
    }

    private static (TrackerService Tracker, Mock<IQBittorrentClient> Qb) TrackerWith(IReadOnlyList<AcquisitionRequest> requests, params string[] qbTags)
    {
        var repo = new Mock<IRequestRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(requests);
        var qb = new Mock<IQBittorrentClient>();
        qb.Setup(q => q.GetTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(qbTags.ToList());
        qb.Setup(q => q.DeleteTagsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var tracker = new TrackerService(repo.Object, Mock.Of<ISonarrClient>(), Mock.Of<IRadarrClient>(), Mock.Of<IProwlarrClient>(), qb.Object,
            Mock.Of<IReleaseService>(), Mock.Of<ILocalMatcherService>(), Mock.Of<ILibraryManager>(), Mock.Of<ILogger<TrackerService>>());
        return (tracker, qb);
    }

    private static List<AcquisitionRequest> Requests() =>
    [
        // Series still in progress: E01 imported, E02 downloading, E03 set aside for review
        new()
        {
            Id = "live",
            Kind = MediaKind.Series,
            Status = RequestStatus.Downloading,
            CorrelationTag = "acq-live-u2",
            Units =
            [
                new DownloadUnit { Id = "u1", Status = RequestStatus.Available, CorrelationTag = "acq-live-u1" },
                new DownloadUnit { Id = "u2", Status = RequestStatus.Downloading, CorrelationTag = "acq-live-u2" },
                new DownloadUnit { Id = "u3", Status = RequestStatus.RequiresAttention, CorrelationTag = "acq-live-u3" }
            ]
        },
        // Series moving on: E01 imported, E02 searching (no torrent yet); the request still points to E01's tag
        new()
        {
            Id = "next",
            Kind = MediaKind.Series,
            Status = RequestStatus.Searching,
            CorrelationTag = "acq-next-u1",
            Units =
            [
                new DownloadUnit { Id = "u1", Status = RequestStatus.Available, CorrelationTag = "acq-next-u1" },
                new DownloadUnit { Id = "u2", Status = RequestStatus.Searching }
            ]
        },
        // Finished movie
        new()
        {
            Id = "done",
            Kind = MediaKind.Movie,
            Status = RequestStatus.Available,
            CorrelationTag = "acq-done",
            Units = [new DownloadUnit { Id = "m", Status = RequestStatus.Available, CorrelationTag = "acq-done" }]
        }
    ];

    [Fact]
    public async Task Cleanup_DeletesTagsOfImportedAndCancelledDownloads_KeepsTrackedAndForeignTags()
    {
        UseConfig(new PluginConfiguration { RemoveTagsAfterImport = true });
        IReadOnlyCollection<string>? deleted = null;
        var (tracker, qb) = TrackerWith(Requests(), "acq-live-u1", "acq-live-u2", "acq-live-u3", "acq-live", "acq-next-u1", "acq-done", "acq-cancelled-x", "mis-series", "radarr");
        qb.Setup(q => q.DeleteTagsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>, CancellationToken>((t, _) => deleted = t)
            .ReturnsAsync(true);

        await tracker.PollActiveRequestsAsync();

        Assert.NotNull(deleted);
        Assert.Equal(["acq-cancelled-x", "acq-done", "acq-live-u1", "acq-next-u1"], deleted!.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cleanup_DisabledInTheConfiguration_DoesNothing()
    {
        UseConfig(new PluginConfiguration { RemoveTagsAfterImport = false });
        var (tracker, qb) = TrackerWith(Requests(), "acq-done", "acq-cancelled-x");

        await tracker.PollActiveRequestsAsync();

        qb.Verify(q => q.GetTagsAsync(It.IsAny<CancellationToken>()), Times.Never);
        qb.Verify(q => q.DeleteTagsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cleanup_RunsAtMostEveryTenMinutes()
    {
        UseConfig(new PluginConfiguration());
        var (tracker, qb) = TrackerWith(Requests(), "acq-done");

        await tracker.PollActiveRequestsAsync();
        await tracker.PollActiveRequestsAsync();

        qb.Verify(q => q.GetTagsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            if (request.RequestUri.AbsolutePath.EndsWith("/auth/login"))
            {
                var login = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                login.Headers.Add("Set-Cookie", "SID=s; path=/");
                return login;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[\"acq-a\",\"otra\"]") };
        }
    }

    [Fact]
    public async Task QBittorrentClient_ReadsAndDeletesTags()
    {
        UseConfig(new PluginConfiguration { QBittorrentUrl = "http://qbittorrent:8080", QBittorrentPassword = "pw" });
        var handler = new RecordingHandler();
        var client = new QBittorrentClient(new HttpClient(handler), Mock.Of<ILogger<QBittorrentClient>>());

        var tags = await client.GetTagsAsync();
        var ok = await client.DeleteTagsAsync(["acq-a", "acq-b"]);

        Assert.Equal(["acq-a", "otra"], tags);
        Assert.True(ok);
        var delete = handler.Calls.Single(c => c.Path.EndsWith("/api/v2/torrents/deleteTags"));
        Assert.Equal("tags=acq-a%2Cacq-b", delete.Body);
    }
}
