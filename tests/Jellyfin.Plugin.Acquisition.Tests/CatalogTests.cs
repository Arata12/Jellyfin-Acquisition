using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class CatalogTests
{
    [Fact]
    public async Task SearchCatalog_FatherLocalAndMotherExternal_BothHandledCorrectlyWithoutDuplication()
    {
        // Arrange
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

        // Sonarr returns both "How I Met Your Father" and "How I Met Your Mother"
        mockSonarr.Setup(s => s.LookupSeriesAsync("how i met", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SonarrSeries { Title = "How I Met Your Father", Year = 2022, TvdbId = 398246 },
                new SonarrSeries { Title = "How I Met Your Mother", Year = 2005, TvdbId = 75760 }
            ]);
        mockRadarr.Setup(r => r.LookupMovieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Local library has "Father", but does NOT have "Mother"
        mockLocalMatcher.Setup(m => m.MatchBatch(userId, It.IsAny<List<CatalogItemDto>>()))
            .Callback<Guid, List<CatalogItemDto>>((uid, list) =>
            {
                var f = list.FirstOrDefault(x => x.Title == "How I Met Your Father");
                if (f != null)
                {
                    f.IsLocal = true;
                    f.LocalItemId = Guid.NewGuid();
                }
            });

        var service = new CatalogService(
            mockSonarr.Object,
            mockRadarr.Object,
            mockLocalMatcher.Object,
            mockRequestRepo.Object,
            mockHttpClientFactory.Object,
            mockAppPaths.Object,
            mockLogger.Object);

        // Act
        var results = await service.SearchCatalogAsync(userId, "how i met");

        // Assert - Father is omitted from external results to avoid duplicating local results (R11)
        Assert.Single(results);

        var mother = results[0];
        Assert.Equal("How I Met Your Mother", mother.Title);
        Assert.False(mother.IsLocal);
        Assert.Null(mother.LocalItemId);
    }

    [Fact]
    public async Task SearchCatalog_CatalogFailure_DoesNotThrow()
    {
        // Arrange
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

        mockSonarr.Setup(s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Sonarr offline"));
        mockRadarr.Setup(r => r.LookupMovieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RadarrMovie { Title = "Inception", Year = 2010, TmdbId = 27205 }]);

        var service = new CatalogService(
            mockSonarr.Object,
            mockRadarr.Object,
            mockLocalMatcher.Object,
            mockRequestRepo.Object,
            mockHttpClientFactory.Object,
            mockAppPaths.Object,
            mockLogger.Object);

        // Act
        var results = await service.SearchCatalogAsync(userId, "inception");

        // Assert - Radarr result is still returned without throwing
        Assert.Single(results);
        Assert.Equal("Inception", results[0].Title);
    }
}
