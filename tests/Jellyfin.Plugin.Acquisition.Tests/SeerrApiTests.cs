using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr.Models;
using Jellyfin.Plugin.Acquisition.Catalog;
using Jellyfin.Plugin.Acquisition.Models;
using Jellyfin.Plugin.Acquisition.Seerr;
using Jellyfin.Plugin.Acquisition.Storage;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class SeerrApiTests : IDisposable
{
    private readonly string _testDir;
    private readonly Mock<IApplicationPaths> _appPaths = new();

    public SeerrApiTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Acquisition_Seerr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ParseSeasons_AllMeansNull_ListIsSortedPositiveDistinct()
    {
        Assert.Null(SeerrBridge.ParseSeasons(Json("\"all\"")));
        Assert.Null(SeerrBridge.ParseSeasons(default));
        Assert.Equal([1, 2, 4], SeerrBridge.ParseSeasons(Json("[4, 2, 0, 2, 1, -3]")));
        Assert.Empty(SeerrBridge.ParseSeasons(Json("[]"))!);
    }

    [Fact]
    public void BuildSeasons_UsesSonarrEpisodesWhenRegistered_ElseSkyHook_NeverSpecials()
    {
        var sonarr = new List<SonarrEpisode>
        {
            new() { SeasonNumber = 0, EpisodeNumber = 1 },
            new() { SeasonNumber = 1, EpisodeNumber = 1, AirDateUtc = new DateTime(2020, 1, 8) },
            new() { SeasonNumber = 1, EpisodeNumber = 2, AirDateUtc = new DateTime(2020, 1, 1) },
            new() { SeasonNumber = 2, EpisodeNumber = 1 }
        };
        var fromSonarr = SeerrBridge.BuildSeasons(sonarr, null)!;
        Assert.Equal([1, 2], fromSonarr.Select(s => s.SeasonNumber));
        Assert.Equal([2, 1], fromSonarr.Select(s => s.EpisodeCount));
        Assert.Equal("2020-01-01", fromSonarr[0].AirDate);
        Assert.Null(fromSonarr[1].AirDate);

        var show = new SkyHookShow
        {
            Episodes =
            [
                new SkyHookEpisode { SeasonNumber = 0, EpisodeNumber = 1 },
                new SkyHookEpisode { SeasonNumber = 3, EpisodeNumber = 1, AirDate = "2021-05-02" },
                new SkyHookEpisode { SeasonNumber = 3, EpisodeNumber = 2, AirDate = "2021-05-09" }
            ]
        };
        var fromSkyHook = SeerrBridge.BuildSeasons(null, show)!;
        Assert.Single(fromSkyHook);
        Assert.Equal(3, fromSkyHook[0].SeasonNumber);
        Assert.Equal(2, fromSkyHook[0].EpisodeCount);
        Assert.Equal("2021-05-02", fromSkyHook[0].AirDate);

        Assert.Null(SeerrBridge.BuildSeasons(null, null));
    }

    [Fact]
    public void SeasonAvailability_OnlyCompleteSeasonsAreAvailable()
    {
        var episodes = new List<SonarrEpisode>
        {
            new() { SeasonNumber = 1, EpisodeNumber = 1, HasFile = true },
            new() { SeasonNumber = 1, EpisodeNumber = 2, HasFile = true },
            new() { SeasonNumber = 2, EpisodeNumber = 1, HasFile = true },
            new() { SeasonNumber = 2, EpisodeNumber = 2, HasFile = false }
        };
        var seasons = SeerrBridge.BuildSeasons(episodes, null)!;
        var status = SeerrBridge.SeasonAvailability(episodes, seasons);

        Assert.Equal(SeerrMediaStatus.Available, status[1]);
        // Partially downloaded seasons stay requestable: the plugin only fetches the missing episodes
        Assert.Equal(SeerrMediaStatus.Unknown, status[2]);
        Assert.Equal(SeerrMediaStatus.PartiallyAvailable, SeerrBridge.MediaStatus(status, hasSomething: true, SeerrMediaStatus.Unknown));
        Assert.Equal(SeerrMediaStatus.Available, SeerrBridge.MediaStatus(new Dictionary<int, int> { [1] = SeerrMediaStatus.Available }, true, SeerrMediaStatus.Unknown));
        Assert.Equal(SeerrMediaStatus.Processing, SeerrBridge.MediaStatus(new Dictionary<int, int> { [1] = SeerrMediaStatus.Unknown }, false, SeerrMediaStatus.Processing));
    }

    [Fact]
    public void BuildMediaInfo_MapsRequestsAndCoveredSeasons()
    {
        var active = new AcquisitionRequest { Kind = MediaKind.Series, Status = RequestStatus.Downloading, Scope = new RequestScope { All = false, SeasonNumbers = [2] } };
        var failed = new AcquisitionRequest { Kind = MediaKind.Series, Status = RequestStatus.Failed, Scope = new RequestScope { All = true } };
        var seasons = new Dictionary<int, int> { [1] = SeerrMediaStatus.Available, [2] = SeerrMediaStatus.Unknown, [3] = SeerrMediaStatus.Unknown };

        var info = SeerrBridge.BuildMediaInfo(1396, 81189, MediaKind.Series, [active, failed], seasons);

        Assert.Equal(SeerrMediaStatus.Processing, info.Status);
        Assert.Equal([SeerrMediaStatus.Available, SeerrMediaStatus.Processing, SeerrMediaStatus.Unknown], info.Seasons!.Select(s => s.Status));
        Assert.Equal(SeerrRequestStatus.Approved, info.Requests![0].Status);
        Assert.Equal([2], info.Requests[0].Seasons!.Select(s => s.SeasonNumber));
        Assert.Equal(SeerrRequestStatus.Failed, info.Requests[1].Status);
        Assert.Equal([1, 2, 3], info.Requests[1].Seasons!.Select(s => s.SeasonNumber));
        Assert.Equal(SeerrBridge.StableId(active.Id), info.Requests[0].Id);
        Assert.True(info.Requests[0].Id > 0);
    }

    [Fact]
    public void Json_UsesSeerrWireNames()
    {
        var details = new SeerrDetailsDto
        {
            Id = 1,
            Name = "X",
            ContentRatings = new SeerrCountryRatingsDto { Results = [new SeerrCountryRatingDto { Rating = "TV-MA" }] },
            MediaInfo = new SeerrMediaInfoDto { TmdbId = 1, Status = SeerrMediaStatus.Available }
        };
        var json = JsonSerializer.Serialize(details, SeerrJson.Options);

        Assert.Contains("\"name\":\"X\"", json);
        Assert.Contains("\"iso_3166_1\":\"US\"", json);
        Assert.Contains("\"mediaInfo\":{", json);
        Assert.Contains("\"status4k\":1", json);
        Assert.DoesNotContain("posterPath", json); // nulls are left out, as in Seerr

        var settings = JsonSerializer.Serialize(new SeerrPublicSettingsDto(), SeerrJson.Options);
        Assert.Contains("\"mediaServerType\":2", settings);
        Assert.Contains("\"youtubeUrl\":\"\"", settings);
        Assert.Contains("\"localLogin\":false", settings);
        Assert.Contains("\"initialized\":true", settings);
    }

    [Fact]
    public void TmdbMapPage_DropsPeopleAndFillsMediaType()
    {
        var page = TmdbClient.MapPage(Json("""
        {"page":2,"total_pages":5,"total_results":90,"results":[
          {"id":10,"media_type":"movie","title":"M","poster_path":"/m.jpg","release_date":"2024-03-01","vote_average":7.5},
          {"id":11,"media_type":"person","name":"P"},
          {"id":12,"name":"T","first_air_date":"2023-01-01","origin_country":["US"]}
        ]}
        """), "tv");

        Assert.Equal(2, page.Page);
        Assert.Equal(5, page.TotalPages);
        Assert.Equal(["movie", "tv"], page.Results.Select(r => r.MediaType));
        Assert.Equal("/m.jpg", page.Results[0].PosterPath);
        Assert.Equal(["US"], page.Results[1].OriginCountry!);
    }

    [Fact]
    public async Task SessionStore_CreatesPersistsAndRemovesSessions()
    {
        var userId = Guid.NewGuid();
        var store = new JsonFileSeerrSessionStore(_appPaths.Object, Mock.Of<ILogger<JsonFileSeerrSessionStore>>());
        var token = await store.CreateAsync(userId);

        Assert.Equal(userId, await store.GetUserIdAsync(token));
        Assert.Null(await store.GetUserIdAsync("wrong"));
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(_testDir, "Acquisition", "seerr-sessions.json")));

        // A new instance (Jellyfin restart) still knows the session
        var reloaded = new JsonFileSeerrSessionStore(_appPaths.Object, Mock.Of<ILogger<JsonFileSeerrSessionStore>>());
        Assert.Equal(userId, await reloaded.GetUserIdAsync(token));

        await reloaded.RemoveAsync(token);
        Assert.Null(await reloaded.GetUserIdAsync(token));
    }

    [Fact]
    public void LoginThrottle_BlocksAfterTooManyFailures()
    {
        var throttle = new SeerrLoginThrottle();
        var address = System.Net.IPAddress.Parse("10.0.0.5");
        for (var i = 0; i < SeerrLoginThrottle.MaxFailures - 1; i++) throttle.RecordFailure(address);
        Assert.False(throttle.IsBlocked(address));

        throttle.RecordFailure(address);
        Assert.True(throttle.IsBlocked(address));
        Assert.False(throttle.IsBlocked(System.Net.IPAddress.Parse("10.0.0.6")));

        throttle.Reset(address);
        Assert.False(throttle.IsBlocked(address));
    }

    private (SeerrBridge Bridge, JsonFileRequestRepository Repo, Mock<ISonarrClient> Sonarr) CreateBridge()
    {
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync("tmdb:1396", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult { Items = [new SonarrSeries { Title = "Breaking Bad", Year = 2008, TvdbId = 81189, TmdbId = 1396, ImdbId = "tt0903747" }] });
        sonarr.Setup(s => s.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SonarrRootFolder { Id = 1, Path = "/tv" }]);

        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.LookupMovieWithDiagnosticsAsync("tmdb:603", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrLookupResult { Items = [new RadarrMovie { Title = "The Matrix", Year = 1999, TmdbId = 603 }] });
        radarr.Setup(r => r.GetRootFoldersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RadarrRootFolder { Id = 1, Path = "/movies" }]);

        var repo = new JsonFileRequestRepository(_appPaths.Object, Mock.Of<ILogger<JsonFileRequestRepository>>());
        var bridge = new SeerrBridge(
            sonarr.Object,
            radarr.Object,
            Mock.Of<ICatalogService>(),
            Mock.Of<ILocalMatcherService>(),
            repo,
            Mock.Of<ISkyHookClient>(),
            Mock.Of<ILogger<SeerrBridge>>());
        return (bridge, repo, sonarr);
    }

    [Fact]
    public async Task CreateRequest_WholeSeries_FollowsAiringWithAutomaticSelection()
    {
        var (bridge, repo, _) = CreateBridge();
        var userId = Guid.NewGuid();

        var created = await bridge.CreateRequestAsync(userId, new SeerrCreateRequestDto { MediaType = "tv", MediaId = 1396, Seasons = Json("\"all\"") });

        var request = Assert.Single(await repo.GetAllAsync());
        Assert.Equal(userId, request.UserId);
        Assert.Equal(MediaKind.Series, request.Kind);
        Assert.Equal("81189", request.ProviderIds.Tvdb);
        Assert.Equal("1396", request.ProviderIds.Tmdb);
        Assert.True(request.Scope.All);
        Assert.True(request.FollowAiring);
        Assert.True(request.AutoReleaseSelection);
        Assert.Equal("/tv", request.RootFolderPath);
        Assert.Equal(RequestStatus.Pending, request.Status);
        Assert.Equal(SeerrRequestStatus.Approved, created.Status);
        Assert.Equal(81189, created.Media!.TvdbId);
    }

    [Fact]
    public async Task CreateRequest_SelectedSeasons_UsesTvdbSeasonNumbersAsSent()
    {
        var (bridge, repo, _) = CreateBridge();

        await bridge.CreateRequestAsync(Guid.NewGuid(), new SeerrCreateRequestDto { MediaType = "tv", MediaId = 1396, Seasons = Json("[3, 1]") });

        var request = Assert.Single(await repo.GetAllAsync());
        Assert.False(request.Scope.All);
        Assert.False(request.FollowAiring);
        Assert.Equal([1, 3], request.Scope.SeasonNumbers);
    }

    [Fact]
    public async Task CreateRequest_Movie_RejectsDuplicatesAnd4k()
    {
        var (bridge, repo, _) = CreateBridge();
        var userId = Guid.NewGuid();

        await bridge.CreateRequestAsync(userId, new SeerrCreateRequestDto { MediaType = "movie", MediaId = 603 });
        var movie = Assert.Single(await repo.GetAllAsync());
        Assert.True(movie.Scope.All);
        Assert.Equal("603", movie.ProviderIds.Tmdb);

        var duplicate = await Assert.ThrowsAsync<SeerrApiException>(() =>
            bridge.CreateRequestAsync(userId, new SeerrCreateRequestDto { MediaType = "movie", MediaId = 603 }));
        Assert.Equal(409, duplicate.StatusCode);

        var fourK = await Assert.ThrowsAsync<SeerrApiException>(() =>
            bridge.CreateRequestAsync(userId, new SeerrCreateRequestDto { MediaType = "movie", MediaId = 603, Is4k = true }));
        Assert.Equal(400, fourK.StatusCode);

        var noSeasons = await Assert.ThrowsAsync<SeerrApiException>(() =>
            bridge.CreateRequestAsync(userId, new SeerrCreateRequestDto { MediaType = "tv", MediaId = 1396, Seasons = Json("[]") }));
        Assert.Equal(400, noSeasons.StatusCode);
    }

    [Fact]
    public async Task Search_KeepsLibraryTitlesMarkedAvailable_WebSearchStillHidesThem()
    {
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync("breaking bad", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult { Items = [new SonarrSeries { Title = "Breaking Bad", Year = 2008, TvdbId = 81189, TmdbId = 1396 }] });
        var radarr = new Mock<IRadarrClient>();
        radarr.Setup(r => r.LookupMovieWithDiagnosticsAsync("breaking bad", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RadarrLookupResult());
        var matcher = new Mock<ILocalMatcherService>();
        matcher.Setup(m => m.MatchBatch(It.IsAny<Guid>(), It.IsAny<List<Controllers.Dtos.CatalogItemDto>>()))
            .Callback<Guid, List<Controllers.Dtos.CatalogItemDto>>((_, items) => items.ForEach(i => i.IsLocal = true));
        var repo = new JsonFileRequestRepository(_appPaths.Object, Mock.Of<ILogger<JsonFileRequestRepository>>());
        var catalog = new CatalogService(sonarr.Object, radarr.Object, matcher.Object, repo, Mock.Of<IHttpClientFactory>(), _appPaths.Object, Mock.Of<ILogger<CatalogService>>());
        var bridge = new SeerrBridge(sonarr.Object, radarr.Object, catalog, matcher.Object, repo, Mock.Of<ISkyHookClient>(), Mock.Of<ILogger<SeerrBridge>>());

        var page = await bridge.SearchAsync(Guid.NewGuid(), "breaking bad", 1);

        var item = Assert.Single(page.Results);
        Assert.Equal(1396, item.Id);
        Assert.Equal("tv", item.MediaType);
        Assert.Equal("2008", item.FirstAirDate);
        Assert.Equal(SeerrMediaStatus.Available, item.MediaInfo!.Status);
        Assert.Empty((await catalog.SearchCatalogWithDiagnosticsAsync(Guid.NewGuid(), "breaking bad")).Items);
    }

    [Fact]
    public async Task CreateRequest_SonarrDown_IsServiceUnavailable()
    {
        var (bridge, _, sonarr) = CreateBridge();
        sonarr.Setup(s => s.LookupSeriesWithDiagnosticsAsync("tmdb:1396", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SonarrLookupResult { Status = ProviderStatus.Unavailable, ErrorMessage = "Sonarr caído" });

        var ex = await Assert.ThrowsAsync<SeerrApiException>(() =>
            bridge.CreateRequestAsync(Guid.NewGuid(), new SeerrCreateRequestDto { MediaType = "tv", MediaId = 1396, Seasons = Json("\"all\"") }));
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("Sonarr caído", ex.Message);
    }
}
