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

/// <summary>
/// Series with several downloads: sequential but non-blocking processing, retry of parked downloads,
/// manual release selection, episodes that have not aired yet and following an airing series.
/// </summary>
public class SeriesProgressTests
{
    private static PluginConfiguration UseConfig(PluginConfiguration config)
    {
        var appPaths = new Mock<IApplicationPaths>();
        var dir = Directory.CreateTempSubdirectory("acq-series-cfg-").FullName;
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(dir);
        appPaths.Setup(p => p.PluginsPath).Returns(dir);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(config);
        return new Plugin(appPaths.Object, xml.Object).Configuration;
    }

    private static Mock<IRequestRepository> RepoFor(AcquisitionRequest request)
    {
        var repo = new Mock<IRequestRepository>();
        repo.Setup(r => r.GetByIdAsync(request.Id, It.IsAny<CancellationToken>())).ReturnsAsync(request);
        repo.Setup(r => r.SaveOrUpdateAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return repo;
    }

    private static TrackerService Tracker(Mock<IRequestRepository> repo, Mock<ISonarrClient>? sonarr = null, Mock<IReleaseService>? releases = null,
        Mock<IProwlarrClient>? prowlarr = null, Mock<IQBittorrentClient>? qb = null)
    {
        return new TrackerService(repo.Object, (sonarr ?? new Mock<ISonarrClient>()).Object, Mock.Of<IRadarrClient>(),
            (prowlarr ?? new Mock<IProwlarrClient>()).Object, (qb ?? new Mock<IQBittorrentClient>()).Object,
            (releases ?? new Mock<IReleaseService>()).Object, Mock.Of<ILocalMatcherService>(), Mock.Of<ILibraryManager>(),
            Mock.Of<ILogger<TrackerService>>());
    }

    private static DownloadUnit Episode(int number, RequestStatus status = RequestStatus.Pending, DateTime? airDate = null) => new()
    {
        Id = $"u{number}",
        SeasonNumber = 1,
        EpisodeNumbers = [number],
        EpisodeIds = [number],
        AirDateUtc = airDate,
        Status = status
    };

    private static AcquisitionRequest Series(params DownloadUnit[] units) => new()
    {
        Id = "req-series",
        Kind = MediaKind.Series,
        Title = "Frieren",
        ArrId = 1,
        Category = "tv-sonarr",
        Status = RequestStatus.Searching,
        ProviderIds = new ProviderIds { Tvdb = "424536" },
        Units = [.. units]
    };

    private static ReleaseCandidate Candidate(string title) => new() { Id = title, Title = title, DownloadUrl = "http://prowlarr/dl", IsTorrent = true, IsEligible = true, IdentityVerified = true, Seeders = 10 };

    private static Mock<IReleaseService> ReleasesReturning(Func<DownloadUnit, List<ReleaseCandidate>> byUnit)
    {
        var releases = new Mock<IReleaseService>();
        releases.Setup(r => r.SearchAndEvaluateReleasesForUnitAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<DownloadUnit>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AcquisitionRequest _, DownloadUnit u, CancellationToken _) => byUnit(u));
        releases.Setup(r => r.PickBestRelease(It.IsAny<IEnumerable<ReleaseCandidate>>()))
            .Returns((IEnumerable<ReleaseCandidate> c) => c.FirstOrDefault(x => x.IsEligible));
        return releases;
    }

    // ---- Sequential, non-blocking ----

    [Fact]
    public async Task FailingEpisode_IsSetAside_AndTheNextOneContinues()
    {
        UseConfig(new PluginConfiguration());
        var e4 = Episode(4, RequestStatus.Searching);
        e4.SelectedRelease = Candidate("Frieren - 04");
        e4.FetchFailures = 2; // third failure now
        var req = Series(e4, Episode(5), Episode(6));
        var prowlarr = new Mock<IProwlarrClient>();
        prowlarr.Setup(p => p.FetchTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TorrentDownloadResult { ErrorMessage = "HTTP 404" });
        var qb = new Mock<IQBittorrentClient>();
        qb.Setup(q => q.GetTorrentsInfoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await Tracker(RepoFor(req), prowlarr: prowlarr, qb: qb).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.Searching, req.Status);
        Assert.Equal(RequestStatus.RequiresAttention, e4.Status);
        Assert.Equal("HTTP 404", e4.ErrorMessage);
        Assert.Contains("S01E04 apartado para revisión", req.History.Last().Message);
        Assert.Contains("S01E05", req.History.Last().Message);
        Assert.Equal("u5", req.GetActiveUnit()!.Id);
        Assert.Null(req.AttentionReason);
    }

