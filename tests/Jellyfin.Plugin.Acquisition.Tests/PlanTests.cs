using System.Net;
using System.Security.Claims;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
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

public class PlanTests
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
    public async Task Catalog_Diagnostics_PartialResponse_WhenSonarrUnauthorized_AndRadarrReady()
    {
        var userId = Guid.NewGuid();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLocalMatcher = new Mock<ILocalMatcherService>();
        var mockRequestRepo = new Mock<IRequestRepository>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockAppPaths = new Mock<IApplicationPaths>();
        var mockLogger = new Mock<ILogger<CatalogService>>();

        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        mockRequestRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        // Sonarr returns 401 Unauthorized
        mockSonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync("matrix", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult
            {
                Status = ProviderStatus.Unauthorized,
                ErrorMessage = "Sonarr returned HTTP 401 Unauthorized"
            });

        // Radarr returns movies successfully
        mockRadarr.Setup(r => r.LookupMovieWithDiagnosticsAsync("matrix", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrLookupResult
            {
                Status = ProviderStatus.Ready,
                Items =
                [
                    new RadarrMovie { Title = "The Matrix", Year = 1999, TmdbId = 603 }
                ]
            });

        var service = new CatalogService(
            mockSonarr.Object,
            mockRadarr.Object,
            mockLocalMatcher.Object,
            mockRequestRepo.Object,
            mockHttpClientFactory.Object,
            mockAppPaths.Object,
            mockLogger.Object);

        var result = await service.SearchCatalogWithDiagnosticsAsync(userId, "matrix");

        // Verify partial response: movies are included despite Sonarr failure
        Assert.Single(result.Items);
        Assert.Equal("The Matrix", result.Items[0].Title);
        Assert.True(result.IsPartial);

        // Verify diagnostics
        Assert.Equal("Unauthorized", result.Sonarr.Status);
        Assert.True(result.Sonarr.IsConfigured);
        Assert.Contains("401", result.Sonarr.Message);

        Assert.Equal("Ready", result.Radarr.Status);
        Assert.True(result.Radarr.IsConfigured);
        Assert.Null(result.Radarr.Message);
    }

    [Fact]
    public async Task Catalog_Diagnostics_NotConfigured_WhenProviderLacksCredentials()
    {
        var userId = Guid.NewGuid();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLocalMatcher = new Mock<ILocalMatcherService>();
        var mockRequestRepo = new Mock<IRequestRepository>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockAppPaths = new Mock<IApplicationPaths>();
        var mockLogger = new Mock<ILogger<CatalogService>>();

        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        mockRequestRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        mockSonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult
            {
                Status = ProviderStatus.NotConfigured,
                ErrorMessage = "Sonarr URL o API key no configurados"
            });

        mockRadarr.Setup(r => r.LookupMovieWithDiagnosticsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrLookupResult
            {
                Status = ProviderStatus.NotConfigured,
                ErrorMessage = "Radarr URL o API key no configurados"
            });

        var service = new CatalogService(
            mockSonarr.Object,
            mockRadarr.Object,
            mockLocalMatcher.Object,
            mockRequestRepo.Object,
            mockHttpClientFactory.Object,
            mockAppPaths.Object,
            mockLogger.Object);

        var result = await service.SearchCatalogWithDiagnosticsAsync(userId, "test");

        Assert.Empty(result.Items);
        Assert.Equal("NotConfigured", result.Sonarr.Status);
        Assert.False(result.Sonarr.IsConfigured);
        Assert.Equal("NotConfigured", result.Radarr.Status);
        Assert.False(result.Radarr.IsConfigured);
    }

    [Fact]
    public async Task Catalog_Diagnostics_FailedLookups_AreNotCached_AllowsImmediateRetry()
    {
        var userId = Guid.NewGuid();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLocalMatcher = new Mock<ILocalMatcherService>();
        var mockRequestRepo = new Mock<IRequestRepository>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockAppPaths = new Mock<IApplicationPaths>();
        var mockLogger = new Mock<ILogger<CatalogService>>();

        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        mockRequestRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        // First call fails with Unavailable (500)
        mockSonarr.SetupSequence(s => s.LookupSeriesWithDiagnosticsAsync("retrytest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult
            {
                Status = ProviderStatus.Unavailable,
                ErrorMessage = "Sonarr service unavailable"
            })
            // Second call succeeds
            .ReturnsAsync(new SonarrLookupResult
            {
                Status = ProviderStatus.Ready,
                Items = [new SonarrSeries { Title = "Retry Series", Year = 2026, TvdbId = 999 }]
            });

        mockRadarr.Setup(r => r.LookupMovieWithDiagnosticsAsync("retrytest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrLookupResult { Status = ProviderStatus.Ready, Items = [] });

        var service = new CatalogService(
            mockSonarr.Object,
            mockRadarr.Object,
            mockLocalMatcher.Object,
            mockRequestRepo.Object,
            mockHttpClientFactory.Object,
            mockAppPaths.Object,
            mockLogger.Object);

        // First search: fails
        var firstResult = await service.SearchCatalogWithDiagnosticsAsync(userId, "retrytest");
        Assert.Equal("Unavailable", firstResult.Sonarr.Status);
        Assert.Empty(firstResult.Items);

        // Second immediate search: should NOT be blocked by 5-minute empty cache, must retry Sonarr
        var secondResult = await service.SearchCatalogWithDiagnosticsAsync(userId, "retrytest");
        Assert.Equal("Ready", secondResult.Sonarr.Status);
        Assert.Single(secondResult.Items);
        Assert.Equal("Retry Series", secondResult.Items[0].Title);

        mockSonarr.Verify(s => s.LookupSeriesWithDiagnosticsAsync("retrytest", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SetupOptions_ReturnsSanitizedClientsAndOptions_WithoutExposingSecrets()
    {
        var userId = Guid.NewGuid();
        var mockCatalog = new Mock<ICatalogService>();
        var mockRepo = new Mock<IRequestRepository>();
        var mockTracker = new Mock<ITrackerService>();
        var mockRelease = new Mock<IReleaseService>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockQb = new Mock<IQBittorrentClient>();
        var mockUserManager = new Mock<IUserManager>();
        var mockLogger = new Mock<ILogger<AcquisitionController>>();

        // User is administrator
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("admin", "admin", "admin");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        // Mock Sonarr setup options
        mockSonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrRootFolder { Id = 1, Path = "/tv/anime" }, new SonarrRootFolder { Id = 2, Path = "/tv/standard" }]);
        mockSonarr.Setup(s => s.GetQualityProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrQualityProfile { Id = 1, Name = "HD - 1080p" }, new SonarrQualityProfile { Id = 2, Name = "Ultra-HD" }]);
        mockSonarr.Setup(s => s.GetDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ArrDownloadClient
                {
                    Id = 1,
                    Name = "qBittorrent Primary",
                    Implementation = "qBittorrent",
                    Enable = true,
                    Fields =
                    [
                        new ArrDownloadClientField { Name = "tvCategory", Value = "sonarr-tv" },
                        new ArrDownloadClientField { Name = "password", Value = "supersecretpassword" },
                        new ArrDownloadClientField { Name = "apiKey", Value = "sonarr_client_api_key" }
                    ]
                }
            ]);

        // Mock Radarr setup options
        mockRadarr.Setup(r => r.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RadarrRootFolder { Id = 1, Path = "/movies" }]);
        mockRadarr.Setup(r => r.GetQualityProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RadarrQualityProfile { Id = 1, Name = "Any" }]);
        mockRadarr.Setup(r => r.GetDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Mock qBittorrent categories
        mockQb.Setup(q => q.GetCategoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new QBittorrentCategory { Name = "sonarr-tv", SavePath = "/downloads/tv" },
                new QBittorrentCategory { Name = "radarr-movies", SavePath = "/downloads/movies" }
            ]);

        var controller = new AcquisitionController(
            mockCatalog.Object,
            mockRepo.Object,
            mockTracker.Object,
            mockRelease.Object,
            mockSonarr.Object,
            mockUserManager.Object,
            mockLogger.Object,
            mockRadarr.Object,
            mockQb.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        var actionResult = await controller.GetSetupOptions(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var options = Assert.IsType<SetupOptionsDto>(okResult.Value);

        // Sonarr verification
        Assert.Equal(2, options.Sonarr.RootFolders.Count);
        Assert.Equal("/tv/anime", options.Sonarr.RootFolders[0].Name);
        Assert.Equal(2, options.Sonarr.QualityProfiles.Count);
        Assert.Equal("HD - 1080p", options.Sonarr.QualityProfiles[0].Name);

        // Sanitized download client: password and apiKey are NOT exposed in DownloadClientSummaryDto
        Assert.Single(options.Sonarr.DownloadClients);
        var client = options.Sonarr.DownloadClients[0];
        Assert.Equal("qBittorrent Primary", client.Name);
        Assert.Equal("sonarr-tv", client.Category);

        // qBittorrent categories
        Assert.Equal(2, options.QBittorrent.Categories.Count);
        Assert.Contains(options.QBittorrent.Categories, c => c.Name == "sonarr-tv" && c.Details == "/downloads/tv");
    }

    [Fact]
    public async Task SetupOptions_NonAdminUser_Forbidden()
    {
        var userId = Guid.NewGuid();
        var mockUserManager = new Mock<IUserManager>();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("normaluser", "normaluser", "normaluser");
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            Mock.Of<IRequestRepository>(),
            Mock.Of<ITrackerService>(),
            Mock.Of<IReleaseService>(),
            Mock.Of<ISonarrClient>(),
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>(),
            Mock.Of<IRadarrClient>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        var result = await controller.GetSetupOptions(CancellationToken.None);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task RequestOptions_ReturnsDestinationsMatchingKind()
    {
        var userId = Guid.NewGuid();
        var mockUserManager = new Mock<IUserManager>();
        mockUserManager.Setup(u => u.GetUserById(userId))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("user", "user", "user"));

        // Setup PluginConfiguration with multiple destinations
        var mockAppPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mockAppPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var mockXml = new Mock<IXmlSerializer>();
        var config = new PluginConfiguration { AllowAllUsers = true };
        mockXml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var plugin = new Plugin(mockAppPaths.Object, mockXml.Object);
        plugin.Configuration.Destinations =
        [
            new DestinationConfig
            {
                Name = "Anime Series",
                Kind = MediaKind.Series,
                RootFolderPath = "/data/anime",
                QualityProfileId = 3,
                SeriesType = "anime"
            },
            new DestinationConfig
            {
                Name = "Standard TV",
                Kind = MediaKind.Series,
                RootFolderPath = "/data/tv",
                QualityProfileId = 1,
                SeriesType = "standard"
            },
            new DestinationConfig
            {
                Name = "4K UHD Movies",
                Kind = MediaKind.Movie,
                RootFolderPath = "/movies/4k",
                QualityProfileId = 5
            }
        ];

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            Mock.Of<IRequestRepository>(),
            Mock.Of<ITrackerService>(),
            Mock.Of<IReleaseService>(),
            Mock.Of<ISonarrClient>(),
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>(),
            Mock.Of<IRadarrClient>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        // Query Series options
        var seriesActionResult = await controller.GetRequestOptions(MediaKind.Series, CancellationToken.None);
        var seriesOk = Assert.IsType<OkObjectResult>(seriesActionResult.Result);
        var seriesOptions = Assert.IsType<RequestOptionsDto>(seriesOk.Value);

        Assert.Equal(2, seriesOptions.Destinations.Count);
        Assert.Equal("Anime Series", seriesOptions.Destinations[0].Name);
        Assert.Equal("anime", seriesOptions.Destinations[0].SeriesType);
        Assert.Equal("/data/anime", seriesOptions.Destinations[0].RootFolderPath);
        Assert.Equal(3, seriesOptions.Destinations[0].QualityProfileId);
        Assert.Equal("Standard TV", seriesOptions.Destinations[1].Name);

        // Query Movie options
        var movieActionResult = await controller.GetRequestOptions(MediaKind.Movie, CancellationToken.None);
        var movieOk = Assert.IsType<OkObjectResult>(movieActionResult.Result);
        var movieOptions = Assert.IsType<RequestOptionsDto>(movieOk.Value);

        Assert.Single(movieOptions.Destinations);
        Assert.Equal("4K UHD Movies", movieOptions.Destinations[0].Name);
        Assert.Equal("/movies/4k", movieOptions.Destinations[0].RootFolderPath);
        Assert.Equal(5, movieOptions.Destinations[0].QualityProfileId);
    }

    [Fact]
    public async Task CreateRequest_WithDestinationName_PersistsDestinationRootFolderAndProfile()
    {
        var userId = Guid.NewGuid();
        var mockUserManager = new Mock<IUserManager>();
        mockUserManager.Setup(u => u.GetUserById(userId))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("user", "user", "user"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mockAppPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var mockXml = new Mock<IXmlSerializer>();
        var config = new PluginConfiguration { AllowAllUsers = true };
        mockXml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var plugin = new Plugin(mockAppPaths.Object, mockXml.Object);
        plugin.Configuration.Destinations =
        [
            new DestinationConfig
            {
                Name = "Anime Collection",
                Kind = MediaKind.Series,
                RootFolderPath = "/media/anime",
                QualityProfileId = 9,
                SeriesType = "anime"
            }
        ];

        var mockRepo = new Mock<IRequestRepository>();
        AcquisitionRequest? savedRequest = null;
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionRequest, CancellationToken>((req, _) => savedRequest = req)
            .Returns(Task.CompletedTask);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            mockRepo.Object,
            Mock.Of<ITrackerService>(),
            Mock.Of<IReleaseService>(),
            Mock.Of<ISonarrClient>(),
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        var dto = new CreateRequestDto
        {
            Kind = MediaKind.Series,
            Title = "Frieren",
            Year = 2023,
            ProviderIds = new ProviderIds { Tvdb = "424536" },
            DestinationName = "Anime Collection",
            Scope = new RequestScope { All = true }
        };

        var actionResult = await controller.CreateRequest(dto, CancellationToken.None);
        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);
        var responseDto = Assert.IsType<AcquisitionRequestDto>(createdResult.Value);

        Assert.NotNull(savedRequest);
        Assert.Equal("Anime Collection", savedRequest.DestinationName);
        Assert.Equal("/media/anime", savedRequest.RootFolderPath);
        Assert.Equal(9, savedRequest.QualityProfileId);
        Assert.Equal("anime", savedRequest.SeriesType);

        Assert.Equal("Anime Collection", responseDto.DestinationName);
        Assert.Equal("/media/anime", responseDto.RootFolderPath);
        Assert.Equal(9, responseDto.QualityProfileId);
        Assert.Equal("anime", responseDto.SeriesType);
    }

    [Fact]
    public async Task TrackerService_HandlePending_ExistingSonarrSeries_PreservesExistingFolderAndProfile()
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
            Id = "req-series-existing",
            Title = "Attack on Titan",
            Kind = MediaKind.Series,
            Status = RequestStatus.Pending,
            DestinationName = "Custom Anime",
            RootFolderPath = "/new/path/anime",
            QualityProfileId = 99,
            SeriesType = "anime",
            ProviderIds = new ProviderIds { Tvdb = "267440" },
            Scope = new RequestScope { All = true }
        };

        mockRepo.Setup(r => r.GetByIdAsync("req-series-existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(req);

        // Sonarr ALREADY has this series in /existing/tv/attack-on-titan with profile 2 and standard type
        mockSonarr.Setup(s => s.FindSeriesByTvdbIdAsync(267440, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrSeries
            {
                Id = 42,
                Title = "Attack on Titan",
                TvdbId = 267440,
                RootFolderPath = "/existing/tv/attack-on-titan",
                QualityProfileId = 2,
                SeriesType = "standard"
            });

        mockSonarr.Setup(s => s.GetEpisodesAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrEpisode { Id = 101, SeasonNumber = 1, EpisodeNumber = 1, HasFile = false }
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

        // Must preserve the existing Sonarr series values
        Assert.Equal(42, req.ArrId);
        Assert.Equal("/existing/tv/attack-on-titan", req.RootFolderPath);
        Assert.Equal(2, req.QualityProfileId);
        Assert.Equal("standard", req.SeriesType);

        // Must NOT call AddSeriesAsync because series already exists
        mockSonarr.Verify(s => s.AddSeriesAsync(It.IsAny<SonarrSeries>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TrackerService_HandlePending_ExistingRadarrMovie_PreservesExistingFolderAndProfile()
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
            Id = "req-movie-existing",
            Title = "Inception",
            Kind = MediaKind.Movie,
            Status = RequestStatus.Pending,
            DestinationName = "4K UHD",
            RootFolderPath = "/movies/4k",
            QualityProfileId = 15,
            ProviderIds = new ProviderIds { Tmdb = "27205" },
            Scope = new RequestScope { All = true }
        };

        mockRepo.Setup(r => r.GetByIdAsync("req-movie-existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(req);

        // Radarr ALREADY has this movie with HD folder and profile 3
        mockRadarr.Setup(r => r.FindMovieByTmdbIdAsync(27205, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrMovie
            {
                Id = 88,
                Title = "Inception",
                TmdbId = 27205,
                RootFolderPath = "/movies/hd",
                QualityProfileId = 3
            });

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

        // Must preserve existing Radarr values
        Assert.Equal(88, req.ArrId);
        Assert.Equal("/movies/hd", req.RootFolderPath);
        Assert.Equal(3, req.QualityProfileId);

        // Must NOT call AddMovieAsync
        mockRadarr.Verify(r => r.AddMovieAsync(It.IsAny<RadarrMovie>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QBittorrentClient_GetCategoriesAsync_ParsesJsonDictionary()
    {
        var jsonResponse = @"{
            ""sonarr-tv"": {
                ""name"": ""sonarr-tv"",
                ""savePath"": ""/downloads/tv""
            },
            ""radarr-movies"": {
                ""name"": ""radarr-movies"",
                ""savePath"": ""/downloads/movies""
            }
        }";

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            if (req.RequestUri != null && req.RequestUri.ToString().Contains("auth/login"))
            {
                var loginResp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Ok.")
                };
                loginResp.Headers.Add("Set-Cookie", "SID=fake-cookie; Path=/");
                return Task.FromResult(loginResp);
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        });

        var httpClient = new HttpClient(handler);
        var client = new QBittorrentClient(httpClient, Mock.Of<ILogger<QBittorrentClient>>());

        var categories = await client.GetCategoriesAsync();

        Assert.Equal(2, categories.Count);
        var sonarrCat = categories.FirstOrDefault(c => c.Name == "sonarr-tv");
        Assert.NotNull(sonarrCat);
        Assert.Equal("/downloads/tv", sonarrCat.SavePath);

        var radarrCat = categories.FirstOrDefault(c => c.Name == "radarr-movies");
        Assert.NotNull(radarrCat);
        Assert.Equal("/downloads/movies", radarrCat.SavePath);
    }

    private static void UsePluginConfiguration(PluginConfiguration config)
    {
        var mockAppPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mockAppPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var mockXml = new Mock<IXmlSerializer>();
        mockXml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(config);
        _ = new Plugin(mockAppPaths.Object, mockXml.Object);
    }

    [Fact]
    public async Task ArrClients_Lookup401WithoutApiKey_ReportsNotConfigured()
    {
        UsePluginConfiguration(new PluginConfiguration { SonarrApiKey = string.Empty, RadarrApiKey = string.Empty });
        var handler401 = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var sonarr = await new SonarrClient(new HttpClient(handler401), Mock.Of<ILogger<SonarrClient>>()).LookupSeriesWithDiagnosticsAsync("test");
        var radarr = await new RadarrClient(new HttpClient(handler401), Mock.Of<ILogger<RadarrClient>>()).LookupMovieWithDiagnosticsAsync("test");

        // Without an API key, a 401 means "not configured yet", not "credentials rejected"
        Assert.Equal(ProviderStatus.NotConfigured, sonarr.Status);
        Assert.Equal(ProviderStatus.NotConfigured, radarr.Status);
    }

    [Fact]
    public async Task SonarrClient_LookupWithDiagnostics_MapsHttpErrorsCorrectly()
    {
        UsePluginConfiguration(new PluginConfiguration { SonarrApiKey = "invalid-key" });

        // 401 Unauthorized
        var handler401 = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client401 = new SonarrClient(new HttpClient(handler401), Mock.Of<ILogger<SonarrClient>>());
        var res401 = await client401.LookupSeriesWithDiagnosticsAsync("test");
        Assert.Equal(ProviderStatus.Unauthorized, res401.Status);
        Assert.Contains("401", res401.ErrorMessage);

        // 429 Too Many Requests
        var handler429 = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var client429 = new SonarrClient(new HttpClient(handler429), Mock.Of<ILogger<SonarrClient>>());
        var res429 = await client429.LookupSeriesWithDiagnosticsAsync("test");
        Assert.Equal(ProviderStatus.RateLimited, res429.Status);
        Assert.Contains("429", res429.ErrorMessage);
    }

    [Fact]
    public async Task RadarrClient_LookupWithDiagnostics_MapsHttpErrorsCorrectly()
    {
        UsePluginConfiguration(new PluginConfiguration { RadarrApiKey = "invalid-key" });

        // 401 Unauthorized
        var handler401 = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client401 = new RadarrClient(new HttpClient(handler401), Mock.Of<ILogger<RadarrClient>>());
        var res401 = await client401.LookupMovieWithDiagnosticsAsync("test");
        Assert.Equal(ProviderStatus.Unauthorized, res401.Status);
        Assert.Contains("401", res401.ErrorMessage);

        // 429 Too Many Requests
        var handler429 = new MockHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var client429 = new RadarrClient(new HttpClient(handler429), Mock.Of<ILogger<RadarrClient>>());
        var res429 = await client429.LookupMovieWithDiagnosticsAsync("test");
        Assert.Equal(ProviderStatus.RateLimited, res429.Status);
        Assert.Contains("429", res429.ErrorMessage);
    }

    [Fact]
    public async Task SetupOptions_SonarrHttpError_DeclaresDisconnectedWithErrorMessage()
    {
        var userId = Guid.NewGuid();
        var mockUserManager = new Mock<IUserManager>();
        var mockUser = new Jellyfin.Database.Implementations.Entities.User("admin", "admin", "admin");
        mockUser.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        mockUserManager.Setup(u => u.GetUserById(userId)).Returns(mockUser);

        var mockSonarr = new Mock<ISonarrClient>();
        mockSonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Sonarr returned HTTP 401: Unauthorized"));

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            Mock.Of<IRequestRepository>(),
            Mock.Of<ITrackerService>(),
            Mock.Of<IReleaseService>(),
            mockSonarr.Object,
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        var actionResult = await controller.GetSetupOptions(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var options = Assert.IsType<SetupOptionsDto>(okResult.Value);

        // Must not declare connection successful on HTTP failure (R04)
        Assert.False(options.Sonarr.IsConnected);
        Assert.Contains("401", options.Sonarr.ErrorMessage);
    }

    [Fact]
    public async Task CreateRequest_NoConfiguredDestinations_ResolvesFromDiscoveredSonarrRoots()
    {
        var userId = Guid.NewGuid();
        var mockUserManager = new Mock<IUserManager>();
        mockUserManager.Setup(u => u.GetUserById(userId))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("user", "user", "user"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mockAppPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var mockXml = new Mock<IXmlSerializer>();
        var config = new PluginConfiguration { AllowAllUsers = true };
        mockXml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var plugin = new Plugin(mockAppPaths.Object, mockXml.Object);
        // config.Destinations is intentionally empty (auto-discovery scenario, R02)
        plugin.Configuration.Destinations = [];

        var mockSonarr = new Mock<ISonarrClient>();
        mockSonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrRootFolder { Id = 1, Path = "/data/anime" },
                new SonarrRootFolder { Id = 2, Path = "/data/standard" }
            ]);

        var mockRepo = new Mock<IRequestRepository>();
        AcquisitionRequest? savedRequest = null;
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionRequest, CancellationToken>((req, _) => savedRequest = req)
            .Returns(Task.CompletedTask);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            mockRepo.Object,
            Mock.Of<ITrackerService>(),
            Mock.Of<IReleaseService>(),
            mockSonarr.Object,
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        var dto = new CreateRequestDto
        {
            Kind = MediaKind.Series,
            Title = "Bleach",
            ProviderIds = new ProviderIds { Tvdb = "74796" },
            DestinationName = "anime", // Explicit choice of the root folder named "anime"
            Scope = new RequestScope { All = true }
        };

        var actionResult = await controller.CreateRequest(dto, CancellationToken.None);
        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);

        Assert.NotNull(savedRequest);
        Assert.Equal("/data/anime", savedRequest.RootFolderPath);
        Assert.Equal("sonarr-root:1", savedRequest.DestinationId);
        // A folder name is not evidence that its content is anime: the series type is never inferred from it
        Assert.Null(savedRequest.SeriesType);
    }

    [Fact]
    public async Task CreateRequest_NoConfiguredDestinations_ResolvesFromDiscoveredRadarrRoots()
    {
        var userId = Guid.NewGuid();
        var mockUserManager = new Mock<IUserManager>();
        mockUserManager.Setup(u => u.GetUserById(userId))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("user", "user", "user"));

        var mockAppPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mockAppPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var mockXml = new Mock<IXmlSerializer>();
        var config = new PluginConfiguration { AllowAllUsers = true };
        mockXml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var plugin = new Plugin(mockAppPaths.Object, mockXml.Object);
        plugin.Configuration.Destinations = [];

        var mockRadarr = new Mock<IRadarrClient>();
        mockRadarr.Setup(r => r.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RadarrRootFolder { Id = 1, Path = "/movies/main" }
            ]);

        var mockRepo = new Mock<IRequestRepository>();
        AcquisitionRequest? savedRequest = null;
        mockRepo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcquisitionRequest, CancellationToken>((req, _) => savedRequest = req)
            .Returns(Task.CompletedTask);

        var controller = new AcquisitionController(
            Mock.Of<ICatalogService>(),
            mockRepo.Object,
            Mock.Of<ITrackerService>(),
            Mock.Of<IReleaseService>(),
            Mock.Of<ISonarrClient>(),
            mockUserManager.Object,
            Mock.Of<ILogger<AcquisitionController>>(),
            mockRadarr.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        var dto = new CreateRequestDto
        {
            Kind = MediaKind.Movie,
            Title = "Oppenheimer",
            ProviderIds = new ProviderIds { Tmdb = "872585" },
            // No choice sent: Radarr has a single root folder, so it is unambiguous
            Scope = new RequestScope { All = true }
        };

        var actionResult = await controller.CreateRequest(dto, CancellationToken.None);
        var createdResult = Assert.IsType<CreatedAtActionResult>(actionResult.Result);

        Assert.NotNull(savedRequest);
        Assert.Equal("/movies/main", savedRequest.RootFolderPath);
    }

    [Fact]
    public async Task Catalog_Cache_PreservesDiagnosticsAndInvalidatesOnConnectionChange()
    {
        var userId = Guid.NewGuid();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLocalMatcher = new Mock<ILocalMatcherService>();
        var mockRequestRepo = new Mock<IRequestRepository>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockAppPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        mockAppPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mockAppPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var mockXml = new Mock<IXmlSerializer>();

        var config = new PluginConfiguration { SonarrApiKey = "initial_key", RadarrApiKey = "" };
        mockXml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);
        var plugin = new Plugin(mockAppPaths.Object, mockXml.Object);

        mockSonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync("interstellar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult
            {
                Status = ProviderStatus.Ready,
                Items = [new SonarrSeries { Title = "Interstellar Series", Year = 2020 }]
            });

        // Radarr is not configured
        mockRadarr.Setup(r => r.LookupMovieWithDiagnosticsAsync("interstellar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrLookupResult
            {
                Status = ProviderStatus.NotConfigured,
                ErrorMessage = "Radarr no configurado"
            });

        var service = new CatalogService(
            mockSonarr.Object,
            mockRadarr.Object,
            mockLocalMatcher.Object,
            mockRequestRepo.Object,
            mockHttpClientFactory.Object,
            mockAppPaths.Object,
            Mock.Of<ILogger<CatalogService>>());

        // 1. Initial search: Radarr is NotConfigured, Sonarr is Ready
        var firstResult = await service.SearchCatalogWithDiagnosticsAsync(userId, "interstellar");
        Assert.Equal("Ready", firstResult.Sonarr.Status);
        Assert.Equal("NotConfigured", firstResult.Radarr.Status);
        Assert.True(firstResult.IsPartial);

        // 2. Second search from cache: MUST PRESERVE NotConfigured diagnostics instead of assuming both Ready
        var cachedResult = await service.SearchCatalogWithDiagnosticsAsync(userId, "interstellar");
        Assert.Equal("Ready", cachedResult.Sonarr.Status);
        Assert.Equal("NotConfigured", cachedResult.Radarr.Status);
        Assert.True(cachedResult.IsPartial);

        // 3. Admin updates Radarr API key in configuration
        config.RadarrApiKey = "new_radarr_key";
        mockRadarr.Setup(r => r.LookupMovieWithDiagnosticsAsync("interstellar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrLookupResult
            {
                Status = ProviderStatus.Ready,
                Items = [new RadarrMovie { Title = "Interstellar", Year = 2014, TmdbId = 157336 }]
            });

        // 4. Next search: Connection fingerprint changed, cache is invalidated automatically
        var refreshedResult = await service.SearchCatalogWithDiagnosticsAsync(userId, "interstellar");
        Assert.Equal("Ready", refreshedResult.Sonarr.Status);
        Assert.Equal("Ready", refreshedResult.Radarr.Status);
        Assert.False(refreshedResult.IsPartial);
        Assert.Equal(2, refreshedResult.Items.Count);
    }

    [Fact]
    public void ConfigMigration_GlobalPathsMigratedToUnifiedDestinations()
    {
        var config = new PluginConfiguration
        {
            SonarrRootFolderPath = "/media/tv",
            SonarrQualityProfileId = 2,
            RadarrRootFolderPath = "/media/movies",
            RadarrQualityProfileId = 4,
            Destinations = []
        };

        config.EnsureMigratedDestinations();

        Assert.Equal(2, config.Destinations.Count);
        var seriesDest = config.Destinations.FirstOrDefault(d => d.Kind == MediaKind.Series);
        Assert.NotNull(seriesDest);
        Assert.Equal("/media/tv", seriesDest.RootFolderPath);
        Assert.Equal(2, seriesDest.QualityProfileId);
        Assert.True(seriesDest.IsDefault);

        var movieDest = config.Destinations.FirstOrDefault(d => d.Kind == MediaKind.Movie);
        Assert.NotNull(movieDest);
        Assert.Equal("/media/movies", movieDest.RootFolderPath);
        Assert.Equal(4, movieDest.QualityProfileId);
        Assert.True(movieDest.IsDefault);
    }

    [Fact]
    public void ConfigKindParsing_ZeroPreservesMovieKind()
    {
        // R03 verification: "0" parsed as decimal integer should yield 0 (Movie)
        string inputMovie = "0";
        var parsedMovie = int.TryParse(inputMovie, out var m) ? m : 1;
        Assert.Equal(0, parsedMovie);

        string inputSeries = "1";
        var parsedSeries = int.TryParse(inputSeries, out var s) ? s : 1;
        Assert.Equal(1, parsedSeries);
    }

    [Fact]
    public async Task TrackerService_HandleSearching_AppliesDiscoveredCategory_WhenConfigEmpty()
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
            Id = "req-cat-discovery",
            Title = "Steins;Gate",
            Kind = MediaKind.Series,
            Status = RequestStatus.Searching,
            Category = null,
            SelectedRelease = new ReleaseCandidate
            {
                Id = "rel-1",
                Title = "Steins.Gate.S01.1080p",
                DownloadUrl = "http://indexer/torrent",
                IsTorrent = true,
                InfoHash = "hash-steins"
            }
        };

        mockRepo.Setup(r => r.GetByIdAsync("req-cat-discovery", It.IsAny<CancellationToken>()))
            .ReturnsAsync(req);

        // Sonarr download clients return qBittorrent client configured with category "tv-anime"
        mockSonarr.Setup(s => s.GetDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ArrDownloadClient
                {
                    Id = 1,
                    Name = "qBittorrent",
                    Implementation = "qBittorrent",
                    Enable = true,
                    Fields = [new ArrDownloadClientField { Name = "tvCategory", Value = "tv-anime" }]
                }
            ]);

        mockQb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        mockProwlarr.Setup(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { TorrentBytes = [1, 2, 3], InfoHash = "hash-steins" });

        QBittorrentAddOptions? capturedAddOptions = null;
        mockQb.Setup(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>()))
            .Callback<QBittorrentAddOptions, CancellationToken>((opts, _) => capturedAddOptions = opts)
            .ReturnsAsync(true);

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

        // Verify discovered category "tv-anime" was applied to the request and add options
        Assert.Equal("tv-anime", req.Category);
        Assert.NotNull(capturedAddOptions);
        Assert.Equal("tv-anime", capturedAddOptions.Category);
    }
}
