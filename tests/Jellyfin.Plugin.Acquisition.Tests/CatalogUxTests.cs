using System.Net;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Registration;
using Jellyfin.Plugin.Acquisition.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class CatalogUxTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private static CatalogService CreateService(Mock<ISonarrClient> sonarr, Mock<IRadarrClient> radarr, Action<List<CatalogItemDto>>? matcher = null)
    {
        var localMatcher = new Mock<ILocalMatcherService>();
        if (matcher != null)
        {
            localMatcher.Setup(m => m.MatchBatch(It.IsAny<Guid>(), It.IsAny<List<CatalogItemDto>>()))
                .Callback<Guid, List<CatalogItemDto>>((_, list) => matcher(list));
        }

        var requestRepo = new Mock<IRequestRepository>();
        requestRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());

        return new CatalogService(
            sonarr.Object,
            radarr.Object,
            localMatcher.Object,
            requestRepo.Object,
            new Mock<IHttpClientFactory>().Object,
            appPaths.Object,
            new Mock<ILogger<CatalogService>>().Object);
    }

    private static SonarrSeries Series(string title, int year, int tvdb) => new() { Title = title, Year = year, TvdbId = tvdb };

    [Fact]
    public async Task Search_SpiritedAwayFoundByAlternateTitle_RanksAheadOfUnrelatedSeries()
    {
        var sonarr = new Mock<ISonarrClient>();
        var radarr = new Mock<IRadarrClient>();
        sonarr.Setup(s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 20).Select(i => Series($"Viaje al fondo {i}", 2000 + i, 1000 + i)).ToList());
        radarr.Setup(r => r.LookupMovieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RadarrMovie
                {
                    Title = "Spirited Away",
                    OriginalTitle = "千と千尋の神隠し",
                    Year = 2001,
                    TmdbId = 129,
                    AlternateTitles = [new RadarrAlternateTitle { Title = "El viaje de Chihiro" }]
                },
                new RadarrMovie { Title = "Otra película", Year = 2010, TmdbId = 5 }
            ]);

        var results = await CreateService(sonarr, radarr).SearchCatalogAsync(Guid.NewGuid(), "El viaje de Chihiro");

        Assert.Equal("Spirited Away", results[0].Title);
        Assert.Equal(MediaKind.Movie, results[0].Kind);
    }

    [Fact]
    public async Task Search_ExactTitleBeatsPrefixMatchesFarDownTheSourceList()
    {
        var sonarr = new Mock<ISonarrClient>();
        var radarr = new Mock<IRadarrClient>();
        var series = Enumerable.Range(1, 33).Select(i => Series($"Serie sin relación {i}", 2000, i)).ToList();
        series.Insert(12, Series("Girlfriend, Girlfriend", 2021, 900));
        series.Add(Series("Girlfriend", 2020, 901));
        sonarr.Setup(s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(series);
        radarr.Setup(r => r.LookupMovieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RadarrMovie { Title = "Girlfriend Experience", Year = 2009, TmdbId = 7 }]);

        var results = await CreateService(sonarr, radarr).SearchCatalogAsync(Guid.NewGuid(), "girlfriend");

        Assert.Equal("Girlfriend", results[0].Title);
        Assert.True(results.FindIndex(r => r.Title == "Girlfriend, Girlfriend") < 3);
    }

    [Fact]
    public void Rank_IsCaseDiacriticsPunctuationAndAmpersandInsensitive()
    {
        var items = new List<CatalogItemDto>
        {
            new() { Id = "a", Kind = MediaKind.Series, Title = "Otra cosa" },
            new() { Id = "b", Kind = MediaKind.Series, Title = "Pokémon & Friends: ¡Ya!" }
        };

        var ranked = CatalogRanking.Rank(items, "POKEMON and friends ya");

        Assert.Equal("b", ranked[0].Id);
    }

    [Fact]
    public void Rank_YearInQueryBoostsMatchingYear_AndTiesKeepOriginalOrder()
    {
        var items = new List<CatalogItemDto>
        {
            new() { Id = "a", Kind = MediaKind.Series, Title = "Dune", Year = 1984 },
            new() { Id = "b", Kind = MediaKind.Series, Title = "Dune", Year = 2021 },
            new() { Id = "c", Kind = MediaKind.Series, Title = "Dune", Year = 2000 }
        };

        var withYear = CatalogRanking.Rank(items, "dune 2021");
        Assert.Equal(["b", "a", "c"], withYear.Select(i => i.Id));

        var noYear = CatalogRanking.Rank(items, "dune");
        Assert.Equal(["a", "b", "c"], noYear.Select(i => i.Id));
    }

    [Fact]
    public void Rank_InterleavesTopOfEachSource()
    {
        var items = new List<CatalogItemDto>
        {
            new() { Id = "s1", Kind = MediaKind.Series, Title = "Zeta" },
            new() { Id = "s2", Kind = MediaKind.Series, Title = "Zeta dos" },
            new() { Id = "m1", Kind = MediaKind.Movie, Title = "Eta" },
            new() { Id = "m2", Kind = MediaKind.Movie, Title = "Eta dos" }
        };

        var ranked = CatalogRanking.Rank(items, "xyz");

        Assert.Equal(["s1", "m1", "s2", "m2"], ranked.Select(i => i.Id));
    }

    [Fact]
    public async Task Search_TwoCharacterQueryIsAccepted()
    {
        var sonarr = new Mock<ISonarrClient>();
        var radarr = new Mock<IRadarrClient>();
        sonarr.Setup(s => s.LookupSeriesAsync("up", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        radarr.Setup(r => r.LookupMovieAsync("up", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RadarrMovie { Title = "Up", Year = 2009, TmdbId = 14160 }]);

        var results = await CreateService(sonarr, radarr).SearchCatalogAsync(Guid.NewGuid(), " Up ");

        Assert.Single(results);
        Assert.Equal("Up", results[0].Title);
    }

    [Fact]
    public async Task Search_MapsDisambiguationFields()
    {
        var sonarr = new Mock<ISonarrClient>();
        var radarr = new Mock<IRadarrClient>();
        sonarr.Setup(s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrSeries
            {
                Title = "Girlfriend",
                Year = 2020,
                TvdbId = 111,
                TmdbId = 222,
                ImdbId = "tt0333",
                Network = "MBS",
                Status = "continuing",
                Genres = ["Animation", "Comedy"],
                Runtime = 24,
                OriginalLanguage = new SonarrLanguage { Name = "Japanese" },
                Seasons = [new SonarrSeason { SeasonNumber = 0 }, new SonarrSeason { SeasonNumber = 1 }, new SonarrSeason { SeasonNumber = 2 }]
            }]);
        radarr.Setup(r => r.LookupMovieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var item = (await CreateService(sonarr, radarr).SearchCatalogAsync(Guid.NewGuid(), "girlfriend")).Single();

        Assert.Equal("MBS", item.Network);
        Assert.Equal("continuing", item.SeriesStatus);
        Assert.Equal(2, item.SeasonCount);
        Assert.Equal(24, item.RuntimeMinutes);
        Assert.Equal("Japanese", item.OriginalLanguage);
        Assert.True(item.IsAnime);
        Assert.Equal(
            ["https://www.themoviedb.org/tv/222", "https://thetvdb.com/?tab=series&id=111", "https://www.imdb.com/title/tt0333/"],
            item.ExternalLinks.Select(l => l.Url));
    }

    [Fact]
    public void IsAnime_RequiresAnimeGenreOrJapaneseAnimation()
    {
        Assert.True(CatalogService.IsAnime(["Anime"], null));
        Assert.True(CatalogService.IsAnime(["Animation"], "Japanese"));
        Assert.False(CatalogService.IsAnime(["Animation"], "English"));
        Assert.False(CatalogService.IsAnime(["Drama"], "Japanese"));
    }

    [Fact]
    public async Task Search_LocalSeriesStaysWithMissingEpisodes_LocalMovieIsHidden()
    {
        var sonarr = new Mock<ISonarrClient>();
        var radarr = new Mock<IRadarrClient>();
        sonarr.Setup(s => s.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrSeries
            {
                Id = 5,
                Title = "Mi serie",
                Year = 2019,
                TvdbId = 10,
                Statistics = new SonarrSeriesStatistics { EpisodeCount = 20, EpisodeFileCount = 12 }
            }]);
        radarr.Setup(r => r.LookupMovieAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RadarrMovie { Title = "Mi película", Year = 2018, TmdbId = 20 }]);

        var service = CreateService(sonarr, radarr, list =>
        {
            foreach (var i in list) { i.IsLocal = true; i.LocalItemId = Guid.NewGuid(); }
        });

        var results = await service.SearchCatalogAsync(Guid.NewGuid(), "mi");

        var series = Assert.Single(results);
        Assert.Equal("Mi serie", series.Title);
        Assert.True(series.IsLocal);
        Assert.Equal(8, series.MissingEpisodes);
    }

    [Fact]
    public void FindFreeSpace_MatchesExactRootOrContainingRoot()
    {
        var roots = new List<(string? Path, long? FreeSpace)>
        {
            ("/media/tv", 100L),
            ("/media/tv/anime", 50L),
            ("/media/movies/", 70L),
            ("/empty", null)
        };

        Assert.Equal(100L, DestinationResolver.FindFreeSpace("/media/tv/", roots));
        Assert.Equal(50L, DestinationResolver.FindFreeSpace("/media/tv/anime", roots));
        Assert.Equal(100L, DestinationResolver.FindFreeSpace("/media/tv/otros", roots));
        Assert.Equal(70L, DestinationResolver.FindFreeSpace("/media/movies", roots));
        Assert.Null(DestinationResolver.FindFreeSpace("/media/tvshows", roots));
        Assert.Null(DestinationResolver.FindFreeSpace("/empty", roots));
        Assert.Null(DestinationResolver.FindFreeSpace(null, roots));
    }

    [Fact]
    public async Task ListFreeSpace_ReadsRootFoldersAndSwallowsFailures()
    {
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrRootFolder { Path = "/tv", FreeSpace = 123 }]);
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.GetRootFoldersAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        var series = await DestinationResolver.ListFreeSpaceAsync(MediaKind.Series, sonarr.Object, radarr.Object, default);
        var movies = await DestinationResolver.ListFreeSpaceAsync(MediaKind.Movie, sonarr.Object, radarr.Object, default);

        Assert.Equal([("/tv", (long?)123)], series);
        Assert.Empty(movies);
    }

    [Theory]
    [InlineData(false, "", 1000L)]
    [InlineData(true, "/data/tv", 1000L)]
    public async Task RequestOptions_PathOnlyForAdmins_FreeSpaceForEveryone(bool admin, string expectedPath, long expectedFree)
    {
        var userId = Guid.NewGuid();
        var user = new Jellyfin.Database.Implementations.Entities.User("u", "u", "u");
        if (admin)
        {
            user.Permissions.Add(new Jellyfin.Database.Implementations.Entities.Permission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator, true));
        }

        var userManager = new Mock<MediaBrowser.Controller.Library.IUserManager>();
        userManager.Setup(u => u.GetUserById(userId)).Returns(user);

        var appPaths = new Mock<IApplicationPaths>();
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        appPaths.Setup(p => p.PluginsPath).Returns(tempDir);
        var xml = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(Configuration.PluginConfiguration), It.IsAny<string>()))
            .Returns(new Configuration.PluginConfiguration { AllowAllUsers = true });
        var plugin = new Plugin(appPaths.Object, xml.Object);
        plugin.Configuration.Destinations =
        [
            new Configuration.DestinationConfig { Name = "TV", Kind = MediaKind.Series, RootFolderPath = "/data/tv", QualityProfileId = 1, IsDefault = true }
        ];

        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrRootFolder { Id = 1, Path = "/data/tv", FreeSpace = 1000 }]);

        var controller = new Controllers.AcquisitionController(
            Mock.Of<ICatalogService>(),
            Mock.Of<IRequestRepository>(),
            Mock.Of<Tracking.ITrackerService>(),
            Mock.Of<Releases.IReleaseService>(),
            sonarr.Object,
            userManager.Object,
            Mock.Of<ILogger<Controllers.AcquisitionController>>(),
            Mock.Of<IRadarrClient>())
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim("Jellyfin-UserId", userId.ToString())], "Test"))
                }
            }
        };

        var result = await controller.GetRequestOptions(MediaKind.Series, CancellationToken.None);
        var options = Assert.IsType<RequestOptionsDto>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result).Value);

        var destination = Assert.Single(options.Destinations);
        Assert.Equal(expectedPath, destination.RootFolderPath);
        Assert.Equal(expectedFree, destination.FreeSpaceBytes);
    }

    [Fact]
    public async Task RootFolderClients_ParseFreeSpace()
    {
        const string json = "[{\"id\":1,\"path\":\"/data/tv\",\"freeSpace\":21474836480}]";

        var sonarr = new SonarrClient(new HttpClient(new StubHandler(json)), new Mock<ILogger<SonarrClient>>().Object);
        var radarr = new RadarrClient(new HttpClient(new StubHandler(json)), new Mock<ILogger<RadarrClient>>().Object);

        Assert.Equal(21474836480L, (await sonarr.GetRootFoldersAsync()).Single().FreeSpace);
        Assert.Equal(21474836480L, (await radarr.GetRootFoldersAsync()).Single().FreeSpace);
    }
}
