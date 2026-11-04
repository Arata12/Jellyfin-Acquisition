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
        Assert.Contains(results[0].RejectionReasons, r => r.Contains("Es de otro año"));
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
        Assert.Contains(results[0].RejectionReasons, r => r.Contains("Es otr"));
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

    // "Girlfriend, Girlfriend" (TVDB 395486, TMDB 113137) is released as "Kanojo mo Kanojo"; "Rent-a-Girlfriend"
    // (TVDB 380654, TMDB 96316) shares a word and has far more seeders
    private static AcquisitionRequest GirlfriendGirlfriendE01() => new()
    {
        Title = "Girlfriend, Girlfriend",
        Year = 2021,
        Kind = MediaKind.Series,
        ArrId = 7,
        ProviderIds = new ProviderIds { Tvdb = "395486", Tmdb = "113137" },
        Scope = new RequestScope { All = false, SeasonNumbers = [1], EpisodeNumbers = [1] }
    };

    private static ProwlarrRelease Release(string title, int seeders, int tvdb = 0, int tmdb = 0) =>
        new() { Title = title, Protocol = "torrent", Seeders = seeders, Size = 1_000_000_000L, TvdbId = tvdb, TmdbId = tmdb };

    [Fact]
    public async Task SearchAndEvaluate_ReleaseIds_DecideOverNameAndSeeders()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => !q.ById), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Release("Rent-a-Girlfriend S01E01 1080p WEB-DL x264", 999),
                Release("Girlfriend.Girlfriend.S01E01.1080p.WEB-DL.x264-WRONGID", 800, tvdb: 380654, tmdb: 96316),
                Release("KanoKano.S01E01.1080p.WEB-DL.x264-TAGGED", 3, tvdb: 395486, tmdb: 113137)
            ]);
        // An indexer answering the search by ID: its untagged result is the requested series whatever its name
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => q.ById), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Release("Kanojo.mo.Kanojo.S01E01.720p.WEB-DL.x264-IDSEARCH", 2)]);

        var service = new ReleaseService(mockProwlarr.Object, new Mock<ISonarrClient>().Object, new Mock<IRadarrClient>().Object, new Mock<ILogger<ReleaseService>>().Object);
        var results = await service.SearchAndEvaluateReleasesAsync(GirlfriendGirlfriendE01());

        var wrongId = results.Single(r => r.Title.EndsWith("WRONGID", StringComparison.Ordinal));
        Assert.False(wrongId.IsEligible);
        Assert.Contains(wrongId.RejectionReasons, r => r.Contains("Es otr", StringComparison.Ordinal) && r.Contains("380654", StringComparison.Ordinal));
        Assert.False(results.Single(r => r.Title.StartsWith("Rent-a-Girlfriend", StringComparison.Ordinal)).IsEligible);
        Assert.True(results.Single(r => r.Title.EndsWith("TAGGED", StringComparison.Ordinal)).IsEligible);
        Assert.True(results.Single(r => r.Title.EndsWith("IDSEARCH", StringComparison.Ordinal)).IsEligible);

        // 1080p tagged release first (quality), then the 720p one; the other series never above them
        Assert.Equal("KanoKano.S01E01.1080p.WEB-DL.x264-TAGGED", service.PickBestRelease(results)!.Title);
        Assert.Equal(["KanoKano.S01E01.1080p.WEB-DL.x264-TAGGED", "Kanojo.mo.Kanojo.S01E01.720p.WEB-DL.x264-IDSEARCH"], results.Take(2).Select(r => r.Title));
    }

    [Fact]
    public async Task SearchAndEvaluate_NameInAnotherLanguage_MatchesAliasReadById()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => !q.ById), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Release("Rent-a-Girlfriend S01E01 1080p WEB-DL x264", 999),
                Release("[SubsPlease] Kanojo mo Kanojo S01E01 (1080p) [WEB-DL]", 5)
            ]);
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => q.ById), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var aliases = new Mock<ITitleAliasService>();
        aliases.Setup(a => a.GetAliasesAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["Kanojo mo Kanojo", "KanoKano"]);

        var service = new ReleaseService(mockProwlarr.Object, new Mock<ISonarrClient>().Object, new Mock<IRadarrClient>().Object, new Mock<ILogger<ReleaseService>>().Object, aliases.Object);
        var results = await service.SearchAndEvaluateReleasesAsync(GirlfriendGirlfriendE01());

        Assert.Equal("[SubsPlease] Kanojo mo Kanojo S01E01 (1080p) [WEB-DL]", service.PickBestRelease(results)!.Title);
        Assert.Equal("[SubsPlease] Kanojo mo Kanojo S01E01 (1080p) [WEB-DL]", results[0].Title);
        Assert.False(results.Single(r => r.Title.StartsWith("Rent-a-Girlfriend", StringComparison.Ordinal)).IsEligible);
    }

    [Fact]
    public async Task SearchAndEvaluate_NothingUnderRequestedName_SearchesAgainWithAlias()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => !q.ById && q.Query == "Girlfriend, Girlfriend"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Release("Rent-a-Girlfriend S01E01 1080p WEB-DL x264", 999)]);
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => !q.ById && q.Query == "Kanojo mo Kanojo"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Release("Kanojo mo Kanojo S01E01 1080p WEB-DL x264", 4)]);
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => q.ById), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var aliases = new Mock<ITitleAliasService>();
        aliases.Setup(a => a.GetAliasesAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["カノジョも彼女", "Kanojo mo Kanojo"]);

        var service = new ReleaseService(mockProwlarr.Object, new Mock<ISonarrClient>().Object, new Mock<IRadarrClient>().Object, new Mock<ILogger<ReleaseService>>().Object, aliases.Object);
        var results = await service.SearchAndEvaluateReleasesAsync(GirlfriendGirlfriendE01());

        Assert.Equal("Kanojo mo Kanojo S01E01 1080p WEB-DL x264", service.PickBestRelease(results)!.Title);
        // The native-script alias is used for matching only, never sent as an indexer query
        mockProwlarr.Verify(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => q.Query == "カノジョも彼女"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAndEvaluate_ArrIdentifiesAnotherSeries_AliasCannotOverrideIt()
    {
        var mockProwlarr = new Mock<IProwlarrClient>();
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => !q.ById), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Release("Kanojo mo Kanojo S01E01 1080p WEB-DL x264", 50)]);
        mockProwlarr.Setup(p => p.SearchAsync(It.Is<ProwlarrSearchQuery>(q => q.ById), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var mockSonarr = new Mock<ISonarrClient>();
        mockSonarr.Setup(s => s.ParseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrParseResult { Series = new SonarrSeries { Id = 99, TvdbId = 380654, Title = "Rent-a-Girlfriend" } });
        var aliases = new Mock<ITitleAliasService>();
        aliases.Setup(a => a.GetAliasesAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["Kanojo mo Kanojo"]);

        var service = new ReleaseService(mockProwlarr.Object, mockSonarr.Object, new Mock<IRadarrClient>().Object, new Mock<ILogger<ReleaseService>>().Object, aliases.Object);
        var results = await service.SearchAndEvaluateReleasesAsync(GirlfriendGirlfriendE01());

        Assert.False(results.Single().IsEligible);
        Assert.Contains(results.Single().RejectionReasons, r => r.Contains("Es otr", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MediaKind.Series, "395486", "113137", null, 1, 3, "{TvdbId:395486}{TmdbId:113137}{Season:1}{Episode:3}")]
    [InlineData(MediaKind.Series, "395486", null, null, 2, null, "{TvdbId:395486}{Season:2}")]
    [InlineData(MediaKind.Movie, null, "129", "tt0245429", null, null, "{TmdbId:129}{ImdbId:tt0245429}")]
    [InlineData(MediaKind.Movie, null, null, null, null, null, "")]
    public void BuildIdQuery_UsesProwlarrIdTokens(MediaKind kind, string? tvdb, string? tmdb, string? imdb, int? season, int? episode, string expected)
    {
        var query = new ProwlarrSearchQuery { Kind = kind, TvdbId = tvdb, TmdbId = tmdb, ImdbId = imdb, Season = season, Episode = episode };
        Assert.Equal(expected, ProwlarrClient.BuildIdQuery(query));
    }
}
