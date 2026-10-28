using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent;
using Jellyfin.Plugin.Acquisition.Adapters.QBittorrent.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class TrackingTests
{
    [Fact]
    public async Task ProcessRequest_DownloadingToImporting_WhenTorrentCompletes()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockLocal = new Mock<ILocalMatcherService>();
        var mockLibrary = new Mock<ILibraryManager>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var req = new AcquisitionRequest
        {
            Id = "req-1",
            Title = "Test Movie",
            Kind = MediaKind.Movie,
            Status = RequestStatus.Downloading,
            TorrentHash = "hash123",
            CorrelationTag = "acq-req-1"
        };

        // qBittorrent returns completed torrent
        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), "hash123", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QBittorrentTorrentInfo
                {
                    Hash = "hash123",
                    Progress = 1.0,
                    State = "uploading"
                }
            ]);

        mockRadarr.Setup(r => r.GetQueueAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            mockRadarr.Object,
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            mockLocal.Object,
            mockLibrary.Object,
            mockLogger.Object);

        await tracker.ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Importing, req.Status);
        mockRepo.Verify(r => r.SaveOrUpdateAsync(It.Is<AcquisitionRequest>(a => a.Status == RequestStatus.Importing), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessRequest_ArrQueueMismatch_FlagsRequiresAttention()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockLocal = new Mock<ILocalMatcherService>();
        var mockLibrary = new Mock<ILibraryManager>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var req = new AcquisitionRequest
        {
            Id = "req-series-1",
            Title = "Expected Series",
            Kind = MediaKind.Series,
            ArrId = 42,
            Status = RequestStatus.Downloading,
            TorrentHash = "hash-series",
            CorrelationTag = "acq-req-series-1"
        };

        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), "hash-series", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QBittorrentTorrentInfo
                {
                    Hash = "hash-series",
                    Progress = 0.2,
                    State = "downloading"
                }
            ]);

        // Sonarr queue associated the download with a DIFFERENT series ID (999 instead of 42)
        mockSonarr.Setup(s => s.GetQueueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrQueueItem
                {
                    DownloadId = "hash-series",
                    SeriesId = 999
                }
            ]);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            mockRadarr.Object,
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            mockLocal.Object,
            mockLibrary.Object,
            mockLogger.Object);

        await tracker.ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.NotNull(req.AttentionReason);
        Assert.Contains("queue association mismatch", req.AttentionReason);
    }

    [Fact]
    public async Task ProcessRequest_Importing_StaysInImporting_WhenArrImported_ButJellyfinNotReady()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockLocal = new Mock<ILocalMatcherService>();
        var mockLibrary = new Mock<ILibraryManager>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var req = new AcquisitionRequest
        {
            Id = "req-movie-1",
            Title = "Ready in Radarr Movie",
            Kind = MediaKind.Movie,
            ArrId = 77,
            Status = RequestStatus.Importing,
            UserId = Guid.NewGuid()
        };

        // Radarr reports file is imported
        mockRadarr.Setup(r => r.GetMovieByIdAsync(77, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrMovie { Id = 77, Title = "Ready in Radarr Movie", HasFile = true });

        // Jellyfin library matcher has NOT found it yet
        mockLocal.Setup(m => m.FindLocalMatch(req.UserId, req.Kind, req.Title, req.Year, req.ProviderIds))
            .Returns((Guid?)null);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            mockRadarr.Object,
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            mockLocal.Object,
            mockLibrary.Object,
            mockLogger.Object);

        await tracker.ProcessRequestAsync(req);
        await Task.Delay(50); // Allow throttled background refresh task to dispatch

        // Status should still be Importing, not Available
        Assert.Equal(RequestStatus.Importing, req.Status);
        Assert.Null(req.LocalItemId);
        Assert.Null(req.CompletedAt);

        // Should have triggered library validation
        mockLibrary.Verify(l => l.ValidateMediaLibrary(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessRequest_Importing_Series_StaysInImporting_WhenOnlyPartialEpisodesImported()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockLocal = new Mock<ILocalMatcherService>();
        var mockLibrary = new Mock<ILibraryManager>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var req = new AcquisitionRequest
        {
            Id = "req-series-partial",
            Title = "Multi-Episode Series",
            Kind = MediaKind.Series,
            ArrId = 55,
            Status = RequestStatus.Importing,
            UserId = Guid.NewGuid(),
            Scope = new RequestScope
            {
                All = false,
                TargetEpisodes =
                [
                    new EpisodeTarget { EpisodeId = 101, SeasonNumber = 1, EpisodeNumber = 1 },
                    new EpisodeTarget { EpisodeId = 102, SeasonNumber = 1, EpisodeNumber = 2 },
                    new EpisodeTarget { EpisodeId = 103, SeasonNumber = 1, EpisodeNumber = 3 }
                ]
            }
        };

        // Sonarr returns only episode 101 as imported, 102 and 103 still missing
        mockSonarr.Setup(s => s.GetEpisodesAsync(55, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true },
                new SonarrEpisode { Id = 102, SeasonNumber = 1, EpisodeNumber = 2, HasFile = false },
                new SonarrEpisode { Id = 103, SeasonNumber = 1, EpisodeNumber = 3, HasFile = false }
            ]);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            mockRadarr.Object,
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            mockLocal.Object,
            mockLibrary.Object,
            mockLogger.Object);

        await tracker.ProcessRequestAsync(req);

        // Status must remain Importing because not all targeted episodes are imported in Sonarr
        Assert.Equal(RequestStatus.Importing, req.Status);
        Assert.Null(req.CompletedAt);
        mockLibrary.Verify(l => l.ValidateMediaLibrary(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRequest_Importing_TransitionsToAvailable_WhenArrImportedAndJellyfinConfirms()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockLocal = new Mock<ILocalMatcherService>();
        var mockLibrary = new Mock<ILibraryManager>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var expectedItemId = Guid.NewGuid();
        var req = new AcquisitionRequest
        {
            Id = "req-series-done",
            Title = "Finished Series",
            Kind = MediaKind.Series,
            ArrId = 60,
            Status = RequestStatus.Importing,
            UserId = Guid.NewGuid(),
            Scope = new RequestScope
            {
                All = false,
                TargetEpisodes =
                [
                    new EpisodeTarget { EpisodeId = 201, SeasonNumber = 1, EpisodeNumber = 1 }
                ]
            }
        };

        // Sonarr confirms target episode has file
        mockSonarr.Setup(s => s.GetEpisodesAsync(60, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrEpisode { Id = 201, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true }
            ]);

        // Jellyfin library confirms item is indexed and accessible
        mockLocal.Setup(m => m.FindLocalMatch(req.UserId, req.Kind, req.Title, req.Year, req.ProviderIds))
            .Returns(expectedItemId);
        mockLocal.Setup(m => m.AreEpisodesAvailable(req.UserId, expectedItemId, It.IsAny<IReadOnlyList<EpisodeTarget>>()))
            .Returns(true);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            mockRadarr.Object,
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            mockLocal.Object,
            mockLibrary.Object,
            mockLogger.Object);

        await tracker.ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Available, req.Status);
        Assert.Equal(expectedItemId, req.LocalItemId);
        Assert.NotNull(req.CompletedAt);
        mockRepo.Verify(r => r.SaveOrUpdateAsync(It.Is<AcquisitionRequest>(a => a.Status == RequestStatus.Available), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessRequest_Searching_AdoptsExistingTorrent_WithoutCallingAddTorrent()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockLocal = new Mock<ILocalMatcherService>();
        var mockLibrary = new Mock<ILibraryManager>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var req = new AcquisitionRequest
        {
            Id = "req-search-dup",
            Title = "Existing Movie",
            Kind = MediaKind.Movie,
            Status = RequestStatus.Searching,
            UserId = Guid.NewGuid(),
            SelectedRelease = new ReleaseCandidate
            {
                Id = "rel-1",
                Title = "Existing.Movie.2024.1080p",
                DownloadUrl = "http://indexer/torrent1"
            }
        };

        // qBittorrent already has a torrent with tag "acq-req-search-dup"
        mockQb.Setup(q => q.GetTorrentsInfoAsync(null, "acq-req-search-dup", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QBittorrentTorrentInfo
                {
                    Hash = "existing-hash-999",
                    Name = "Existing.Movie.2024.1080p",
                    State = "downloading"
                }
            ]);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            mockRadarr.Object,
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            mockLocal.Object,
            mockLibrary.Object,
            mockLogger.Object);

        await tracker.ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Downloading, req.Status);
        Assert.Equal("existing-hash-999", req.TorrentHash);

        // AddTorrent must NOT have been called (R07 deduplication)
        mockQb.Verify(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        mockProwlarr.Verify(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