    [Fact]
    public async Task EpisodeWithoutReleases_IsSetAsideAfterThreeSearches_ButTheLastOneKeepsRetrying()
    {
        UseConfig(new PluginConfiguration());
        var releases = ReleasesReturning(_ => []);
        var e5 = Episode(5);
        e5.SearchAttempts = 2;
        var req = Series(e5, Episode(6));

        await Tracker(RepoFor(req), releases: releases).ProcessRequestAsync(req);
        Assert.Equal(RequestStatus.RequiresAttention, e5.Status);
        Assert.Equal(RequestStatus.Searching, req.Status);

        // E06 is now the only one left: it keeps retrying instead of being set aside
        var e6 = req.Units[1];
        e6.SearchAttempts = 5;
        req.NextAttemptUtc = null;
        await Tracker(RepoFor(req), releases: releases).ProcessRequestAsync(req);
        Assert.Equal(RequestStatus.Pending, e6.Status);
        Assert.True(req.NextAttemptUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task WhenOnlyParkedDownloadsRemain_TheRequestNeedsAttentionWithASummary()
    {
        UseConfig(new PluginConfiguration());
        var e4 = Episode(4, RequestStatus.RequiresAttention);
        e4.ErrorMessage = "HTTP 404";
        var req = Series(Episode(3, RequestStatus.Available), e4);

        await Tracker(RepoFor(req)).ProcessRequestAsync(req);

        Assert.Equal(RequestStatus.RequiresAttention, req.Status);
        Assert.Equal("S01E04: HTTP 404", req.AttentionReason);
    }

    [Fact]
    public async Task Retry_RequeuesEveryParkedDownload_WithoutInterruptingTheRequest()
    {
        var e4 = Episode(4, RequestStatus.RequiresAttention);
        var e6 = Episode(6, RequestStatus.RequiresAttention);
        e6.SearchAttempts = 3;
        var req = Series(e4, Episode(5, RequestStatus.Downloading), e6);

        var result = await Tracker(RepoFor(req)).RetryRequestAsync(req.Id, _ => true);

        Assert.Equal(RetryOutcome.Success, result.Outcome);
        Assert.All(new[] { e4, e6 }, u => Assert.Equal(RequestStatus.Pending, u.Status));
        Assert.Equal(0, e6.SearchAttempts);
        Assert.Equal(RequestStatus.Searching, req.Status);
        Assert.Equal(RequestStatus.Downloading, req.Units[1].Status);
    }

    // ---- Manual release selection ----

    [Fact]
    public async Task ManualMode_StoresTheListAndWaits_WithoutSendingOrSearchingAgain()
    {
        UseConfig(new PluginConfiguration { ReleaseSelectionMode = "manual" });
        var releases = ReleasesReturning(_ => [Candidate("Frieren - 05")]);
        var qb = new Mock<IQBittorrentClient>();
        var req = Series(Episode(5));

        await Tracker(RepoFor(req), releases: releases, qb: qb).ProcessRequestAsync(req);
        await Tracker(RepoFor(req), releases: releases, qb: qb).ProcessRequestAsync(req);

        Assert.Null(req.Units[0].SelectedRelease);
        Assert.Single(req.AvailableReleases);
        Assert.Contains("elige una", req.History.Last().Message);
        releases.Verify(r => r.SearchAndEvaluateReleasesForUnitAsync(It.IsAny<AcquisitionRequest>(), It.IsAny<DownloadUnit>(), It.IsAny<CancellationToken>()), Times.Once);
        qb.Verify(q => q.AddTorrentAsync(It.IsAny<QBittorrentAddOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManualChoice_ForAParkedDownload_PutsItBackInTheQueue()
    {
        var config = UseConfig(new PluginConfiguration { AllowAllUsers = true });
        var userId = Guid.NewGuid();
        var parked = Episode(4, RequestStatus.RequiresAttention);
        parked.ErrorMessage = "HTTP 404";
        var req = Series(Episode(3, RequestStatus.Available), parked);
        req.UserId = userId;
        req.Status = RequestStatus.RequiresAttention;
        req.AvailableReleases = [Candidate("Frieren - 04 v2")];
        req.AvailableReleasesUnitId = "u4";
        var controller = Controller(RepoFor(req), userId);

        var result = await controller.SelectRelease(req.Id, new SelectReleaseDto { ReleaseId = "Frieren - 04 v2" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(RequestStatus.Pending, parked.Status);
        Assert.Equal("Frieren - 04 v2", parked.SelectedRelease!.Title);
        Assert.Equal(RequestStatus.Searching, req.Status);
        Assert.False(config.IsManualReleaseSelection());
    }

    // ---- Airing series ----

    [Fact]
    public async Task EpisodeChosenBeforeItAirs_WaitsForItsAirDatePlusTheMargin()
    {
        UseConfig(new PluginConfiguration());
        var airs = DateTime.UtcNow.AddDays(2);
        var releases = ReleasesReturning(_ => [Candidate("x")]);
        var req = Series(Episode(8, airDate: airs));

        await Tracker(RepoFor(req), releases: releases).ProcessRequestAsync(req);

        releases.VerifyNoOtherCalls();
        Assert.Equal(airs + DownloadUnit.ReleaseGrace, req.NextAttemptUtc!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Contains("Esperando la emisión de S01E08", req.History.Last().Message);
    }

    [Fact]
    public async Task FullSeries_SkipsFutureAndToBeAnnouncedEpisodes_AndFollowsTheSeries()
    {
        UseConfig(new PluginConfiguration());
        var now = DateTime.UtcNow;
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, AirDateUtc = now.AddDays(-14), HasFile = true },
            new SonarrEpisode { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, AirDateUtc = now.AddDays(-7) },
            new SonarrEpisode { Id = 3, SeasonNumber = 1, EpisodeNumber = 3, AirDateUtc = now.AddMinutes(-20) }, // aired, still within the margin
            new SonarrEpisode { Id = 4, SeasonNumber = 1, EpisodeNumber = 4, AirDateUtc = now.AddDays(7) },
            new SonarrEpisode { Id = 5, SeasonNumber = 1, EpisodeNumber = 5 }                                   // to be announced
        ]);
        var req = Series();
        req.Status = RequestStatus.Pending;
        req.FollowAiring = true;
        req.Scope = new RequestScope { All = true };

        await Tracker(RepoFor(req), sonarr, ReleasesReturning(_ => [])).ProcessRequestAsync(req);

        Assert.Equal([2], req.Scope.TargetEpisodes.Select(t => t.EpisodeId));
        Assert.Single(req.Units);
    }

    [Fact]
    public async Task FollowedSeries_AddsNewlyAiredEpisodes()
    {
        UseConfig(new PluginConfiguration());
        var now = DateTime.UtcNow;
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, AirDateUtc = now.AddDays(-7), HasFile = true },
            new SonarrEpisode { Id = 3, SeasonNumber = 1, EpisodeNumber = 3, AirDateUtc = now.AddHours(-3) },
            new SonarrEpisode { Id = 4, SeasonNumber = 1, EpisodeNumber = 4, AirDateUtc = now.AddDays(7) }
        ]);
        var req = Series(Episode(2, RequestStatus.Available));
        req.Status = RequestStatus.Available;
        req.FollowAiring = true;
        req.Scope = new RequestScope { All = true, TargetEpisodes = [new EpisodeTarget { EpisodeId = 2, SeasonNumber = 1, EpisodeNumber = 2 }] };

        var repo = RepoFor(req);
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([req]);
        await Tracker(repo, sonarr).PollActiveRequestsAsync();

        Assert.Equal(RequestStatus.Searching, req.Status);
        var added = Assert.Single(req.Units, u => u.Status == RequestStatus.Pending);
        Assert.Equal([3], added.EpisodeIds);
        Assert.Contains("S01E03", req.History.Last().Message);
    }

    [Fact]
    public async Task FollowedSeries_StopsWhenTheSeriesHasEndedAndNothingIsLeftToAir()
    {
        UseConfig(new PluginConfiguration());
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([
            new SonarrEpisode { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, AirDateUtc = DateTime.UtcNow.AddDays(-7), HasFile = true }
        ]);
        sonarr.Setup(s => s.GetSeriesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new SonarrSeries { Id = 1, Status = "ended" });
        var req = Series(Episode(2, RequestStatus.Available));
        req.Status = RequestStatus.Available;
        req.FollowAiring = true;

        await Tracker(RepoFor(req), sonarr).ProcessRequestAsync(req);

        Assert.False(req.FollowAiring);
        Assert.False(req.IsActive);
        Assert.Contains("se deja de seguir", req.History.Last().Message);
    }

    [Fact]
    public async Task FollowedSeries_CountsAsActive_ForDuplicatesAndCanBeCancelled()
    {
        var repoDir = Directory.CreateTempSubdirectory("acq-follow-repo-").FullName;
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(repoDir);
        var repo = new JsonFileRequestRepository(appPaths.Object, Mock.Of<ILogger<JsonFileRequestRepository>>());
        var req = Series(Episode(2, RequestStatus.Available));
        req.Status = RequestStatus.Available;
        req.FollowAiring = true;
        await repo.SaveOrUpdateAsync(req);

        Assert.NotNull(await repo.FindActiveByExternalIdAsync(MediaKind.Series, "tvdb", "424536"));

        var tracker = new TrackerService(repo, Mock.Of<ISonarrClient>(), Mock.Of<IRadarrClient>(), Mock.Of<IProwlarrClient>(), Mock.Of<IQBittorrentClient>(),
            Mock.Of<IReleaseService>(), Mock.Of<ILocalMatcherService>(), Mock.Of<ILibraryManager>(), Mock.Of<ILogger<TrackerService>>());
        Assert.Equal(RetryOutcome.Success, (await tracker.CancelRequestAsync(req.Id, _ => true)).Outcome);
        Assert.Null(await repo.GetByIdAsync(req.Id));
    }

    // ---- Summary shown in the modal ----

    [Fact]
    public async Task RequestDto_SummarisesTheDownloads()
    {
        UseConfig(new PluginConfiguration { AllowAllUsers = true });
        var userId = Guid.NewGuid();
        var parked = Episode(4, RequestStatus.RequiresAttention);
        parked.ErrorMessage = "HTTP 404";
        var req = Series(Episode(3, RequestStatus.Available), parked, Episode(5, RequestStatus.Downloading), Episode(6), Episode(7, airDate: DateTime.UtcNow.AddDays(3)));
        req.UserId = userId;
        req.FollowAiring = true;

        var dto = Assert.IsType<AcquisitionRequestDto>(Assert.IsType<OkObjectResult>((await Controller(RepoFor(req), userId).GetRequest(req.Id, CancellationToken.None)).Result).Value);

        Assert.Equal(5, dto.Progress.Total);
        Assert.Equal(1, dto.Progress.Available);
        Assert.Equal(1, dto.Progress.InProgress);
        Assert.Equal(2, dto.Progress.Pending);
        Assert.Equal(1, dto.Progress.Attention);
        Assert.Equal("S01E05", dto.Progress.Current);
        Assert.Equal("S01E04", Assert.Single(dto.Progress.AttentionItems).Label);
        Assert.True(dto.Progress.Following);
    }

    private static AcquisitionController Controller(Mock<IRequestRepository> repo, Guid userId)
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(userId)).Returns(new Jellyfin.Database.Implementations.Entities.User("user", "user", "user"));
        var tracker = new Mock<ITrackerService>();
        tracker.Setup(t => t.AcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Mock.Of<IDisposable>());
        return new AcquisitionController(Mock.Of<ICatalogService>(), repo.Object, tracker.Object, Mock.Of<IReleaseService>(), Mock.Of<ISonarrClient>(),
            users.Object, Mock.Of<ILogger<AcquisitionController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString())], "Test")) }
            }
        };
    }
}
