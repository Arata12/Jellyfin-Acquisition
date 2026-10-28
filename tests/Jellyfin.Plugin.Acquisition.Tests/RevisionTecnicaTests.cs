using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
using Jellyfin.Plugin.Acquisition.Configuration;
using Jellyfin.Plugin.Acquisition.Controllers;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Jellyfin.Plugin.Acquisition.Storage;
using Jellyfin.Plugin.Acquisition.Tracking;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class RevisionTecnicaTests
{
    private class MockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _func;
        public MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> func) => _func = func;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => _func(req, ct);
    }

    [Fact]
    public async Task N01_SingleEpisodeS02E03_OnlyAcceptsS02E03_RejectsSeasonPackAndOtherEpisodes()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        var request = new AcquisitionRequest
        {
            Title = "Severance",
            Kind = MediaKind.Series,
            ArrId = 10,
            Scope = new RequestScope
            {
                All = false,
                TargetEpisodes = [new EpisodeTarget { EpisodeId = 203, SeasonNumber = 2, EpisodeNumber = 3 }]
            }
        };

        var unit = new DownloadUnit
        {
            SeasonNumber = 2,
            EpisodeNumbers = [3],
            EpisodeIds = [203],
            IsFullSeasonPack = false
        };

        var prowlarrReleases = new List<ProwlarrRelease>
        {
            new() { Title = "Severance.S01E01.1080p.WEB-DL", Protocol = "torrent", Seeders = 50 },
            new() { Title = "Severance.S02E01.1080p.WEB-DL", Protocol = "torrent", Seeders = 50 },
            new() { Title = "Severance.S02.Complete.1080p.WEB-DL", Protocol = "torrent", Seeders = 50 },
            new() { Title = "Severance.S02E03.1080p.WEB-DL", Protocol = "torrent", Seeders = 50 }
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prowlarrReleases);

        // Sonarr parse associates title
        mockSonarr.Setup(s => s.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrParseResult
            {
                Series = new SonarrSeries { Id = 10, Title = "Severance" }
            });

        var releaseService = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);
        var results = await releaseService.SearchAndEvaluateReleasesForUnitAsync(request, unit);

        Assert.Equal(4, results.Count);

        // S01E01 must be rejected for Season mismatch
        var s01e01 = results.First(r => r.Title.Contains("S01E01"));
        Assert.False(s01e01.IsEligible);
        Assert.Contains(s01e01.RejectionReasons, r => r.Contains("Season mismatch"));

        // S02E01 must be rejected for Episode mismatch
        var s02e01 = results.First(r => r.Title.Contains("S02E01"));
        Assert.False(s02e01.IsEligible);
        Assert.Contains(s02e01.RejectionReasons, r => r.Contains("Episode mismatch"));

        // S02 Season Pack must be rejected for Coverage mismatch (user requested standalone episode)
        var s02Pack = results.First(r => r.Title.Contains("S02.Complete"));
        Assert.False(s02Pack.IsEligible);
        Assert.Contains(s02Pack.RejectionReasons, r => r.Contains("Coverage mismatch"));

        // S02E03 must be eligible and top candidate
        var s02e03 = results.First(r => r.Title.Contains("S02E03"));
        Assert.True(s02e03.IsEligible);
        Assert.Empty(s02e03.RejectionReasons);

        var best = releaseService.PickBestRelease(results);
        Assert.NotNull(best);
        Assert.Equal(s02e03.Title, best.Title);
    }

    [Fact]
    public async Task N02_SonarrErrorOrEmptyOrPartial_NeverMarksAvailable_EvenIfSeriesExistsInJellyfin()
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

        var userId = Guid.NewGuid();
        var existingSeriesId = Guid.NewGuid();

        var request = new AcquisitionRequest
        {
            Id = "req-n02",
            Title = "Better Call Saul",
            Kind = MediaKind.Series,
            ArrId = 40,
            UserId = userId,
            Status = RequestStatus.Importing,
            Scope = new RequestScope
            {
                All = false,
                TargetEpisodes =
                [
                    new EpisodeTarget { EpisodeId = 601, SeasonNumber = 6, EpisodeNumber = 1 },
                    new EpisodeTarget { EpisodeId = 602, SeasonNumber = 6, EpisodeNumber = 2 }
                ]
            }
        };

        // Case 1: Sonarr returns null (HTTP error / 500)
        mockSonarr.Setup(s => s.GetEpisodesAsync(40, It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<SonarrEpisode>?)null);

        // Jellyfin finds old series entity
        mockLocal.Setup(m => m.FindLocalMatch(userId, MediaKind.Series, request.Title, request.Year, request.ProviderIds))
            .Returns(existingSeriesId);
        // But episode availability check in Jellyfin is false
        mockLocal.Setup(m => m.AreEpisodesAvailable(userId, existingSeriesId, It.IsAny<IReadOnlyList<EpisodeTarget>>()))
            .Returns(false);

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

        await tracker.ProcessRequestAsync(request);

        // Must remain in Importing
        Assert.Equal(RequestStatus.Importing, request.Status);
        Assert.Null(request.CompletedAt);

        // Case 2: Sonarr returns partial (only 601 has file, 602 does not)
        mockSonarr.Setup(s => s.GetEpisodesAsync(40, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrEpisode { Id = 601, SeasonNumber = 6, EpisodeNumber = 1, HasFile = true },
                new SonarrEpisode { Id = 602, SeasonNumber = 6, EpisodeNumber = 2, HasFile = false }
            ]);

        await tracker.ProcessRequestAsync(request);
        Assert.Equal(RequestStatus.Importing, request.Status);
        Assert.Null(request.CompletedAt);

        // Case 3: Both imported in Sonarr, but episodes not yet indexed in Jellyfin
        mockSonarr.Setup(s => s.GetEpisodesAsync(40, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrEpisode { Id = 601, SeasonNumber = 6, EpisodeNumber = 1, HasFile = true },
                new SonarrEpisode { Id = 602, SeasonNumber = 6, EpisodeNumber = 2, HasFile = true }
            ]);

        await tracker.ProcessRequestAsync(request);
        Assert.Equal(RequestStatus.Importing, request.Status);
        Assert.Null(request.CompletedAt);

        // Case 4: Episodes finally confirmed present in Jellyfin!
        mockLocal.Setup(m => m.AreEpisodesAvailable(userId, existingSeriesId, It.IsAny<IReadOnlyList<EpisodeTarget>>()))
            .Returns(true);

        await tracker.ProcessRequestAsync(request);
        Assert.Equal(RequestStatus.Available, request.Status);
        Assert.NotNull(request.CompletedAt);
        Assert.Equal(existingSeriesId, request.LocalItemId);
    }

    [Fact]
    public async Task N03_StateTransitions_RejectInvalidActions_WithConflict()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockTracker = new Mock<ITrackerService>();
        var mockUserManager = new Mock<IUserManager>();
        var mockPaths = new Mock<IApplicationPaths>();
        var mockLogger = new Mock<ILogger<AcquisitionController>>();

        var userId = Guid.NewGuid();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("test", "test", "test");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        // Semaphore releaser dummy
        mockTracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        mockTracker.Setup(t => t.RetryRequestAsync(It.IsAny<string>(), It.IsAny<Func<AcquisitionRequest, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string reqId, Func<AcquisitionRequest, bool> canManage, CancellationToken ct) =>
            {
                var r = mockRepo.Object.GetByIdAsync(reqId, ct).Result;
                if (r == null) return new RetryResult(RetryOutcome.NotFound, null);
                if (!canManage(r)) return new RetryResult(RetryOutcome.Forbidden, r);
                if (r.Status is not (RequestStatus.RequiresAttention or RequestStatus.Failed))
                    return new RetryResult(RetryOutcome.InvalidStatus, r, "Only requests requiring attention or failed can be retried.");
                return new RetryResult(RetryOutcome.Success, r);
            });

        var mockCatalog = new Mock<ICatalogService>();
        var controller = new AcquisitionController(
            mockCatalog.Object,
            mockRepo.Object,
            mockTracker.Object,
            mockRelease.Object,
            mockSonarr.Object,
            mockUserManager.Object,
            mockLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
            }
        };

        // 1. Trying to ConfirmScope on a request that is already Downloading -> 409 Conflict
        var downloadingReq = new AcquisitionRequest
        {
            Id = "req-downloading",
            UserId = userId,
            Status = RequestStatus.Downloading,
            TorrentHash = "hash123"
        };
        mockRepo.Setup(r => r.GetByIdAsync("req-downloading", It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadingReq);

        var confirmResult = await controller.ConfirmScope("req-downloading", new ScopeConfirmationDto { All = true }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(confirmResult.Result);

        // 2. Trying to Retry a request that is actively Downloading -> 409 Conflict
        var retryResult = await controller.RetryRequest("req-downloading", CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(retryResult.Result);

        // 3. Trying to SelectRelease on an already Available request -> 409 Conflict
        var availableReq = new AcquisitionRequest
        {
            Id = "req-avail",
            UserId = userId,
            Status = RequestStatus.Available
        };
        mockRepo.Setup(r => r.GetByIdAsync("req-avail", It.IsAny<CancellationToken>()))
            .ReturnsAsync(availableReq);

        var selectResult = await controller.SelectRelease("req-avail", new SelectReleaseDto { ReleaseId = "rel-1" }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(selectResult.Result);
    }

    [Fact]
    public async Task N04_Prowlarr_RedirectToDifferentPortOrHost_StripsApiKey()
    {
        var requests = new List<HttpRequestMessage>();

        var handler = new MockHandler((req, ct) =>
        {
            requests.Add(req);
            if (req.RequestUri!.Port == 9696)
            {
                // Redirect to same host, different port (9697)
                var redirectResp = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirectResp.Headers.Location = new Uri("http://localhost:9697/torrent/download");
                return Task.FromResult(redirectResp);
            }
            else
            {
                var okResp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x64, 0x38, 0x3a, 0x61])
                };
                return Task.FromResult(okResp);
            }
        });

        var httpClient = new HttpClient(handler);
        var mockLogger = new Mock<ILogger<ProwlarrClient>>();
        var client = new ProwlarrClient(httpClient, mockLogger.Object);

        var result = await client.FetchTorrentAsync("http://localhost:9696/download");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests.Count);

        // First request to port 9696 can have api key
        // Second request to port 9697 must NOT have X-Api-Key!
        Assert.Equal(9697, requests[1].RequestUri?.Port);
        Assert.False(requests[1].Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task N05_ManualSelection_RejectsIncompatibleIdentityOrYear_WithBadRequest()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockTracker = new Mock<ITrackerService>();
        var mockUserManager = new Mock<IUserManager>();
        var mockPaths = new Mock<IApplicationPaths>();
        var mockLogger = new Mock<ILogger<AcquisitionController>>();

        var userId = Guid.NewGuid();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("test", "test", "test");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);
        mockTracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var mockCatalog = new Mock<ICatalogService>();
        var controller = new AcquisitionController(
            mockCatalog.Object,
            mockRepo.Object,
            mockTracker.Object,
            mockRelease.Object,
            mockSonarr.Object,
            mockUserManager.Object,
            mockLogger.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
            }
        };

        var request = new AcquisitionRequest
        {
            Id = "req-movie-manual",
            UserId = userId,
            Title = "Dune: Part Two",
            Year = 2024,
            Kind = MediaKind.Movie,
            Status = RequestStatus.Searching,
            AvailableReleases =
            [
                new ReleaseCandidate
                {
                    Id = "rel-wrong-year",
                    Title = "Dune.1984.1080p.BluRay",
                    IsTorrent = true,
                    RejectionReasons = ["Year mismatch (found 1984, expected 2024)"]
                }
            ]
        };

        mockRepo.Setup(r => r.GetByIdAsync("req-movie-manual", It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);

        // Attempting to select a release with Year mismatch via API -> BadRequest (400)
        var result = await controller.SelectRelease("req-movie-manual", new SelectReleaseDto { ReleaseId = "rel-wrong-year" }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task N06_ArrImportsAndTorrentRemoved_TrackerReconcilesViaHistory()
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
            Id = "req-reconcile",
            Title = "Severance",
            Kind = MediaKind.Series,
            ArrId = 15,
            Status = RequestStatus.Downloading,
            TorrentHash = "hash-completed-999",
            CorrelationTag = "acq-req-reconcile",
            Units =
            [
                new DownloadUnit
                {
                    SeasonNumber = 1,
                    EpisodeNumbers = [1],
                    EpisodeIds = [101],
                    Status = RequestStatus.Downloading,
                    TorrentHash = "hash-completed-999"
                }
            ]
        };

        // qBittorrent returns nothing (torrent was removed after seeding goal met)
        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Sonarr queue returns empty
        mockSonarr.Setup(s => s.GetQueueAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        // Sonarr history shows it was downloaded and imported
        mockSonarr.Setup(s => s.GetHistoryAsync("hash-completed-999", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrHistoryItem
                {
                    EventType = "downloadFolderImported",
                    DownloadId = "hash-completed-999",
                    SeriesId = 15
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

        // Tracker should reconcile and transition to Importing
        Assert.Equal(RequestStatus.Importing, req.Status);
        Assert.Equal(RequestStatus.Importing, req.Units[0].Status);
    }

    [Fact]
    public async Task SchemaMigration_V1ToV2_PreservesIdentitiesAndCreatesV1Backup()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Migration_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");
        var v1BakFile = Path.Combine(testDir, "Acquisition", "requests.json.v1.bak");

        // Schema 1 JSON
        var v1Json = """
        {
            "schemaVersion": 1,
            "requests": [
                {
                    "id": "mig-1",
                    "title": "Existing Series",
                    "kind": 1,
                    "status": 2,
                    "torrentHash": "hash-v1-series",
                    "scope": {
                        "all": false,
                        "seasonNumbers": [1],
                        "episodeNumbers": [1, 2],
                        "targetEpisodes": [
                            { "episodeId": 101, "seasonNumber": 1, "episodeNumber": 1 },
                            { "episodeId": 102, "seasonNumber": 1, "episodeNumber": 2 }
                        ]
                    }
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v1Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];
        Assert.Equal("Existing Series", req.Title);
        Assert.NotEmpty(req.Units); // Units should be migrated
        Assert.Equal(1, req.Units[0].SeasonNumber);

        // requests.json.v1.bak must have been created
        Assert.True(File.Exists(v1BakFile));

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public async Task H01_RealLock_RetryAuthorizedRequest_CompletesWithoutDeadlock()
    {
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockMatcher = new Mock<ILocalMatcherService>();
        var mockLibrary = new Mock<ILibraryManager>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var userId = Guid.NewGuid();
        var request = new AcquisitionRequest
        {
            Id = "req-h01",
            UserId = userId,
            Title = "Test Show",
            Status = RequestStatus.RequiresAttention,
            AttentionReason = "Indexer unreachable",
            ErrorMessage = "Timeout error"
        };

        var requestsStore = new Dictionary<string, AcquisitionRequest> { [request.Id] = request };
        var mockRepo = new Mock<IRequestRepository>();
        mockRepo.Setup(r => r.GetByIdAsync(request.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => requestsStore.TryGetValue(request.Id, out var req) ? req : null);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionRequest, CancellationToken>((r, _) => requestsStore[r.Id] = r)
            .Returns(Task.CompletedTask);

        var realTracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            mockRadarr.Object,
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            mockMatcher.Object,
            mockLibrary.Object,
            mockLogger.Object);

        var mockUserManager = new Mock<IUserManager>();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("test", "test", "test");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            mockRepo.Object,
            realTracker,
            mockRelease.Object,
            mockSonarr.Object,
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
            }
        };

        // Calling retry with real semaphore locks MUST complete without deadlocking
        var retryTask = controller.RetryRequest("req-h01", CancellationToken.None);
        var completedTask = await Task.WhenAny(retryTask, Task.Delay(3000));
        Assert.Same(retryTask, completedTask);

        var actionResult = await retryTask;
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<AcquisitionRequestDto>(okResult.Value);

        Assert.Equal(RequestStatus.Pending, dto.Status);
        Assert.Null(dto.AttentionReason);
        Assert.Null(dto.ErrorMessage);
        Assert.Equal(RequestStatus.Pending, requestsStore["req-h01"].Status);
    }

    [Fact]
    public async Task H02_Migration_MultiSeason_DoesNotDuplicateTorrentAcrossUnits()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Migration_MultiSeason_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");

        // Schema 1 JSON with two seasons and an active Season 1 download
        var v1Json = """
        {
            "schemaVersion": 1,
            "requests": [
                {
                    "id": "mig-multi-1",
                    "title": "Two Seasons Show",
                    "kind": 1,
                    "status": 2,
                    "torrentHash": "hash-season-1",
                    "torrentName": "Show.S01.1080p",
                    "correlationTag": "acq-mig-multi-1",
                    "selectedRelease": {
                        "id": "rel-s01",
                        "title": "Show.S01.1080p",
                        "infoHash": "hash-season-1",
                        "seasonNumber": 1,
                        "isSeasonPack": true,
                        "isTorrent": true
                    },
                    "scope": {
                        "all": false,
                        "seasonNumbers": [1, 2],
                        "episodeNumbers": [],
                        "targetEpisodes": [
                            { "episodeId": 101, "seasonNumber": 1, "episodeNumber": 1 },
                            { "episodeId": 102, "seasonNumber": 1, "episodeNumber": 2 },
                            { "episodeId": 201, "seasonNumber": 2, "episodeNumber": 1 },
                            { "episodeId": 202, "seasonNumber": 2, "episodeNumber": 2 }
                        ]
                    }
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v1Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];
        Assert.Equal(2, req.Units.Count);

        // Unit 1 (Season 1) inherits the active torrent and correlation tag
        var u1 = req.Units.First(u => u.SeasonNumber == 1);
        Assert.Equal(RequestStatus.Downloading, u1.Status);
        Assert.Equal("hash-season-1", u1.TorrentHash);
        Assert.NotNull(u1.SelectedRelease);
        Assert.Equal("acq-mig-multi-1", u1.CorrelationTag);

        // Unit 2 (Season 2) MUST NOT inherit the S01 torrent or release
        var u2 = req.Units.First(u => u.SeasonNumber == 2);
        Assert.Equal(RequestStatus.Pending, u2.Status);
        Assert.Null(u2.TorrentHash);
        Assert.Null(u2.SelectedRelease);

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public async Task H03_FastExitAll_DoesNotDeclareAvailableWithoutJellyfin()
    {
        var mockSonarr = new Mock<ISonarrClient>();
        var mockMatcher = new Mock<ILocalMatcherService>();
        var mockRepo = new Mock<IRequestRepository>();

        var req = new AcquisitionRequest
        {
            Id = "req-h03",
            Kind = MediaKind.Series,
            ArrId = 44,
            Status = RequestStatus.Pending,
            Scope = new RequestScope { All = true }
        };

        mockRepo.Setup(r => r.GetByIdAsync(req.Id, It.IsAny<CancellationToken>())).ReturnsAsync(req);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            Mock.Of<IRadarrClient>(),
            Mock.Of<IProwlarrClient>(),
            Mock.Of<IQBittorrentClient>(),
            Mock.Of<IReleaseService>(),
            mockMatcher.Object,
            Mock.Of<ILibraryManager>(),
            Mock.Of<ILogger<TrackerService>>());

        // Case 1: Sonarr returns [] because catalog is still populating
        mockSonarr.Setup(s => s.GetEpisodesAsync(44, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await tracker.ProcessRequestAsync(req);
        Assert.Equal(RequestStatus.Pending, req.Status); // Must wait, cannot declare Available!

        // Case 2: All aired episodes have files in Sonarr, but NOT indexed in Jellyfin
        var airedEpisodes = new List<SonarrEpisode>
        {
            new() { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, HasFile = true, AirDateUtc = DateTime.UtcNow.AddDays(-10) },
            new() { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, HasFile = true, AirDateUtc = DateTime.UtcNow.AddDays(-5) }
        };
        mockSonarr.Setup(s => s.GetEpisodesAsync(44, It.IsAny<CancellationToken>()))
            .ReturnsAsync(airedEpisodes);

        mockMatcher.Setup(m => m.FindLocalMatch(It.IsAny<Guid>(), MediaKind.Series, It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<ProviderIds>()))
            .Returns((Guid?)null); // Jellyfin has not detected files yet

        await tracker.ProcessRequestAsync(req);
        Assert.Equal(RequestStatus.Importing, req.Status); // Distinguishes Arr import from Jellyfin availability
        Assert.Null(req.CompletedAt);
        Assert.Null(req.LocalItemId);

        // Case 3: Jellyfin confirms files are present and accessible
        var localId = Guid.NewGuid();
        mockMatcher.Setup(m => m.FindLocalMatch(It.IsAny<Guid>(), MediaKind.Series, It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<ProviderIds>()))
            .Returns(localId);
        mockMatcher.Setup(m => m.AreEpisodesAvailable(It.IsAny<Guid>(), localId, It.IsAny<List<EpisodeTarget>>()))
            .Returns(true);

        await tracker.ProcessRequestAsync(req);
        Assert.Equal(RequestStatus.Available, req.Status);
        Assert.NotNull(req.CompletedAt);
        Assert.Equal(localId, req.LocalItemId);
    }

    [Fact]
    public async Task H04_AiringSeason_OrNoPack_SplitsIntoEpisodeUnits()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRelease = new Mock<IReleaseService>();
        var mockQb = new Mock<IQBittorrentClient>();

        var req = new AcquisitionRequest
        {
            Id = "req-h04",
            Kind = MediaKind.Series,
            Category = "tv-sonarr",
            ArrId = 55,
            Status = RequestStatus.Searching,
            Scope = new RequestScope
            {
                All = false,
                SeasonNumbers = [2],
                TargetEpisodes = [
                    new EpisodeTarget { EpisodeId = 201, SeasonNumber = 2, EpisodeNumber = 1 },
                    new EpisodeTarget { EpisodeId = 202, SeasonNumber = 2, EpisodeNumber = 2 }
                ]
            },
            Units = [
                new DownloadUnit
                {
                    SeasonNumber = 2,
                    EpisodeNumbers = [1, 2],
                    EpisodeIds = [201, 202],
                    IsFullSeasonPack = true,
                    Status = RequestStatus.Pending
                }
            ]
        };

        mockRepo.Setup(r => r.GetByIdAsync(req.Id, It.IsAny<CancellationToken>())).ReturnsAsync(req);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Searching for Season 2 pack returns no pack releases (only individual episodes exist)
        mockRelease.Setup(r => r.SearchAndEvaluateReleasesForUnitAsync(req, It.Is<DownloadUnit>(u => u.IsFullSeasonPack), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockRelease.Setup(r => r.PickBestRelease(It.Is<IEnumerable<ReleaseCandidate>>(c => !c.Any())))
            .Returns((ReleaseCandidate?)null);

        // Searching for individual episode 1 returns a valid release
        var ep1Candidate = new ReleaseCandidate
        {
            Id = "rel-ep1",
            Title = "Show.S02E01.1080p",
            InfoHash = "hash-ep1",
            SeasonNumber = 2,
            EpisodeNumber = 1,
            IsTorrent = true,
            IsEligible = true,
            IdentityVerified = true
        };
        mockRelease.Setup(r => r.SearchAndEvaluateReleasesForUnitAsync(req, It.Is<DownloadUnit>(u => !u.IsFullSeasonPack && u.EpisodeNumbers.Contains(1)), It.IsAny<CancellationToken>()))
            .ReturnsAsync([ep1Candidate]);
        mockRelease.Setup(r => r.PickBestRelease(It.Is<IEnumerable<ReleaseCandidate>>(c => c.Any(x => x.Id == "rel-ep1"))))
            .Returns(ep1Candidate);

        mockProwlarr.Setup(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { TorrentBytes = [1, 2, 3], InfoHash = "hash-ep1" });
        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockQb.Setup(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            Mock.Of<IRadarrClient>(),
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            Mock.Of<ILocalMatcherService>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<ILogger<TrackerService>>());

        await tracker.ProcessRequestAsync(req);

        // Tracker should have split the season pack into individual episode units and started downloading E01
        Assert.Equal(2, req.Units.Count);
        Assert.All(req.Units, u => Assert.False(u.IsFullSeasonPack));

        var u1 = req.Units[0];
        Assert.Equal(1, u1.EpisodeNumbers[0]);
        Assert.Equal(RequestStatus.Downloading, u1.Status);
        Assert.Equal("hash-ep1", u1.TorrentHash);

        var u2 = req.Units[1];
        Assert.Equal(2, u2.EpisodeNumbers[0]);
        Assert.Equal(RequestStatus.Pending, u2.Status);
        Assert.Null(u2.TorrentHash);
    }

    [Fact]
    public async Task H05_QueueBlockedDiagnostics_CommunicatedEvenWhenTorrentSeeding()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();

        var req = new AcquisitionRequest
        {
            Id = "req-h05",
            Kind = MediaKind.Series,
            ArrId = 66,
            Status = RequestStatus.Downloading,
            TorrentHash = "hash-seeding-100",
            CorrelationTag = "acq-req-h05"
        };

        mockRepo.Setup(r => r.GetByIdAsync(req.Id, It.IsAny<CancellationToken>())).ReturnsAsync(req);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Torrent is 100% completed and present in qBittorrent (seeding)
        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QBittorrentTorrentInfo
                {
                    Hash = "hash-seeding-100",
                    Name = "Show.S01E01.1080p",
                    Progress = 1.0,
                    State = "uploading"
                }
            ]);

        // Sonarr CDH import is blocked due to path permissions
        var blockedQueue = new List<SonarrQueueItem>
        {
            new()
            {
                DownloadId = "hash-seeding-100",
                SeriesId = 66,
                TrackedDownloadStatus = "error",
                TrackedDownloadState = "importBlocked",
                StatusMessages = [
                    new SonarrStatusMessage
                    {
                        Title = "Show.S01E01.1080p",
                        Messages = ["Import failed: Access to destination path /media/tv is denied."]
                    }
                ]
            }
        };
        mockSonarr.Setup(s => s.GetQueueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(blockedQueue);

        var tracker = new TrackerService(
            mockRepo.Object,
            mockSonarr.Object,
            Mock.Of<IRadarrClient>(),
            Mock.Of<IProwlarrClient>(),
            mockQb.Object,
            Mock.Of<IReleaseService>(),
            Mock.Of<ILocalMatcherService>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<ILogger<TrackerService>>());

        await tracker.ProcessRequestAsync(req);

        // Tracker must NOT ignore the queue error just because the torrent exists in qB; it must flag RequiresAttention
        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Contains("Access to destination path /media/tv is denied", req.AttentionReason);
    }

    [Fact]
    public async Task H06_StaleReleaseCandidatesFromPreviousUnit_RejectedOnNextUnit()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockTracker = new Mock<ITrackerService>();
        var mockUserManager = new Mock<IUserManager>();

        var userId = Guid.NewGuid();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("test", "test", "test");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        mockTracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var u1 = new DownloadUnit { Id = "u1", SeasonNumber = 1, Status = RequestStatus.Available };
        var u2 = new DownloadUnit { Id = "u2", SeasonNumber = 2, Status = RequestStatus.Pending };

        var staleS01Release = new ReleaseCandidate
        {
            Id = "rel-stale-s01",
            Title = "Show.S01.Complete.1080p",
            SeasonNumber = 1,
            IsSeasonPack = true,
            IsTorrent = true,
            IsEligible = true,
            IdentityVerified = true
        };

        var req = new AcquisitionRequest
        {
            Id = "req-h06",
            UserId = userId,
            Status = RequestStatus.Searching,
            AvailableReleasesUnitId = "u1", // bound to u1
            AvailableReleases = [staleS01Release],
            Units = [u1, u2]
        };

        mockRepo.Setup(r => r.GetByIdAsync("req-h06", It.IsAny<CancellationToken>()))
            .ReturnsAsync(req);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            mockRepo.Object,
            mockTracker.Object,
            Mock.Of<IReleaseService>(),
            Mock.Of<ISonarrClient>(),
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
            }
        };

        // Selecting a stale release bound to u1 when active unit is now u2 must be rejected with 409 Conflict
        var selectResult = await controller.SelectRelease("req-h06", new SelectReleaseDto { ReleaseId = "rel-stale-s01" }, CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(selectResult.Result);
        Assert.Contains("descarga anterior", Assert.IsType<ErrorResponseDto>(conflict.Value).Message);
    }

    [Fact]
    public async Task H07_SeriesTitleMismatch_DerivativeAndRemake_RejectedWhenParserHasNoMatch()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        var request = new AcquisitionRequest
        {
            Title = "Dexter",
            Year = 2006,
            Kind = MediaKind.Series,
            Scope = new RequestScope { All = false, SeasonNumbers = [1], EpisodeNumbers = [1] }
        };

        var releases = new List<ProwlarrRelease>
        {
            // Derivative / spin-off: has extra subtitle words in title prefix
            new() { Title = "Dexter.New.Blood.S01E01.1080p.WEB-DL", Protocol = "torrent", Seeders = 100 },
            // Remake with different year token
            new() { Title = "Dexter.1986.S01E01.1080p.WEB-DL", Protocol = "torrent", Seeders = 50 },
            // Correct series matching title prefix
            new() { Title = "Dexter.S01E01.1080p.WEB-DL", Protocol = "torrent", Seeders = 40 }
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);

        // Sonarr parse returns NO association (fallback branch triggered)
        mockSonarr.Setup(s => s.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SonarrParseResult?)null);

        var releaseService = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);
        var results = await releaseService.SearchAndEvaluateReleasesAsync(request);

        Assert.Equal(3, results.Count);

        // 1. Derivative must be rejected for title mismatch
        var spinOff = results.First(r => r.Title.Contains("New.Blood"));
        Assert.False(spinOff.IsEligible);
        Assert.Contains(spinOff.RejectionReasons, r => r.Contains("Title mismatch"));

        // 2. Remake with wrong year must be rejected for year mismatch
        var remake = results.First(r => r.Title.Contains("1986"));
        Assert.False(remake.IsEligible);
        Assert.Contains(remake.RejectionReasons, r => r.Contains("Year mismatch"));

        // 3. Exact series must be eligible and picked
        var correct = results.First(r => r.Title == "Dexter.S01E01.1080p.WEB-DL");
        Assert.True(correct.IsEligible);
        Assert.True(correct.IdentityVerified);

        var best = releaseService.PickBestRelease(results);
        Assert.NotNull(best);
        Assert.Equal("Dexter.S01E01.1080p.WEB-DL", best.Title);
    }

    [Fact]
    public async Task J01_ScenarioA_ContinuationAfterMigratingU1_U2SendsOwnRelease_NeverAdoptsU1Torrent()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockRelease = new Mock<IReleaseService>();

        var u1 = new DownloadUnit
        {
            Id = "u1",
            SeasonNumber = 1,
            Status = RequestStatus.Available,
            TorrentHash = "hash-u1-legacy",
            CorrelationTag = "acq-req-j01" // legacy tag inherited on U1
        };

        var u2 = new DownloadUnit
        {
            Id = "u2",
            SeasonNumber = 2,
            EpisodeNumbers = [1],
            Status = RequestStatus.Searching,
            CorrelationTag = "acq-req-j01-u2"
        };

        var u2Release = new ReleaseCandidate
        {
            Id = "rel-u2",
            Title = "Show.S02E01.1080p",
            InfoHash = "hash-u2-new",
            SeasonNumber = 2,
            EpisodeNumber = 1,
            IsTorrent = true,
            IsEligible = true,
            IdentityVerified = true
        };

        var req = new AcquisitionRequest
        {
            Category = "tv-sonarr",
            Id = "req-j01",
            Kind = MediaKind.Series,
            Status = RequestStatus.Searching,
            Units = [u1, u2],
            SelectedRelease = u2Release,
            CorrelationTag = "acq-req-j01-u2"
        };
        u2.SelectedRelease = u2Release;

        mockRepo.Setup(r => r.GetByIdAsync(req.Id, It.IsAny<CancellationToken>())).ReturnsAsync(req);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // qBittorrent has U1 still seeding under the legacy tag "acq-req-j01"
        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), "acq-req-j01", (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QBittorrentTorrentInfo { Hash = "hash-u1-legacy", Name = "Show.S01.Complete", State = "uploading" }
            ]);

        // qBittorrent has no torrent under U2's tag "acq-req-j01-u2"
        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), "acq-req-j01-u2", (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        mockProwlarr.Setup(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { TorrentBytes = [1, 2, 3], InfoHash = "hash-u2-new" });

        var addedOptions = new List<QBittorrentAddOptions>();
        mockQb.Setup(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>()))
            .Callback<QBittorrentAddOptions, CancellationToken>((opts, _) => addedOptions.Add(opts))
            .ReturnsAsync(true);

        var tracker = new TrackerService(
            mockRepo.Object,
            Mock.Of<ISonarrClient>(),
            Mock.Of<IRadarrClient>(),
            mockProwlarr.Object,
            mockQb.Object,
            mockRelease.Object,
            Mock.Of<ILocalMatcherService>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<ILogger<TrackerService>>());

        await tracker.ProcessRequestAsync(req);

        // U2 must have sent its OWN torrent and never adopted U1's legacy torrent
        Assert.Single(addedOptions);
        Assert.Equal("acq-req-j01-u2", addedOptions[0].Tag);
        Assert.Equal("hash-u2-new", u2.TorrentHash);
        Assert.NotEqual("hash-u1-legacy", u2.TorrentHash);
    }

    [Fact]
    public async Task J01_ScenarioB_AlternativeReleaseSelected_DoesNotAdoptPreviousTorrent()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockTracker = new Mock<ITrackerService>();
        var mockUserManager = new Mock<IUserManager>();

        var userId = Guid.NewGuid();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("test", "test", "test");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        mockTracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var u1 = new DownloadUnit
        {
            Id = "u1",
            SeasonNumber = 1,
            EpisodeNumbers = [1],
            Status = RequestStatus.RequiresAttention,
            TorrentHash = "hash-torrent-A",
            CorrelationTag = "acq-req-alt-u1"
        };

        var alternativeReleaseB = new ReleaseCandidate
        {
            Id = "rel-B",
            Title = "Show.S01E01.Alternative.1080p",
            InfoHash = "hash-release-B",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            IsTorrent = true,
            IsEligible = true,
            IdentityVerified = true
        };

        var req = new AcquisitionRequest
        {
            Id = "req-alt",
            UserId = userId,
            Status = RequestStatus.RequiresAttention,
            TorrentHash = "hash-torrent-A",
            Units = [u1],
            AvailableReleasesUnitId = "u1",
            AvailableReleases = [alternativeReleaseB]
        };

        mockRepo.Setup(r => r.GetByIdAsync("req-alt", It.IsAny<CancellationToken>())).ReturnsAsync(req);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            mockRepo.Object,
            mockTracker.Object,
            Mock.Of<IReleaseService>(),
            Mock.Of<ISonarrClient>(),
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
            }
        };

        // User explicitly selects alternative release B
        var result = await controller.SelectRelease("req-alt", new SelectReleaseDto { ReleaseId = "rel-B" }, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);

        // Previous hash must be invalidated and a fresh correlation tag assigned
        Assert.Null(u1.TorrentHash);
        Assert.Null(req.TorrentHash);
        Assert.NotEqual("acq-req-alt-u1", u1.CorrelationTag);
        Assert.Equal("rel-B", u1.SelectedRelease?.Id);
    }

    [Fact]
    public async Task J02_Migration_ActiveDownloadInSeason2_ProcessesSeason2FirstBeforeSeason1()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Migration_ActiveS02_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");

        // Schema 1 JSON where Season 2 has the active download and Season 1 is pending
        var v1Json = """
        {
            "schemaVersion": 1,
            "requests": [
                {
                    "id": "mig-s02-active",
                    "title": "Two Season Drama",
                    "kind": 1,
                    "status": 2,
                    "torrentHash": "hash-s02-active",
                    "torrentName": "Drama.S02.Complete",
                    "selectedRelease": {
                        "id": "rel-s02",
                        "title": "Drama.S02.Complete",
                        "infoHash": "hash-s02-active",
                        "seasonNumber": 2,
                        "isSeasonPack": true,
                        "isTorrent": true
                    },
                    "scope": {
                        "all": false,
                        "seasonNumbers": [1, 2],
                        "targetEpisodes": [
                            { "episodeId": 101, "seasonNumber": 1, "episodeNumber": 1 },
                            { "episodeId": 201, "seasonNumber": 2, "episodeNumber": 1 }
                        ]
                    }
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v1Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];

        // S01 is Pending, S02 is Downloading
        var u1 = req.Units.First(u => u.SeasonNumber == 1);
        var u2 = req.Units.First(u => u.SeasonNumber == 2);
        Assert.Equal(RequestStatus.Pending, u1.Status);
        Assert.Equal(RequestStatus.Downloading, u2.Status);
        Assert.Equal("hash-s02-active", u2.TorrentHash);

        // GetActiveUnit must return U2 (the downloading unit), NOT U1
        var active = req.GetActiveUnit();
        Assert.NotNull(active);
        Assert.Equal(2, active.SeasonNumber);
        Assert.Equal("hash-s02-active", active.TorrentHash);

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public async Task J02_Migration_UnidentifiableCoverage_DoesNotAssumeFirstSeason_FlagsRequiresAttention()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "Migration_Unidentified_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");

        // Schema 1 JSON with active torrent but no season token in title, release, or name
        var v1Json = """
        {
            "schemaVersion": 1,
            "requests": [
                {
                    "id": "mig-unidentified",
                    "title": "Mystery Show",
                    "kind": 1,
                    "status": 2,
                    "torrentHash": "hash-mystery",
                    "torrentName": "Mystery.Show.Collection.720p",
                    "scope": {
                        "all": false,
                        "seasonNumbers": [1, 2],
                        "targetEpisodes": [
                            { "episodeId": 101, "seasonNumber": 1, "episodeNumber": 1 },
                            { "episodeId": 201, "seasonNumber": 2, "episodeNumber": 1 }
                        ]
                    }
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v1Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];

        // Must not assume Season 1: must set RequiresAttention for manual confirmation
        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Contains("Migración ambigua", req.AttentionReason);
        Assert.All(req.Units, u => Assert.Equal(RequestStatus.Pending, u.Status));
        Assert.All(req.Units, u => Assert.Null(u.TorrentHash));

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public async Task J03_NumericTitleSeries_1923_CanonicalArrMatch_IsEligible()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        var request = new AcquisitionRequest
        {
            Title = "1923",
            Year = 2022,
            ArrId = 77,
            Kind = MediaKind.Series,
            Scope = new RequestScope { All = false, SeasonNumbers = [1], EpisodeNumbers = [1] }
        };

        var releases = new List<ProwlarrRelease>
        {
            new() { Title = "1923.S01E01.1080p.WEB-DL", Protocol = "torrent", Seeders = 100 }
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);

        // Sonarr parse returns positive canonical match for Series ID 77
        mockSonarr.Setup(s => s.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrParseResult
            {
                Series = new SonarrSeries { Id = 77, Title = "1923" }
            });

        var releaseService = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);
        var results = await releaseService.SearchAndEvaluateReleasesAsync(request);

        Assert.Single(results);
        var r = results[0];
        Assert.True(r.IsEligible);
        Assert.True(r.IdentityVerified);
        Assert.DoesNotContain(r.RejectionReasons, reason => reason.Contains("Year mismatch"));
    }

    [Fact]
    public async Task J03_EpisodeAirDateYear_DoesNotConflictWithPremiereYear_WhenArrConfirms()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        var request = new AcquisitionRequest
        {
            Title = "The Daily Show",
            Year = 1996,
            ArrId = 88,
            Kind = MediaKind.Series,
            Scope = new RequestScope { All = false, SeasonNumbers = [2024], EpisodeNumbers = [10] }
        };

        var releases = new List<ProwlarrRelease>
        {
            new() { Title = "The.Daily.Show.2024.10.15.HDTV.x264", Protocol = "torrent", Seeders = 50 }
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);

        // Sonarr parse confirms it is series ID 88
        mockSonarr.Setup(s => s.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrParseResult
            {
                Series = new SonarrSeries { Id = 88, Title = "The Daily Show" }
            });

        var releaseService = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);
        var results = await releaseService.SearchAndEvaluateReleasesAsync(request);

        Assert.Single(results);
        var r = results[0];
        Assert.True(r.IdentityVerified);
        Assert.DoesNotContain(r.RejectionReasons, reason => reason.Contains("Year mismatch"));
    }

    [Fact]
    public async Task K01_ManualSelection_WithProduction32CharId_ProducesUniqueCorrelationTagsAcrossSelections()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockTracker = new Mock<ITrackerService>();
        var mockUserManager = new Mock<IUserManager>();

        var userId = Guid.NewGuid();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("test", "test", "test");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        mockTracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var prodRequestId = Guid.NewGuid().ToString("N"); // 32-character production ID
        var unitId = "unit-101";

        var unit = new DownloadUnit
        {
            Id = unitId,
            SeasonNumber = 1,
            EpisodeNumbers = [1],
            Status = RequestStatus.Searching
        };

        var releaseA = new ReleaseCandidate
        {
            Id = "rel-A",
            Title = "Show.S01E01.A",
            InfoHash = "hash-A",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            IsTorrent = true,
            IsEligible = true,
            IdentityVerified = true
        };

        var releaseB = new ReleaseCandidate
        {
            Id = "rel-B",
            Title = "Show.S01E01.B",
            InfoHash = "hash-B",
            SeasonNumber = 1,
            EpisodeNumber = 1,
            IsTorrent = true,
            IsEligible = true,
            IdentityVerified = true
        };

        var req = new AcquisitionRequest
        {
            Id = prodRequestId,
            UserId = userId,
            Status = RequestStatus.Searching,
            Units = [unit],
            AvailableReleasesUnitId = unitId,
            AvailableReleases = [releaseA, releaseB]
        };

        mockRepo.Setup(r => r.GetByIdAsync(prodRequestId, It.IsAny<CancellationToken>())).ReturnsAsync(req);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            mockRepo.Object,
            mockTracker.Object,
            Mock.Of<IReleaseService>(),
            Mock.Of<ISonarrClient>(),
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
            }
        };

        // First manual selection
        var resultA = await controller.SelectRelease(prodRequestId, new SelectReleaseDto { ReleaseId = "rel-A" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(resultA.Result);
        var tag1 = unit.CorrelationTag;
        Assert.NotNull(tag1);
        Assert.StartsWith($"acq-{prodRequestId}-{unitId}-", tag1);

        // Put request into requires attention to simulate subsequent manual selection after failure
        req.Status = RequestStatus.RequiresAttention;
        unit.Status = RequestStatus.RequiresAttention;

        // Second manual selection
        var resultB = await controller.SelectRelease(prodRequestId, new SelectReleaseDto { ReleaseId = "rel-B" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(resultB.Result);
        var tag2 = unit.CorrelationTag;
        Assert.NotNull(tag2);
        Assert.StartsWith($"acq-{prodRequestId}-{unitId}-", tag2);

        // Crucial invariant: tag1 and tag2 must not collide despite 32-char ID
        Assert.NotEqual(tag1, tag2);
    }

    [Fact]
    public async Task K01_TrackerService_DownloadingWithoutHash_DoesNotBlindlyAdoptWrongTorrentUnderSharedTag()
    {
        var mockRepo = new Mock<IRequestRepository>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockLogger = new Mock<ILogger<TrackerService>>();

        var sharedTag = "acq-shared-tag";

        var unit = new DownloadUnit
        {
            Id = "u1",
            SeasonNumber = 1,
            EpisodeNumbers = [1],
            Status = RequestStatus.Downloading,
            CorrelationTag = sharedTag,
            TorrentHash = null, // Torrent bytes fetched without hash
            TorrentName = "Show.S01E01.TargetRelease"
        };

        var req = new AcquisitionRequest
        {
            Id = "req-k01",
            Kind = MediaKind.Series,
            Status = RequestStatus.Downloading,
            Units = [unit],
            CorrelationTag = sharedTag,
            TorrentHash = null,
            TorrentName = "Show.S01E01.TargetRelease"
        };

        mockRepo.Setup(r => r.GetByIdAsync(req.Id, It.IsAny<CancellationToken>())).ReturnsAsync(req);
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // qBittorrent returns two torrents under this tag: Torrent A (older attempt) and Torrent B (unrelated)
        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), sharedTag, (string?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QBittorrentTorrentInfo { Hash = "hash-torrent-A", Name = "Show.S01E01.OlderAttempt", State = "downloading" },
                new QBittorrentTorrentInfo { Hash = "hash-torrent-Other", Name = "Completely.Different", State = "downloading" }
            ]);

        var tracker = new TrackerService(
            mockRepo.Object,
            Mock.Of<ISonarrClient>(),
            Mock.Of<IRadarrClient>(),
            mockProwlarr.Object,
            mockQb.Object,
            Mock.Of<IReleaseService>(),
            Mock.Of<ILocalMatcherService>(),
            Mock.Of<ILibraryManager>(),
            mockLogger.Object);

        await tracker.ProcessRequestAsync(req);

        // Must NOT adopt Torrent A's hash since its name does not match TargetRelease!
        Assert.Null(req.TorrentHash);
        Assert.Null(unit.TorrentHash);
    }

    [Fact]
    public async Task K02_Schema2Migration_DistinctValidHashes_PreservesBothAssociations()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "K02_Distinct_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");

        // Schema 2 JSON with S01 Available (hash-A) and S02 Downloading (hash-B)
        var v2Json = """
        {
            "schemaVersion": 2,
            "requests": [
                {
                    "id": "mig-k02-distinct",
                    "title": "Drama Show",
                    "kind": 1,
                    "status": 2,
                    "correlationTag": "acq-mig-k02-distinct",
                    "units": [
                        {
                            "id": "u1",
                            "seasonNumber": 1,
                            "episodeNumbers": [1, 2],
                            "status": 4,
                            "torrentHash": "hash-A",
                            "torrentName": "Drama.S01.Complete",
                            "correlationTag": "acq-u1",
                            "selectedRelease": {
                                "id": "rel-1",
                                "title": "Drama.S01.Complete",
                                "infoHash": "hash-A",
                                "seasonNumber": 1
                            }
                        },
                        {
                            "id": "u2",
                            "seasonNumber": 2,
                            "episodeNumbers": [1, 2],
                            "status": 2,
                            "torrentHash": "hash-B",
                            "torrentName": "Drama.S02.Complete",
                            "correlationTag": "acq-u2",
                            "selectedRelease": {
                                "id": "rel-2",
                                "title": "Drama.S02.Complete",
                                "infoHash": "hash-B",
                                "seasonNumber": 2
                            }
                        }
                    ]
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v2Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];
        Assert.Equal(2, req.Units.Count);

        var u1 = req.Units.First(u => u.SeasonNumber == 1);
        var u2 = req.Units.First(u => u.SeasonNumber == 2);

        // S01 must retain its legitimate completed association (hash-A, correlation tag, release)
        Assert.Equal(RequestStatus.Available, u1.Status);
        Assert.Equal("hash-A", u1.TorrentHash);
        Assert.Equal("acq-u1", u1.CorrelationTag);
        Assert.NotNull(u1.SelectedRelease);

        // S02 must retain its active downloading association (hash-B)
        Assert.Equal(RequestStatus.Downloading, u2.Status);
        Assert.Equal("hash-B", u2.TorrentHash);
        Assert.Equal("acq-u2", u2.CorrelationTag);

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public async Task K02_Schema2Migration_DuplicateHash_SingleActiveUnit_ClearsPendingDuplicate()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "K02_SingleActive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");

        // Schema 2 JSON where S01 (Pending) and S02 (Downloading) share the same duplicate hash-B
        var v2Json = """
        {
            "schemaVersion": 2,
            "requests": [
                {
                    "id": "mig-k02-single-active",
                    "title": "Drama Show",
                    "kind": 1,
                    "status": 2,
                    "units": [
                        {
                            "id": "u1",
                            "seasonNumber": 1,
                            "episodeNumbers": [1, 2],
                            "status": 0,
                            "torrentHash": "hash-B",
                            "torrentName": "Drama.S02.Complete",
                            "correlationTag": "acq-u1"
                        },
                        {
                            "id": "u2",
                            "seasonNumber": 2,
                            "episodeNumbers": [1, 2],
                            "status": 2,
                            "torrentHash": "hash-B",
                            "torrentName": "Drama.S02.Complete",
                            "correlationTag": "acq-u2"
                        }
                    ]
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v2Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];

        var u1 = req.Units.First(u => u.SeasonNumber == 1);
        var u2 = req.Units.First(u => u.SeasonNumber == 2);

        // S02 was the active downloading unit: it retains hash-B
        Assert.Equal(RequestStatus.Downloading, u2.Status);
        Assert.Equal("hash-B", u2.TorrentHash);

        // S01 was pending: duplicate association is cleared
        Assert.Equal(RequestStatus.Pending, u1.Status);
        Assert.Null(u1.TorrentHash);
        Assert.Null(u1.CorrelationTag);

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public async Task K02_Schema2Migration_DuplicateHash_TwoActiveUnits_ResolvedByCoverageEvidence()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "K02_CoverageEvidence_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");

        // Schema 2 JSON where BOTH S01 and S02 are downloading with hash-B,
        // but the release explicitly indicates Season 2 coverage
        var v2Json = """
        {
            "schemaVersion": 2,
            "requests": [
                {
                    "id": "mig-k02-coverage",
                    "title": "Drama Show",
                    "kind": 1,
                    "status": 2,
                    "selectedRelease": {
                        "id": "rel-s02",
                        "title": "Drama.S02.Complete.1080p",
                        "infoHash": "hash-B",
                        "seasonNumber": 2,
                        "isSeasonPack": true
                    },
                    "units": [
                        {
                            "id": "u1",
                            "seasonNumber": 1,
                            "episodeNumbers": [1, 2],
                            "status": 2,
                            "torrentHash": "hash-B",
                            "torrentName": "Drama.S02.Complete.1080p",
                            "correlationTag": "acq-u1"
                        },
                        {
                            "id": "u2",
                            "seasonNumber": 2,
                            "episodeNumbers": [1, 2],
                            "status": 2,
                            "torrentHash": "hash-B",
                            "torrentName": "Drama.S02.Complete.1080p",
                            "correlationTag": "acq-u2",
                            "selectedRelease": {
                                "id": "rel-s02",
                                "title": "Drama.S02.Complete.1080p",
                                "infoHash": "hash-B",
                                "seasonNumber": 2,
                                "isSeasonPack": true
                            }
                        }
                    ]
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v2Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];

        var u1 = req.Units.First(u => u.SeasonNumber == 1);
        var u2 = req.Units.First(u => u.SeasonNumber == 2);

        // S02 matches coverage: retains hash-B and Downloading status
        Assert.Equal(RequestStatus.Downloading, u2.Status);
        Assert.Equal("hash-B", u2.TorrentHash);

        // S01 does not match coverage: cleared and reset to Pending
        Assert.Equal(RequestStatus.Pending, u1.Status);
        Assert.Null(u1.TorrentHash);
        Assert.Null(u1.CorrelationTag);

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public async Task K02_Schema2Migration_DuplicateHash_TwoActiveUnits_AmbiguousCoverage_RequiresAttention()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "K02_Ambiguous_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDir, "Acquisition"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(testDir);
        var mockLogger = new Mock<ILogger<JsonFileRequestRepository>>();

        var primaryFile = Path.Combine(testDir, "Acquisition", "requests.json");

        // Schema 2 JSON where BOTH S01 and S02 are downloading with hash-B,
        // and neither has season coverage evidence
        var v2Json = """
        {
            "schemaVersion": 2,
            "requests": [
                {
                    "id": "mig-k02-ambig",
                    "title": "Drama Show",
                    "kind": 1,
                    "status": 2,
                    "units": [
                        {
                            "id": "u1",
                            "seasonNumber": 1,
                            "episodeNumbers": [1, 2],
                            "status": 2,
                            "torrentHash": "hash-B",
                            "torrentName": "Drama.Complete.Collection",
                            "correlationTag": "acq-u1"
                        },
                        {
                            "id": "u2",
                            "seasonNumber": 2,
                            "episodeNumbers": [1, 2],
                            "status": 2,
                            "torrentHash": "hash-B",
                            "torrentName": "Drama.Complete.Collection",
                            "correlationTag": "acq-u2"
                        }
                    ]
                }
            ]
        }
        """;
        await File.WriteAllTextAsync(primaryFile, v2Json);

        var repo = new JsonFileRequestRepository(mockAppPaths.Object, mockLogger.Object);
        var requests = await repo.GetAllAsync();

        Assert.Single(requests);
        var req = requests[0];

        // Ambiguous without coverage evidence: must require manual attention and NOT arbitrarily wipe
        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Contains("Migración ambigua", req.AttentionReason);

        try { Directory.Delete(testDir, true); } catch { }
    }

    [Fact]
    public void B01_CatalogSearchResultDto_SerializesWithCamelCaseNames_EvenUnderPascalCaseNamingPolicy()
    {
        // Jellyfin's server default options use PascalCase (PropertyNamingPolicy = null).
        // The DTOs must use [JsonPropertyName] so that output JSON is consistently camelCase.
        var pascalOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null // PascalCase policy
        };

        var dto = new CatalogSearchResultDto
        {
            Items =
            [
                new CatalogItemDto
                {
                    Id = "sonarr-10",
                    Kind = MediaKind.Series,
                    Title = "Naruto",
                    Year = 2002,
                    Overview = "Ninja adventure",
                    PosterUrl = "/images/naruto.jpg",
                    ProviderIds = new ProviderIds { Tvdb = "78857" }
                }
            ],
            Sonarr = new ProviderDiagnosticsDto { Status = "Ready", IsConfigured = true },
            Radarr = new ProviderDiagnosticsDto { Status = "Ready", IsConfigured = true },
            IsPartial = false,
            TotalRemoteFound = 1,
            LocalMatchesCount = 0,
            DiagnosticMessage = "Lookup successful"
        };

        var json = JsonSerializer.Serialize(dto, pascalOptions);

        Assert.Contains("\"items\":", json);
        Assert.Contains("\"diagnosticMessage\":", json);
        Assert.Contains("\"sonarr\":", json);
        Assert.Contains("\"radarr\":", json);
        Assert.Contains("\"title\":\"Naruto\"", json);
        Assert.Contains("\"kind\":", json);
        Assert.Contains("\"posterUrl\":", json);

        // Ensure PascalCase properties are NOT present
        Assert.DoesNotContain("\"Items\":", json);
        Assert.DoesNotContain("\"DiagnosticMessage\":", json);
        Assert.DoesNotContain("\"Sonarr\":", json);
        Assert.DoesNotContain("\"Title\":", json);
    }

    [Fact]
    public void B01_CatalogSearchResultDto_DeserializesFromBothCamelCaseAndPascalCaseJson()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var pascalJson = """
        {
            "Items": [
                {
                    "Id": "sonarr-10",
                    "Kind": 1,
                    "Title": "Naruto",
                    "Year": 2002,
                    "PosterUrl": "/images/naruto.jpg"
                }
            ],
            "Sonarr": { "Status": "Ready", "IsConfigured": true },
            "Radarr": { "Status": "Ready", "IsConfigured": true },
            "IsPartial": false,
            "DiagnosticMessage": "Diagnóstico Pascal"
        }
        """;

        var dtoFromPascal = JsonSerializer.Deserialize<CatalogSearchResultDto>(pascalJson, options);
        Assert.NotNull(dtoFromPascal);
        Assert.Single(dtoFromPascal.Items);
        Assert.Equal("Naruto", dtoFromPascal.Items[0].Title);
        Assert.Equal("Diagnóstico Pascal", dtoFromPascal.DiagnosticMessage);

        var camelJson = """
        {
            "items": [
                {
                    "id": "sonarr-10",
                    "kind": 1,
                    "title": "Naruto",
                    "year": 2002,
                    "posterUrl": "/images/naruto.jpg"
                }
            ],
            "sonarr": { "status": "Ready", "isConfigured": true },
            "radarr": { "status": "Ready", "isConfigured": true },
            "isPartial": false,
            "diagnosticMessage": "Diagnóstico Camel"
        }
        """;

        var dtoFromCamel = JsonSerializer.Deserialize<CatalogSearchResultDto>(camelJson, options);
        Assert.NotNull(dtoFromCamel);
        Assert.Single(dtoFromCamel.Items);
        Assert.Equal("Naruto", dtoFromCamel.Items[0].Title);
        Assert.Equal("Diagnóstico Camel", dtoFromCamel.DiagnosticMessage);
    }

    [Fact]
    public void B01_JavaScriptClient_CrossContractVerification_WithSimulatedHttpAndDOM()
    {
        // Executes the Node test runner verifying acceptance criteria of B01:
        // 1. PascalCase with Naruto title in Items generates card
        // 2. camelCase with Naruto title in items generates card
        // 3. PascalCase with error in DiagnosticMessage displays the diagnostic error (no false 'Sin coincidencias externas')
        // 4. camelCase with error in diagnosticMessage displays the diagnostic error
        // 5. Valid empty response displays 'Sin coincidencias externas'
        // 6. getAuthHeaders negotiates Accept: application/json; profile="CamelCase"
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.Acquisition.sln")))
        {
            dir = dir.Parent;
        }
        var workspaceRoot = dir?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var jsPath = Path.Combine(workspaceRoot, "src/Jellyfin.Plugin.Acquisition/Web/acquisition.js").Replace("\\", "/");

        var script = $$"""
        class Element {
            constructor(tagName) {
                this.tagName = tagName;
                this.children = [];
                this.parentElement = null;
                this.className = '';
                this.id = '';
                this._innerHTML = '';
                this.textContent = '';
                this.style = {};
            }
            set innerHTML(val) {
                this._innerHTML = val;
                this.children = [];
                const regex = /<([a-z0-9]+)([^>]*)>(.*?)<\/\1>/gis;
                let match;
                while ((match = regex.exec(val)) !== null) {
                    const el = new Element(match[1]);
                    const attrs = match[2];
                    const idMatch = attrs.match(/id=[\"']([^\"']+)[\"']/);
                    if (idMatch) el.id = idMatch[1];
                    const classMatch = attrs.match(/class=[\"']([^\"']+)[\"']/);
                    if (classMatch) el.className = classMatch[1];
                    el.innerHTML = match[3];
                    this.appendChild(el);
                }
            }
            get innerHTML() { return this._innerHTML; }
            appendChild(child) {
                if (child.parentElement) {
                    child.parentElement.children = child.parentElement.children.filter(c => c !== child);
                }
                child.parentElement = this;
                this.children.push(child);
                return child;
            }
            get lastElementChild() {
                return this.children.length > 0 ? this.children[this.children.length - 1] : null;
            }
            querySelector(selector) {
                if (selector.startsWith('#')) {
                    const id = selector.slice(1);
                    if (this.id === id) return this;
                    for (const c of this.children) {
                        const res = c.querySelector(selector);
                        if (res) return res;
                    }
                }
                if (selector.startsWith('.')) {
                    const cls = selector.slice(1);
                    if (this.className.split(/\s+/).includes(cls)) return this;
                    for (const c of this.children) {
                        const res = c.querySelector(selector);
                        if (res) return res;
                    }
                }
                return null;
            }
            querySelectorAll(selector) {
                let results = [];
                if (selector.startsWith('.')) {
                    const cls = selector.slice(1);
                    if (this.className.split(/\s+/).includes(cls)) results.push(this);
                }
                for (const c of this.children) {
                    results = results.concat(c.querySelectorAll(selector));
                }
                return results;
            }
            addEventListener() {}
            remove() {
                if (this.parentElement) {
                    this.parentElement.children = this.parentElement.children.filter(c => c !== child);
                    this.parentElement = null;
                }
            }
        }

        global.document = {
            readyState: 'complete',
            body: new Element('body'),
            createElement: (tag) => new Element(tag),
            getElementById: (id) => global.document.body.querySelector('#' + id),
            querySelector: (selector) => {
                if (selector === '.searchView .searchResults' || selector === 'main') {
                    return global.document.body;
                }
                return global.document.body.querySelector(selector);
            },
            addEventListener: () => {}
        };
        global.window = {
            addEventListener: () => {},
            MutationObserver: class { observe() {} },
            ApiClient: null
        };
        global.MutationObserver = global.window.MutationObserver;

        const mod = require('{{jsPath}}');

        async function run() {
            // Check headers
            const headers = mod.getAuthHeaders();
            if (!headers.Accept || !headers.Accept.includes('profile=\"CamelCase\"')) {
                throw new Error('Accept header does not request CamelCase profile');
            }

            // 1. PascalCase Naruto
            global.document.body.children = [];
            global.fetch = async () => ({
                ok: true,
                json: async () => ({
                    Items: [{
                        Id: 'sonarr-1',
                        Kind: 1,
                        Title: 'Naruto',
                        Year: 2002,
                        Overview: 'A ninja adventure',
                        PosterUrl: '/poster.jpg'
                    }],
                    Sonarr: { Status: 'Ready', IsConfigured: true },
                    Radarr: { Status: 'Ready', IsConfigured: true },
                    IsPartial: false,
                    DiagnosticMessage: null
                })
            });
            await mod.performExternalSearch('naruto');
            let cards = global.document.body.querySelectorAll('.acquisition-card');
            if (cards.length !== 1) throw new Error('Expected 1 card for PascalCase Naruto, got ' + cards.length);

            // 2. camelCase Naruto
            global.document.body.children = [];
            global.fetch = async () => ({
                ok: true,
                json: async () => ({
                    items: [{
                        id: 'sonarr-1',
                        kind: 1,
                        title: 'Naruto',
                        year: 2002,
                        overview: 'A ninja adventure',
                        posterUrl: '/poster.jpg'
                    }],
                    sonarr: { status: 'Ready', isConfigured: true },
                    radarr: { status: 'Ready', isConfigured: true },
                    isPartial: false,
                    diagnosticMessage: null
                })
            });
            await mod.performExternalSearch('naruto-camel');
            cards = global.document.body.querySelectorAll('.acquisition-card');
            if (cards.length !== 1) throw new Error('Expected 1 card for camelCase Naruto, got ' + cards.length);

            // 3. PascalCase Error
            global.document.body.children = [];
            global.fetch = async () => ({
                ok: true,
                json: async () => ({
                    Items: [],
                    Sonarr: { Status: 'Unauthorized', Message: 'Credenciales inválidas' },
                    Radarr: { Status: 'Ready' },
                    IsPartial: true,
                    DiagnosticMessage: 'Sonarr: Credenciales inválidas'
                })
            });
            await mod.performExternalSearch('naruto-err-pascal');
            let empty = global.document.body.querySelector('.acquisition-empty');
            if (!empty || !empty.innerHTML.includes('Sonarr: Credenciales inválidas')) {
                throw new Error('PascalCase error not displayed');
            }

            // 4. camelCase Error
            global.document.body.children = [];
            global.fetch = async () => ({
                ok: true,
                json: async () => ({
                    items: [],
                    sonarr: { status: 'Unauthorized', message: 'Credenciales inválidas' },
                    radarr: { status: 'Ready' },
                    isPartial: true,
                    diagnosticMessage: 'Sonarr: Credenciales inválidas'
                })
            });
            await mod.performExternalSearch('naruto-err-camel');
            empty = global.document.body.querySelector('.acquisition-empty');
            if (!empty || !empty.innerHTML.includes('Sonarr: Credenciales inválidas')) {
                throw new Error('camelCase error not displayed');
            }

            // 5. Valid empty
            global.document.body.children = [];
            global.fetch = async () => ({
                ok: true,
                json: async () => ({
                    Items: [],
                    Sonarr: { Status: 'Ready' },
                    Radarr: { Status: 'Ready' },
                    IsPartial: false,
                    DiagnosticMessage: null
                })
            });
            await mod.performExternalSearch('naruto-valid-empty');
            empty = global.document.body.querySelector('.acquisition-empty');
            if (!empty || !empty.innerHTML.includes('Sin coincidencias externas.')) {
                throw new Error('Valid empty message not displayed');
            }

            console.log('B01 JS verification completed successfully');
        }

        run().catch(err => {
            console.error(err);
            process.exit(1);
        });
        """;

        // Write script to a temp file to avoid escaping issues in CLI arguments
        var tempScript = Path.Combine(Path.GetTempPath(), $"b01_test_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tempScript, script);
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = tempScript,
                WorkingDirectory = workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            Assert.NotNull(process);
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"Node script failed (exit code {process.ExitCode}): {stdErr}\n{stdOut}");
            Assert.Contains("B01 JS verification completed successfully", stdOut);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }
}
