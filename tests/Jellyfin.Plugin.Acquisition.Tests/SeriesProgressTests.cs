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

    // ---- One request per series: more seasons are added to the one in progress ----

    private static SonarrEpisode Ep(int id, int season, int episode, bool hasFile = false, int airedDaysAgo = 30) =>
        new() { Id = id, SeasonNumber = season, EpisodeNumber = episode, HasFile = hasFile, AirDateUtc = DateTime.UtcNow.AddDays(-airedDaysAgo) };

    private static Mock<ISonarrClient> SonarrWith(params SonarrEpisode[] episodes)
    {
        var sonarr = new Mock<ISonarrClient>();
        sonarr.Setup(s => s.GetEpisodesAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([.. episodes]);
        return sonarr;
    }

    [Fact]
    public async Task AddSeasons_WhileTheLastSeasonDownloads_QueuesThemWithoutInterrupting()
    {
        // Requested only season 3, which is downloading; seasons 1 and 2 are added afterwards
        var downloading = new DownloadUnit { Id = "s3", SeasonNumber = 3, EpisodeNumbers = [1, 2], EpisodeIds = [31, 32], IsFullSeasonPack = true, Status = RequestStatus.Downloading };
        var req = Series(downloading);
        req.Status = RequestStatus.Downloading;
        req.Scope = new RequestScope { SeasonNumbers = [3] };
        var sonarr = SonarrWith(Ep(11, 1, 1), Ep(12, 1, 2), Ep(21, 2, 1), Ep(22, 2, 2, hasFile: true), Ep(31, 3, 1), Ep(32, 3, 2));

        var result = await Tracker(RepoFor(req), sonarr).AddToRequestAsync(req.Id, false, [1, 2], [], _ => true);

        Assert.Equal(AddScopeOutcome.Added, result.Outcome);
        Assert.Equal(RequestStatus.Downloading, req.Status);
        Assert.Equal([1, 2, 3], req.Scope.SeasonNumbers);
        Assert.Equal(3, req.Units.Count);
        Assert.Same(downloading, req.GetActiveUnit());
        var season1 = req.Units.Single(u => u.SeasonNumber == 1);
        Assert.True(season1.IsFullSeasonPack);
        Assert.Equal([11, 12], season1.EpisodeIds);
        // Season 2 already has E02: only the missing episode, one by one, never a pack of the whole season
        var season2 = req.Units.Single(u => u.SeasonNumber == 2);
        Assert.False(season2.IsFullSeasonPack);
        Assert.Equal([21], season2.EpisodeIds);
        Assert.Contains("Temporada 1", req.History.Last().Message);
    }

    [Fact]
    public async Task AddSeasons_GoAfterWhatWasAlreadyQueued()
    {
        var req = Series(
            new DownloadUnit { Id = "s3e1", SeasonNumber = 3, EpisodeNumbers = [1], EpisodeIds = [31], Status = RequestStatus.Downloading },
            new DownloadUnit { Id = "s3e2", SeasonNumber = 3, EpisodeNumbers = [2], EpisodeIds = [32], Status = RequestStatus.Pending });
        req.Status = RequestStatus.Downloading;
        req.Scope = new RequestScope { SeasonNumbers = [3] };
        var sonarr = SonarrWith(Ep(11, 1, 1), Ep(31, 3, 1), Ep(32, 3, 2));

        await Tracker(RepoFor(req), sonarr).AddToRequestAsync(req.Id, false, [1], [], _ => true);

        // The season asked for first keeps its turn; the added one follows
        Assert.Equal(["s3e1", "s3e2"], req.Units.Take(2).Select(u => u.Id));
        Assert.Equal(11, req.Units[2].EpisodeIds.Single());
    }

    [Fact]
    public async Task AddSeasons_WhenNothingIsInProgress_StartsSearchingThem_AndEarlierSeasonsGoFirst()
    {
        // Season 2 finished except one episode set aside and one not aired yet
        var parked = new DownloadUnit { Id = "s2e1", SeasonNumber = 2, EpisodeNumbers = [1], EpisodeIds = [21], Status = RequestStatus.RequiresAttention };
        var future = new DownloadUnit { Id = "s2e3", SeasonNumber = 2, EpisodeNumbers = [3], EpisodeIds = [23], AirDateUtc = DateTime.UtcNow.AddDays(5), Status = RequestStatus.Pending };
        var req = Series(new DownloadUnit { Id = "s2e2", SeasonNumber = 2, EpisodeNumbers = [2], EpisodeIds = [22], Status = RequestStatus.Available }, parked, future);
        req.Status = RequestStatus.RequiresAttention;
        req.Scope = new RequestScope { SeasonNumbers = [2] };
        req.ImportingStartedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var sonarr = SonarrWith(Ep(11, 1, 1), Ep(21, 2, 1), Ep(22, 2, 2, hasFile: true), Ep(23, 2, 3, airedDaysAgo: -5));

        var result = await Tracker(RepoFor(req), sonarr).AddToRequestAsync(req.Id, false, [1], [], _ => true);

        Assert.Equal(AddScopeOutcome.Added, result.Outcome);
        Assert.Equal(RequestStatus.Searching, req.Status);
        Assert.Null(req.ImportingStartedAt);
        // The new season 1 is not held behind S02E03, which has not aired yet
        Assert.Equal(11, req.GetActiveUnit()!.EpisodeIds.Single());
        Assert.Equal(RequestStatus.RequiresAttention, parked.Status);
    }

    [Fact]
    public async Task AddWholeSeries_FollowsAiringAndAddsEverythingAiredThatIsMissing()
    {
        var req = Series(new DownloadUnit { Id = "s2", SeasonNumber = 2, EpisodeNumbers = [1], EpisodeIds = [21], Status = RequestStatus.Available });
        req.Status = RequestStatus.Importing;
        req.Scope = new RequestScope { SeasonNumbers = [2] };
        var sonarr = SonarrWith(Ep(11, 1, 1), Ep(21, 2, 1, hasFile: true), Ep(22, 2, 2, airedDaysAgo: -3));

        var result = await Tracker(RepoFor(req), sonarr).AddToRequestAsync(req.Id, true, [], [], _ => true);

        Assert.Equal(AddScopeOutcome.Added, result.Outcome);
        Assert.True(req.Scope.All);
        Assert.True(req.FollowAiring);
        // Aired and missing now; S02E02 airs later and comes with following
        Assert.Equal([11], req.Units.Where(u => u.Status == RequestStatus.Pending).SelectMany(u => u.EpisodeIds));
        Assert.Equal(RequestStatus.Searching, req.Status);
    }

    [Fact]
    public async Task AddSeasons_AlreadyRequestedOrWholeSeries_IsReportedAsCovered()
    {
        var req = Series(new DownloadUnit { Id = "s1", SeasonNumber = 1, EpisodeNumbers = [1], EpisodeIds = [11], Status = RequestStatus.Downloading });
        req.Status = RequestStatus.Downloading;
        req.Scope = new RequestScope { SeasonNumbers = [1] };
        var sonarr = SonarrWith(Ep(11, 1, 1));

        Assert.Equal(AddScopeOutcome.AlreadyCovered, (await Tracker(RepoFor(req), sonarr).AddToRequestAsync(req.Id, false, [1], [], _ => true)).Outcome);
        Assert.Single(req.Units);

        req.Scope.All = true;
        req.FollowAiring = true;
        Assert.Equal(AddScopeOutcome.AlreadyCovered, (await Tracker(RepoFor(req), sonarr).AddToRequestAsync(req.Id, false, [2], [], _ => true)).Outcome);
    }

    [Fact]
    public async Task AddSeasons_ToAFinishedRequest_IsRejected()
    {
        var req = Series(new DownloadUnit { Id = "s1", SeasonNumber = 1, EpisodeNumbers = [1], EpisodeIds = [11], Status = RequestStatus.Available });
        req.Status = RequestStatus.Available;

        var result = await Tracker(RepoFor(req), SonarrWith(Ep(21, 2, 1))).AddToRequestAsync(req.Id, false, [2], [], _ => true);

        Assert.Equal(AddScopeOutcome.Invalid, result.Outcome);
    }

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
