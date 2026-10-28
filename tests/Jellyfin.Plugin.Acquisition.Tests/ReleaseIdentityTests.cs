using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Releases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class ReleaseIdentityTests
{
    [Fact]
    public async Task SearchAndEvaluate_MovieWithWrongYear_IsRejected()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        // User is requesting "Dune" (2021)
        var request = new AcquisitionRequest
        {
            Title = "Dune",
            Year = 2021,
            Kind = MediaKind.Movie,
            Scope = new RequestScope { All = true }
        };

        // Prowlarr returns 1984 movie
        var releases = new List<ProwlarrRelease>
        {
            new() { Title = "Dune.1984.1080p.BluRay.x264", Protocol = "torrent", Seeders = 100, Size = 2_000_000_000L }
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);

        var service = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);
        var results = await service.SearchAndEvaluateReleasesAsync(request);

        Assert.Single(results);
        Assert.False(results[0].IsEligible);
        Assert.Contains(results[0].RejectionReasons, r => r.Contains("Year mismatch"));
    }

    [Fact]
    public async Task SearchAndEvaluate_SeriesParsedMismatch_IsRejected()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        var request = new AcquisitionRequest
        {
            Title = "Chernobyl",
            Kind = MediaKind.Series,
            ArrId = 10,
            ProviderIds = new ProviderIds { Tvdb = "360893" },
            Scope = new RequestScope { All = false, SeasonNumbers = [1], EpisodeNumbers = [1] }
        };

        var releases = new List<ProwlarrRelease>
        {
            new() { Title = "Chernobyl.Diaries.2012.1080p.BluRay", Protocol = "torrent", Seeders = 50, Size = 2_000_000_000L }
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);

        // Sonarr parses this release to a different series
        mockSonarr.Setup(s => s.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrParseResult
            {
                Series = new SonarrSeries { Id = 99, TvdbId = 999999, Title = "Chernobyl Diaries" }
            });

        var service = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);
        var results = await service.SearchAndEvaluateReleasesAsync(request);

        Assert.Single(results);
        Assert.False(results[0].IsEligible);
        Assert.Contains(results[0].RejectionReasons, r => r.Contains("Identity mismatch"));
    }

    [Fact]
    public async Task SearchAndEvaluate_PreferredQualityFirst_SeedersBreakTies()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        var mockSonarr = new Mock<ISonarrClient>();
        var mockRadarr = new Mock<IRadarrClient>();
        var mockLogger = new Mock<ILogger<ReleaseService>>();

        var request = new AcquisitionRequest
        {
            Title = "Inception",
            Year = 2010,
            Kind = MediaKind.Movie,
            Scope = new RequestScope { All = true }
        };

        // Default preferences: 1080p, WEB-DL, H.264
        var releases = new List<ProwlarrRelease>
        {
            new() { Title = "Inception.2010.1080p.WEBRip.x264", Protocol = "torrent", Seeders = 9000, Size = 2_000_000_000L },
            new() { Title = "Inception.2010.1080p.WEB-DL.x264", Protocol = "torrent", Seeders = 40, Size = 3_000_000_000L },
            new() { Title = "Inception.2010.1080p.WEB-DL.x264-OTHER", Protocol = "torrent", Seeders = 300, Size = 3_100_000_000L }
        };

        mockProwlarr.Setup(p => p.SearchAsync(It.IsAny<ProwlarrSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);

        var service = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, mockRadarr.Object, mockLogger.Object);
        var results = await service.SearchAndEvaluateReleasesAsync(request);

        // The preferred WEB-DL wins over a WEBRip with far more seeders; between the two WEB-DL, the one with more seeders
        Assert.Equal(["Inception.2010.1080p.WEB-DL.x264-OTHER", "Inception.2010.1080p.WEB-DL.x264", "Inception.2010.1080p.WEBRip.x264"], results.Select(r => r.Title));
        Assert.Equal("Inception.2010.1080p.WEB-DL.x264-OTHER", service.PickBestRelease(results)!.Title);
    }
}
